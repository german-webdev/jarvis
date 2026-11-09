"""Lightweight local robot colouring. No remote service or voice impersonation."""

import io
import math
import wave

import numpy as np


def normalize_pitch(pitch=0) -> float:
    """Semitones, bounded for speech; malformed saved settings use the natural pitch."""
    try:
        value = float(pitch)
    except (TypeError, ValueError, OverflowError):
        return 0.0
    return round(max(-6.0, min(6.0, value)), 2) if math.isfinite(value) else 0.0


def apply_pitch(wav: bytes, pitch=0) -> bytes:
    """Shift pitch independently of speaking speed, retaining PCM format and duration."""
    pitch = normalize_pitch(pitch)
    if pitch == 0:
        return wav
    with wave.open(io.BytesIO(wav), "rb") as source:
        if source.getnchannels() != 1 or source.getsampwidth() != 2:
            raise ValueError("Voice pitch expects mono PCM16")
        rate = source.getframerate()
        data = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(np.float32) / 32768
    if not len(data):
        return wav

    # PyAV is already included by faster-whisper. Use its bundled FFmpeg filters,
    # not a system executable. Resampling shifts pitch; reciprocal atempo restores
    # duration without undoing the pitch shift (its range here is 0.707..1.414).
    import av

    shifted_rate = round(rate * 2 ** (pitch / 12))
    graph = av.filter.Graph()
    source = graph.add_abuffer(sample_rate=rate, format="flt", layout="mono", time_base=f"1/{rate}")
    graph.link_nodes(
        source,
        graph.add("asetrate", str(shifted_rate)),
        graph.add("aresample", f"{rate}:filter_size=64"),
        graph.add("atempo", str(rate / shifted_rate)),
        graph.add("aformat", "sample_fmts=flt:channel_layouts=mono"),
        graph.add("abuffersink"),
    )
    graph.configure()
    # Drain the filters through trailing silence, including for tiny utterances.
    # atempo rounds to analysis windows; trim/pad to the original frame count.
    padded = np.pad(data, (0, round(rate * 0.25))).reshape(1, -1)
    frame = av.AudioFrame.from_ndarray(padded, format="flt", layout="mono")
    frame.sample_rate = rate
    frame.pts = 0
    graph.push(frame)
    graph.push(None)
    chunks = []
    while True:
        try:
            chunks.append(graph.pull().to_ndarray().reshape(-1))
        except av.error.EOFError:
            break
    processed = np.concatenate(chunks) if chunks else np.empty(0, dtype=np.float32)
    processed = np.pad(processed[:len(data)], (0, max(0, len(data) - len(processed))))
    peak = float(np.max(np.abs(processed)))
    if peak > 0.95:
        processed *= 0.95 / peak
    result = io.BytesIO()
    with wave.open(result, "wb") as target:
        target.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        target.writeframes((np.clip(processed, -1, 1) * 32767).astype("<i2").tobytes())
    return result.getvalue()


def apply_effect(wav: bytes, style="natural", amount=0.45) -> bytes:
    if style not in {"natural", "robot", "radio"}:
        raise ValueError("Unknown voice style")
    if not 0 <= amount <= 1:
        raise ValueError("Robot amount must be between zero and one")
    if style == "natural" or amount == 0:
        return wav
    with wave.open(io.BytesIO(wav), "rb") as source:
        if source.getnchannels() != 1 or source.getsampwidth() != 2:
            raise ValueError("Voice effects expect mono PCM16")
        rate = source.getframerate()
        data = np.frombuffer(source.readframes(source.getnframes()), dtype="<i2").astype(np.float32) / 32768
    if not len(data):
        return wav
    if style == "robot":
        # A subtle lower pitch plus parallel metallic modulation retains consonants and intelligibility.
        ratio = 1 - 0.07 * amount
        data = np.interp(np.arange(round(len(data) / ratio)) * ratio, np.arange(len(data)), data)
        time = np.arange(len(data)) / rate
        metallic = data * (0.65 * np.cos(2 * np.pi * 73 * time) + 0.35 * np.cos(2 * np.pi * 109 * time))
        delayed = np.zeros_like(data)
        delay = round(0.018 * rate)
        delayed[delay:] = data[:-delay] if len(data) > delay else []
        processed = data * (1 - amount * 0.25) + metallic * amount * 0.30 + delayed * amount * 0.10
    else:
        spectrum = np.fft.rfft(data)
        frequencies = np.fft.rfftfreq(len(data), 1 / rate)
        band = (frequencies >= 280) & (frequencies <= 3800)
        filtered = np.fft.irfft(spectrum * band, n=len(data))
        processed = data * (1 - amount) + np.tanh(filtered * 1.6) / 1.6 * amount
    peak = float(np.max(np.abs(processed)))
    if peak > 0.95:
        processed *= 0.95 / peak
    result = io.BytesIO()
    with wave.open(result, "wb") as target:
        target.setparams((1, 2, rate, 0, "NONE", "not compressed"))
        target.writeframes((np.clip(processed, -1, 1) * 32767).astype("<i2").tobytes())
    return result.getvalue()
