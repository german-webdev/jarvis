"""Local Vosk wake test with synthetic speech; never opens a microphone."""

import io
import json
import sys
import wave
from pathlib import Path

import numpy as np
import onnxruntime as ort
from piper import PiperVoice
from test_speech import pcm16k
from vosk import KaldiRecognizer, Model, SetLogLevel

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "speech"))

from jarvis_speech.engine import Engine  # noqa: E402


def main():
    ort.set_seed(0)
    SetLogLevel(-1)
    model = Model(str(ROOT / "models/vosk-model-small-ru-0.22"))
    results = []
    for voice_name in ["dmitri", "denis"]:
        voice = PiperVoice.load(str(ROOT / f"models/piper/ru_RU-{voice_name}-medium.onnx"))
        phrases = [("Джарвис.", True), ("Сервис.", False), ("Сервис работает.", False),
                   ("Открой браузер.", False), ("Я вернусь через час.", False)]
        if voice_name == "dmitri":
            phrases.insert(1, ("Джервис.", True))
        for phrase, expected in phrases:
            out = io.BytesIO()
            with wave.open(out, "wb") as wav:
                voice.synthesize_wav(phrase, wav)
            samples = np.frombuffer(pcm16k(out.getvalue()), dtype="<i2")
            for level in ([1, 0.2] if expected else [1]):
                pcm = (samples.astype(np.float32) * level).astype("<i2").tobytes() + bytes(64000)
                engine = Engine.__new__(Engine)
                engine.wake = KaldiRecognizer(model, 16000)
                engine.wake.SetWords(True)
                observed = []
                engine.wake_observer = observed.append
                boundary = None
                for pos in range(0, len(pcm), 1024):
                    boundary = engine.wake_boundary(pcm[pos:pos + 1024])
                    if boundary is not None:
                        break
                results.append({"voice": voice_name, "phrase": phrase, "level": level,
                                "activated": boundary is not None, "expected": expected,
                                "observed": observed})
    report = {"cases": results, "success": all(r["activated"] == r["expected"] for r in results)}
    (ROOT / "work").mkdir(exist_ok=True)
    (ROOT / "work/wake-smoke.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    assert report["success"], "See work/wake-smoke.json"


if __name__ == "__main__":
    main()
