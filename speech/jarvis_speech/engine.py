import io
import json
import time
import wave
from pathlib import Path

import numpy as np
import onnxruntime as ort

from jarvis_speech.voice_effects import apply_effect


class Vad:
    """Silero's published recurrent ONNX interface, CPU-only at 16 kHz."""

    def __init__(self, path: Path):
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        self.session = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
        self.reset()

    def reset(self):
        self.state = np.zeros((2, 1, 128), dtype=np.float32)
        self.context = np.zeros((1, 64), dtype=np.float32)

    def probability(self, pcm: bytes) -> float:
        samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32).reshape(1, -1) / 32768
        data = np.concatenate([self.context, samples], axis=1)
        result, self.state = self.session.run(
            None, {"input": data, "state": self.state, "sr": np.array(16000, dtype=np.int64)}
        )
        self.context = data[:, -64:].copy()
        return float(result[0][0])


class Engine:
    def __init__(self, root: Path, progress):
        from faster_whisper import WhisperModel
        from piper import PiperVoice
        from vosk import KaldiRecognizer, Model, SetLogLevel

        models = root / "models"
        progress("Загружаю определение речи…")
        self.vad = Vad(models / "silero_vad.onnx")
        progress("Загружаю распознавание речи…")
        self.asr = WhisperModel(
            str(models / "whisper-small"),
            device="cpu",
            compute_type="int8",
            cpu_threads=4,
            num_workers=1,
            local_files_only=True,
        )
        progress("Загружаю голос Дмитрий…")
        self.voice = PiperVoice.load(str(models / "piper" / "ru_RU-dmitri-medium.onnx"))
        self.models = models
        self.voices = {"dmitri": self.voice}
        self.speech_cache = {}
        progress("Загружаю ожидание имени…")
        SetLogLevel(-1)
        self.wake_model = Model(str(models / "vosk-model-small-ru-0.22"))
        self.wake = KaldiRecognizer(self.wake_model, 16000)
        self.wake.SetWords(True)

    def wake_boundary(self, pcm: bytes) -> int | None:
        final = self.wake.AcceptWaveform(pcm)
        value = json.loads(self.wake.Result() if final else self.wake.PartialResult())
        words = value.get("text", value.get("partial", "")).lower().split()
        # No broad fuzzy matching: similar TV words must not activate commands.
        if not ({"джарвис", "jarvis"} & set(words)):
            return None
        # Finalize only after the fast partial match; stable partial timestamps are much slower.
        result = value if final else json.loads(self.wake.FinalResult())
        for word in result.get("result", []):
            if word["word"] in {"джарвис", "jarvis"}:
                return round(word["end"] * 16000)
        return None

    def reset(self):
        self.vad.reset()
        self.wake.Reset()

    def transcribe(self, pcm: bytes) -> str:
        samples = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768
        segments, _ = self.asr.transcribe(
            samples,
            language="ru",
            beam_size=5,
            condition_on_previous_text=False,
            vad_filter=False,
            initial_prompt="Джарвис. Браузер. Открой. Запусти. Отмена.",
            hotwords="браузер, Джарвис",
            no_speech_threshold=0.6,
        )
        return " ".join(s.text.strip() for s in segments).strip()

    def recognize(self, pcm: bytes) -> tuple[str, int]:
        started = time.perf_counter()
        text = self.transcribe(pcm)
        return text, round((time.perf_counter() - started) * 1000)

    def synthesize(self, text: str, voice="dmitri", style="natural", amount=0.45, rate=1.0) -> bytes:
        from piper import PiperVoice, SynthesisConfig

        if len(text) > 500:
            raise ValueError("Reply is too long")
        if voice not in {"dmitri", "denis"} or not 0.85 <= rate <= 1.2:
            raise ValueError("Unsupported voice or speech rate")
        if voice not in self.voices:
            self.voices[voice] = PiperVoice.load(str(self.models / "piper" / f"ru_RU-{voice}-medium.onnx"))
        key = (text, voice, style, round(amount, 2), round(rate, 2))
        if key not in self.speech_cache:
            stream = io.BytesIO()
            with wave.open(stream, "wb") as out:
                self.voices[voice].synthesize_wav(
                    text, out, syn_config=SynthesisConfig(length_scale=1 / rate)
                )
            if len(self.speech_cache) >= 32:
                self.speech_cache.clear()
            self.speech_cache[key] = apply_effect(stream.getvalue(), style, amount)
        return self.speech_cache[key]
