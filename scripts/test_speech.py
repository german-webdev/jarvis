"""Real-model smoke test with synthetic speech; never opens microphone or desktop apps."""

import base64
import io
import json
import os
import queue
import subprocess
import sys
import threading
import time
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def pcm16k(wav_bytes):
    with wave.open(io.BytesIO(wav_bytes), "rb") as audio:
        samples = np.frombuffer(audio.readframes(audio.getnframes()), dtype="<i2")
        rate = audio.getframerate()
    size = round(len(samples) * 16000 / rate)
    values = np.interp(np.arange(size) * rate / 16000, np.arange(len(samples)), samples)
    return values.astype("<i2").tobytes()


def main():
    env = dict(os.environ, PYTHONPATH=str(ROOT / "speech"), PYTHONIOENCODING="utf-8", HF_HUB_OFFLINE="1")
    (ROOT / "work").mkdir(exist_ok=True)
    messages = queue.Queue()
    results = {}
    with (ROOT / "work" / "speech-test-stderr.log").open("w", encoding="utf-8") as errors:
        process = subprocess.Popen(
            [sys.executable, "-u", "-m", "jarvis_speech.worker"],
            cwd=ROOT,
            env=env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=errors,
            text=True,
            encoding="utf-8",
        )

        def reader():
            for line in process.stdout:
                messages.put(json.loads(line))
            messages.put({"type": "eof"})

        threading.Thread(target=reader, daemon=True).start()

        def send(kind, session=1, **kwargs):
            process.stdin.write(json.dumps({"type": kind, "session": session, **kwargs}) + "\n")
            process.stdin.flush()

        def expect(kind, timeout=45):
            deadline = time.monotonic() + timeout
            while True:
                event = messages.get(timeout=max(0.01, deadline - time.monotonic()))
                if event["type"] in {"error", "fatal", "eof"}:
                    raise RuntimeError(event)
                if event["type"] == kind:
                    return event

        try:
            expect("ready", timeout=120)
            send("synthesize", text="Джарвис, запусти браузер, пожалуйста.")
            audio = pcm16k(base64.b64decode(expect("speech")["wav"]))
            with wave.open(str(ROOT / "work" / "command-16k.wav"), "wb") as out:
                out.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
                out.writeframes(audio)
            send("transcribe", pcm=base64.b64encode(audio).decode())
            result = expect("transcript")
            assert "браузер" in result["text"].lower(), result
            results["file_transcript"] = result
            send("transcribe", session=2, pcm=base64.b64encode(bytes(64000)).decode())
            expect("empty")
            results["silence_rejected"] = True
            send("start", session=3, mode="command")
            expect("listening")
            for start in range(0, len(audio), 3200):
                send("audio", session=3, pcm=base64.b64encode(audio[start : start + 3200]).decode())
            for _ in range(12):
                send("audio", session=3, pcm=base64.b64encode(bytes(3200)).decode())
            results["stream_transcript"] = expect("transcript")
            send("start", session=4, mode="wake")
            expect("listening")
            for start in range(0, len(audio), 3200):
                send("audio", session=4, pcm=base64.b64encode(audio[start : start + 3200]).decode())
            for _ in range(12):
                send("audio", session=4, pcm=base64.b64encode(bytes(3200)).decode())
            try:
                expect("activated", timeout=8)
                results["wake_transcript"] = expect("transcript")
            except queue.Empty:
                raise AssertionError("Wake word was not detected in the synthetic phrase") from None
            send("cancel", session=5)
            send("synthesize", session=5, text="Браузер открыт.")
            assert len(base64.b64decode(expect("speech")["wav"])) > 1000
            results["reply_synthesized"] = True
            (ROOT / "work" / "speech-smoke.json").write_text(
                json.dumps(results, indent=2, ensure_ascii=False), encoding="utf-8"
            )
            print(json.dumps(results, indent=2, ensure_ascii=False))
        finally:
            if process.poll() is None:
                send("quit")
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
            process.stdin.close()
            process.stdout.close()


if __name__ == "__main__":
    main()
