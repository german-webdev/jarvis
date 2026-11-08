"""Wake-word boundaries and command collection, independent of the desktop host."""

import re
from collections import deque

from jarvis_speech.segmenter import FRAME_BYTES, FRAME_SECONDS, Segmenter


def address_only(text: str) -> bool:
    words = re.findall(r"[а-яёa-z]+", text.lower())
    return bool(words) and all(w in {"джарвис", "джервис", "чарвис", "джарвиз", "jarvis"} for w in words)


class Interaction:
    def __init__(self, engine, emit):
        self.engine = engine
        self.emit = emit
        self.reset()

    def reset(self):
        self.mode = "idle"
        self.pending = bytearray()
        self.preroll = deque(maxlen=188)
        self.samples = 0
        self.waited = 0
        self.from_wake = False
        self.segmenter = Segmenter()

    def start(self, mode: str, wait_seconds=8):
        if mode not in {"command", "wake"}:
            raise ValueError("Unknown listening mode")
        if not 4 <= wait_seconds <= 15:
            raise ValueError("Command wait must be between 4 and 15 seconds")
        self.reset()
        self.mode = mode
        self.from_wake = mode == "wake"
        self.wait_frames = round(wait_seconds / FRAME_SECONDS)
        self.segmenter = Segmenter(wait_seconds=wait_seconds)
        self.engine.reset()

    def feed(self, pcm: bytes):
        if len(pcm) > 32000 or len(pcm) % 2:
            raise ValueError("Audio packet exceeds one second or is not PCM16")
        if self.mode == "idle":
            return
        self.pending.extend(pcm)
        while len(self.pending) >= FRAME_BYTES and self.mode != "idle":
            frame = bytes(self.pending[:FRAME_BYTES])
            del self.pending[:FRAME_BYTES]
            if self.mode == "wake":
                self.samples += FRAME_BYTES // 2
                self.preroll.append(frame)
                boundary = self.engine.wake_boundary(frame)
                if boundary is None:
                    continue
                # Vosk timestamps let us discard the name but retain a command spoken immediately after it.
                buffered = b"".join(self.preroll)
                buffer_start = self.samples - len(buffered) // 2
                cut = max(0, min(len(buffered), (boundary - buffer_start) * 2))
                self.pending[:0] = buffered[cut:]
                self.preroll.clear()
                self.mode = "command"
                self.segmenter.reset()
                self.engine.vad.reset()
                self.emit("activated")
                continue
            self.waited += 1
            result = self.segmenter.feed(frame, self.engine.vad.probability(frame))
            if result:
                outcome, audio = result
                if outcome == "audio":
                    # Keep collecting microphone packets while checking a wake-mode candidate.
                    self.emit("processing", keep_capture=self.from_wake)
                    text, elapsed = self.engine.recognize(audio)
                    if self.from_wake and (not text or address_only(text)):
                        self.engine.vad.reset()
                        self.emit("awaiting_command")
                    else:
                        self.mode = "idle"
                        self.emit("transcript", text=text, elapsed_ms=elapsed)
                        continue
                elif not self.from_wake:
                    self.mode = "idle"
                    self.emit("empty")
                    continue
            if self.from_wake and self.waited >= self.wait_frames and not self.segmenter.frames:
                self.mode = "idle"
                self.emit("command_timeout")
