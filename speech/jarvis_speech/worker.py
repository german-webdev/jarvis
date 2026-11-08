"""Version 1 JSONL protocol over standard streams; diagnostics go to stderr."""

import base64
import json
import queue
import sys
import threading
import time
import traceback
from collections import deque
from pathlib import Path

from jarvis_speech.engine import Engine
from jarvis_speech.segmenter import FRAME_BYTES, Segmenter


def emit(kind, session=0, **values):
    print(json.dumps({"type": kind, "session": session, **values}, ensure_ascii=False), flush=True)


def read_commands(inbox):
    try:
        while line := sys.stdin.readline(2_000_001):
            if len(line) > 2_000_000:
                raise ValueError("Protocol message is too large")
            message = json.loads(line)
            if not isinstance(message, dict):
                raise ValueError("Expected an object")
            inbox.put(message)
    except Exception:
        traceback.print_exc(file=sys.stderr)
    finally:
        inbox.put({"type": "quit"})


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="strict")
    sys.stdin.reconfigure(encoding="utf-8", errors="strict")
    root = Path(__file__).resolve().parents[2]
    try:
        engine = Engine(root, lambda text: emit("loading", text=text))
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit("fatal", text=f"Не удалось загрузить голосовой модуль: {error}")
        return 1
    inbox = queue.Queue(maxsize=128)
    threading.Thread(target=read_commands, args=(inbox,), daemon=True).start()
    emit("ready", protocol=1, asr="small/cpu/int8", voice="ru_RU-dmitri-medium")
    session = 0
    mode = "idle"
    pending = bytearray()
    preroll = deque(maxlen=62)  # almost two seconds, includes a name spoken without a pause
    segmenter = Segmenter()

    def transcribe(pcm):
        emit("processing", session)
        started = time.perf_counter()
        text = engine.transcribe(pcm)
        emit("transcript", session, text=text, elapsed_ms=round((time.perf_counter() - started) * 1000))

    while True:
        command = inbox.get()
        kind = command.get("type")
        incoming = command.get("session", 0)
        try:
            if kind == "quit":
                return 0
            if kind == "start":
                session = int(incoming)
                mode = command.get("mode", "command")
                if mode not in {"command", "wake"}:
                    raise ValueError("Unknown listening mode")
                pending.clear()
                preroll.clear()
                segmenter.reset()
                engine.reset()
                emit("listening", session, mode=mode)
            elif kind == "cancel":
                mode = "idle"
                pending.clear()
                preroll.clear()
                segmenter.reset()
                engine.reset()
                session = int(incoming)
            elif kind == "synthesize":
                # No action is ever executed here. The host decides whether this reply is still relevant.
                wav = engine.synthesize(str(command["text"]))
                emit("speech", incoming, wav=base64.b64encode(wav).decode("ascii"))
            elif kind == "transcribe":
                session = int(incoming)
                mode = "idle"
                pcm = base64.b64decode(command["pcm"], validate=True)
                if not 3200 <= len(pcm) <= 640000 or len(pcm) % 2:
                    raise ValueError("Expected 0.1–20 seconds of PCM16 mono at 16 kHz")
                engine.reset()
                probabilities = [
                    engine.vad.probability(pcm[i : i + FRAME_BYTES])
                    for i in range(0, len(pcm) - FRAME_BYTES + 1, FRAME_BYTES)
                ]
                if sum(p >= 0.5 for p in probabilities) < 5:
                    emit("empty", session)
                else:
                    transcribe(pcm)
            elif kind == "audio" and incoming == session and mode != "idle":
                pcm = base64.b64decode(command["pcm"], validate=True)
                if len(pcm) > 32000 or len(pcm) % 2:
                    raise ValueError("Audio packet exceeds one second or is not PCM16")
                pending.extend(pcm)
                while len(pending) >= FRAME_BYTES and mode != "idle":
                    frame = bytes(pending[:FRAME_BYTES])
                    del pending[:FRAME_BYTES]
                    if mode == "wake":
                        preroll.append(frame)
                        if engine.wake_detected(frame):
                            mode = "command"
                            emit("activated", session)
                            segmenter.reset()
                            engine.vad.reset()
                            for old in preroll:
                                segmenter.feed(old, engine.vad.probability(old))
                            preroll.clear()
                        continue
                    result = segmenter.feed(frame, engine.vad.probability(frame))
                    if result:
                        mode = "idle"
                        outcome, audio = result
                        if outcome == "audio":
                            transcribe(audio)
                        else:
                            emit("empty", session)
            elif kind not in {"audio"}:
                raise ValueError("Unknown protocol operation")
        except Exception as error:
            mode = "idle"
            traceback.print_exc(file=sys.stderr)
            emit("error", incoming, text=str(error))


if __name__ == "__main__":
    raise SystemExit(main())
