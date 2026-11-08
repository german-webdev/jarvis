"""Bounded utterance collection, independent of microphone or ML runtime."""

from collections import deque

RATE = 16000
FRAME_SAMPLES = 512
FRAME_BYTES = FRAME_SAMPLES * 2
FRAME_SECONDS = FRAME_SAMPLES / RATE


class Segmenter:
    def __init__(self, silence_seconds=0.8, maximum_seconds=10, wait_seconds=6):
        self.silence_frames = round(silence_seconds / FRAME_SECONDS)
        self.maximum_frames = round(maximum_seconds / FRAME_SECONDS)
        self.wait_frames = round(wait_seconds / FRAME_SECONDS)
        self.reset()

    def reset(self):
        self.preroll = deque(maxlen=10)
        self.frames = []
        self.silence = 0
        self.waited = 0
        self.voiced = 0

    def feed(self, pcm: bytes, probability: float):
        if len(pcm) != FRAME_BYTES:
            raise ValueError("Expected one 512-sample PCM16 frame")
        if not self.frames:
            self.preroll.append(pcm)
            self.waited += 1
            if probability >= 0.5:
                self.frames = list(self.preroll)
                self.voiced = 1
            elif self.waited >= self.wait_frames:
                self.reset()
                return "empty", b""
            return None
        self.frames.append(pcm)
        if probability >= 0.5:
            self.voiced += 1
            self.silence = 0
        else:
            self.silence += 1
        if self.silence >= self.silence_frames or len(self.frames) >= self.maximum_frames:
            audio = b"".join(self.frames)
            kind = "audio" if self.voiced >= 5 else "empty"
            self.reset()
            return kind, audio if kind == "audio" else b""
        return None
