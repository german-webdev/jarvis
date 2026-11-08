"""Version 1 JSONL protocol over standard streams; diagnostics go to stderr."""

import base64
import json
import queue
import sys
import threading
import time
import traceback
from pathlib import Path

from jarvis_speech.engine import Engine
from jarvis_speech.interaction import Interaction
from jarvis_speech.segmenter import FRAME_BYTES


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
    interaction = Interaction(engine, lambda kind, **values: emit(kind, session, **values))

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
                engine.set_vocabulary(command.get("vocabulary", ""), choosing=command.get("choosing", False))
                session = int(incoming)
                mode = command.get("mode", "command")
                interaction.start(mode, float(command.get("wait_seconds", 8)))
                emit("listening", session, mode=mode)
            elif kind == "cancel":
                interaction.reset()
                engine.reset()
                session = int(incoming)
            elif kind == "synthesize":
                # No action is ever executed here. The host decides whether this reply is still relevant.
                wav = engine.synthesize(
                    str(command["text"]),
                    voice=command.get("voice", "dmitri"),
                    style=command.get("style", "natural"),
                    amount=float(command.get("amount", 0.45)),
                    rate=float(command.get("rate", 1.0)),
                )
                emit("speech", incoming, wav=base64.b64encode(wav).decode("ascii"))
            elif kind == "transcribe":
                engine.set_vocabulary(command.get("vocabulary", ""))
                session = int(incoming)
                interaction.reset()
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
            elif kind == "audio" and incoming == session:
                interaction.feed(base64.b64decode(command["pcm"], validate=True))
            elif kind not in {"audio"}:
                raise ValueError("Unknown protocol operation")
        except Exception as error:
            interaction.reset()
            traceback.print_exc(file=sys.stderr)
            emit("error", incoming, text=str(error))


if __name__ == "__main__":
    raise SystemExit(main())
