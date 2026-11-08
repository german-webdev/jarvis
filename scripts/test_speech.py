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
            # Piper uses ONNX random operators. Fix their seed for reproducible test audio.
            [sys.executable, "-u", "-c", "import onnxruntime as ort; ort.set_seed(0); from jarvis_speech.worker import main; raise SystemExit(main())"],
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
            send("synthesize", session=6, text="Джарвис.")
            name_audio = pcm16k(base64.b64decode(expect("speech")["wav"]))
            send("synthesize", session=6, text="Запусти браузер, пожалуйста.")
            command_audio = pcm16k(base64.b64decode(expect("speech")["wav"]))

            def stream(data, session):
                for offset in range(0, len(data), 3200):
                    send("audio", session=session, pcm=base64.b64encode(data[offset:offset + 3200]).decode())

            send("start", session=7, mode="wake", wait_seconds=8)
            expect("listening")
            stream(name_audio + bytes(64000), 7)  # name, then a two-second pause
            expect("activated")
            # A transcript of the name must never reach the desktop command router.
            time.sleep(0.15)
            while not messages.empty():
                event = messages.get_nowait()
                assert event["type"] not in {"transcript", "empty", "command_timeout", "error"}, event
            stream(command_audio + bytes(32000), 7)
            results["paused_wake_transcript"] = expect("transcript")
            assert "браузер" in results["paused_wake_transcript"]["text"].lower()
            for session in range(20, 23):
                send("start", session=session, mode="wake", wait_seconds=4)
                expect("listening")
                stream(name_audio + bytes(160000), session)
                expect("activated")
                expect("command_timeout")
            results["name_only_timeout"] = True
            results["repeated_wake_sessions"] = 3
            for style in ["natural", "robot", "radio"]:
                send("synthesize", session=9, text="Системы готовы. Слушаю ваши команды.", voice="denis", style=style)
                voice_wav = base64.b64decode(expect("speech")["wav"])
                assert len(voice_wav) > 1000
                (ROOT / "work" / f"voice-denis-{style}.wav").write_bytes(voice_wav)
            results["denis_presets"] = ["natural", "robot", "radio"]
            results["name_variants"] = []
            for phrase, expected in [("Запусти сабнавтику.", "сабнавтику"), ("Шестую.", "шестую")]:
                send("synthesize", session=10, text=phrase)
                sample = pcm16k(base64.b64decode(expect("speech")["wav"]))
                send("start", session=11, mode="command", wait_seconds=10,
                     choosing=expected == "шестую",
                     vocabulary="Subnautica, сабнавтика, сабнавтику, Forza Horizon 5, Forza Horizon 6, пятую, шестую, вариант два")
                expect("listening")
                stream(sample + bytes(32000), 11)
                recognized = expect("transcript")
                assert expected in recognized["text"].lower(), recognized
                results["name_variants"].append(recognized)
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
