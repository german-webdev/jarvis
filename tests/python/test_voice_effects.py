import io
import wave

import numpy as np
import pytest
from jarvis_speech.voice_effects import apply_effect


def sample():
    result = io.BytesIO()
    with wave.open(result, "wb") as w:
        w.setparams((1, 2, 22050, 0, "NONE", "not compressed"))
        w.writeframes((np.sin(np.arange(2205) * 0.1) * 32000).astype("<i2").tobytes())
    return result.getvalue()


@pytest.mark.parametrize("style", ["robot", "radio"])
def test_effect_preserves_format_and_does_not_clip(style):
    result = apply_effect(sample(), style, 1)
    with wave.open(io.BytesIO(result), "rb") as w:
        assert (w.getnchannels(), w.getsampwidth(), w.getframerate()) == (1, 2, 22050)
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2")
        assert np.max(np.abs(pcm.astype(np.int32))) <= 31130
    assert result != sample()


def test_disabled_effect_is_bit_identical():
    assert apply_effect(sample(), "natural") == sample()
    assert apply_effect(sample(), "robot", 0) == sample()


def test_invalid_voice_style_rejected():
    with pytest.raises(ValueError):
        apply_effect(sample(), "unknown")
