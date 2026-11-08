"""Lightweight local robot colouring. No remote service or voice impersonation."""

import io
import wave

import numpy as np


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
