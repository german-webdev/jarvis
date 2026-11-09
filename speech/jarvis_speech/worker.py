"""Version 1 JSONL protocol over standard streams; diagnostics go to stderr."""

import base64
import json
import sys
import threading
import time
import traceback
from collections import deque
from pathlib import Path

from jarvis_speech.engine import Engine
from jarvis_speech.interaction import Interaction
from jarvis_speech.segmenter import FRAME_BYTES


def emit(kind, session=0, **values):
    print(json.dumps({"type": kind, "session": session, **values}, ensure_ascii=False), flush=True)


class CommandInbox:
    """Bounded PCM backlog; session controls replace pending work, never wait on ASR."""

    def __init__(self):
        self._condition = threading.Condition()
        self._pending = deque()
        self.generation = 0
        self.session = 0
        self.audio_limit = 128

    def put(self, message):
        with self._condition:
            kind = message.get("type")
            if kind in {"start", "cancel", "transcribe"}:
                self.generation += 1
                self.session = message.get("session", 0)
                self.audio_limit = 16 if kind == "start" and message.get("mode") == "task_interrupt" else 128
                self._pending.clear()
            elif kind == "audio":
                if not isinstance(message.get("pcm"), str) or len(message["pcm"]) > 42668:
                    raise ValueError("Audio packet exceeds one second")
                if message.get("session") != self.session or len(self._pending) >= self.audio_limit:
                    return
            elif kind != "quit" and len(self._pending) >= 128:
                raise ValueError("Protocol queue is full")
            self._pending.append((self.generation, message))
            self._condition.notify()

    def get(self):
        with self._condition:
            while not self._pending:
                self._condition.wait()
            return self._pending.popleft()


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
    inbox = CommandInbox()
    threading.Thread(target=read_commands, args=(inbox,), daemon=True).start()
    emit("ready", protocol=1, asr="small/cpu/int8", voice="ru_RU-dmitri-medium")
    return run_worker(engine, inbox, emit)


def run_worker(engine, inbox, output):
    session = 0
    generation = -1

    def current_emit(kind, **values):
        # Recognition cannot be interrupted inside the native ASR call. Its result is
        # discarded if stdin has already accepted a newer control/session boundary.
        if generation == inbox.generation:
            output(kind, session, **values)

    engine.wake_observer = lambda text: current_emit("wake_heard", text=text)
    interaction = Interaction(engine, current_emit)

    def transcribe(pcm):
        current_emit("processing")
        started = time.perf_counter()
        text = engine.transcribe(pcm)
        current_emit("transcript", text=text, elapsed_ms=round((time.perf_counter() - started) * 1000))

    while True:
        incoming_generation, command = inbox.get()
        kind = command.get("type")
        incoming = command.get("session", 0)
        try:
            if kind == "quit":
                return 0
            if kind == "start":
                generation = incoming_generation
                session = int(incoming)
                mode = command.get("mode", "command")
                if mode == "task_interrupt":
                    engine.set_task_interrupt()
                else:
                    engine.set_vocabulary(command.get("vocabulary", ""), choosing=command.get("choosing", False))
                interaction.start(mode, float(command.get("wait_seconds", 8)))
                current_emit("listening", mode=mode)
            elif kind == "cancel":
                generation = incoming_generation
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
                    pitch=command.get("pitch", 0),
                )
                if incoming_generation == inbox.generation:
                    output("speech", incoming, wav=base64.b64encode(wav).decode("ascii"))
            elif kind == "transcribe":
                generation = incoming_generation
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
                    current_emit("empty")
                else:
                    transcribe(pcm)
            elif kind == "audio" and incoming == session and incoming_generation == generation == inbox.generation:
                interaction.feed(base64.b64decode(command["pcm"], validate=True))
            elif kind not in {"audio"}:
                raise ValueError("Unknown protocol operation")
        except Exception as error:
            interaction.reset()
            traceback.print_exc(file=sys.stderr)
            if incoming_generation == inbox.generation:
                output("error", incoming, text=str(error))


if __name__ == "__main__":
    raise SystemExit(main())
