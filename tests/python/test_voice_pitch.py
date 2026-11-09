import base64
import io
import json
import wave
from types import SimpleNamespace

import numpy as np
import pytest
from jarvis_speech.engine import Engine
from jarvis_speech.voice_effects import apply_effect, apply_pitch, normalize_pitch


def wav_samples(samples, rate=22050):
    stream = io.BytesIO()
    with wave.open(stream, "wb") as output:
        output.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        output.writeframes(np.asarray(samples, dtype="<i2").tobytes())
    return stream.getvalue()


def read_samples(wav):
    with wave.open(io.BytesIO(wav), "rb") as source:
        assert (source.getnchannels(), source.getsampwidth()) == (1, 2)
        return source.getframerate(), np.frombuffer(source.readframes(source.getnframes()), dtype="<i2")


@pytest.mark.parametrize("rate", [16000, 22050, 48000])
@pytest.mark.parametrize("pitch", [-6, -3, 3, 6])
def test_pitch_changes_frequency_without_changing_duration_or_sample_rate(rate, pitch):
    samples = np.sin(np.arange(rate * 2) * 2 * np.pi * 440 / rate) * 20000
    actual_rate, actual = read_samples(apply_pitch(wav_samples(samples, rate), pitch))
    assert actual_rate == rate
    assert len(actual) == len(samples)
    middle = actual[rate // 4:-rate // 4]
    spectrum = np.abs(np.fft.rfft(middle * np.hanning(len(middle))))
    frequency = np.argmax(spectrum) * rate / len(middle)
    assert frequency == pytest.approx(440 * 2 ** (pitch / 12), abs=1)
    assert np.max(np.abs(actual.astype(np.int32))) <= 31130
    assert np.sqrt(np.mean(middle.astype(float) ** 2)) > 10000


@pytest.mark.parametrize("pitch", [0, None, "invalid", float("nan"), float("inf"), -float("inf")])
def test_disabled_or_invalid_pitch_preserves_every_byte(pitch):
    wav = wav_samples([0, 2000, -3000, 32767, -32768])
    assert apply_pitch(wav, pitch) == wav


@pytest.mark.parametrize("pitch,expected", [(-100, -6), (100, 6), ("3.25", 3.25)])
def test_pitch_is_bounded(pitch, expected):
    assert normalize_pitch(pitch) == expected
    wav = wav_samples(np.sin(np.arange(22050) * 0.1) * 32000)
    assert apply_pitch(wav, pitch) == apply_pitch(wav, expected)


@pytest.mark.parametrize("length", [0, 1, 8, 100, 1000])
@pytest.mark.parametrize("pitch", [-6, 6])
def test_short_audio_stays_finite_and_same_length(length, pitch):
    wav = wav_samples(np.full(length, 32767))
    rate, samples = read_samples(apply_pitch(wav, pitch))
    assert rate == 22050
    assert len(samples) == length
    if length:
        assert np.max(np.abs(samples.astype(np.int32))) <= 31130
        assert np.any(samples)
    else:
        assert apply_pitch(wav, pitch) == wav


@pytest.mark.parametrize("style", ["natural", "radio", "robot"])
def test_pitch_retains_existing_style_duration(style):
    wav = wav_samples(np.sin(np.arange(22050) * 0.1) * 16000)
    baseline = apply_effect(wav, style, 0.7)
    shifted = apply_effect(apply_pitch(wav, 6), style, 0.7)
    assert read_samples(shifted)[0] == read_samples(baseline)[0]
    assert len(read_samples(shifted)[1]) == len(read_samples(baseline)[1])
    assert shifted != baseline


def test_engine_routes_pitch_and_keeps_rate_and_cache_independent():
    calls = []

    def synthesize_wav(text, output, syn_config):
        calls.append(syn_config.length_scale)
        output.setparams((1, 2, 22050, 0, "NONE", "not compressed"))
        output.writeframes((np.sin(np.arange(22050) * 0.1) * 16000).astype("<i2").tobytes())

    engine = Engine.__new__(Engine)
    engine.voices = {"dmitri": SimpleNamespace(synthesize_wav=synthesize_wav)}
    engine.speech_cache = {}
    original = engine.synthesize("test", rate=1.1)
    shifted = engine.synthesize("test", rate=1.1, pitch=6)
    assert shifted != original
    assert engine.synthesize("test", rate=1.1, pitch=100) == shifted
    assert engine.synthesize("test", rate=1.1, pitch=float("nan")) == original
    assert calls == [1 / 1.1, 1 / 1.1]
    assert len(read_samples(original)[1]) == len(read_samples(shifted)[1])


@pytest.mark.parametrize("pitch", [None, -3.5])
def test_worker_forwards_pitch_and_older_requests_default_to_zero(monkeypatch, pitch):
    from jarvis_speech import worker

    calls = []

    class Stream(io.StringIO):
        def reconfigure(self, **kwargs):
            pass

    class StubEngine:
        def __init__(self, *args, **kwargs):
            pass

        def synthesize(self, text, **kwargs):
            calls.append((text, kwargs))
            return b"speech"

    command = {"type": "synthesize", "text": "test", "session": 17, "rate": 1.1}
    if pitch is not None:
        command["pitch"] = pitch
    output = Stream()
    monkeypatch.setattr(worker.sys, "stdin", Stream(json.dumps(command) + "\n"))
    monkeypatch.setattr(worker.sys, "stdout", output)
    monkeypatch.setattr(worker, "Engine", StubEngine)
    assert worker.main() == 0
    assert calls[0][1]["pitch"] == (pitch if pitch is not None else 0)
    assert calls[0][1]["rate"] == 1.1
    emitted = [json.loads(line) for line in output.getvalue().splitlines()]
    assert emitted[-1] == {"type": "speech", "session": 17, "wav": base64.b64encode(b"speech").decode()}
