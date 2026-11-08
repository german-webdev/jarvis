import pytest
from jarvis_speech.segmenter import FRAME_BYTES, Segmenter

FRAME = bytes(FRAME_BYTES)


def test_silence_does_not_become_a_command():
    segmenter = Segmenter(wait_seconds=0.2)
    results = [segmenter.feed(FRAME, 0.01) for _ in range(8)]
    assert ("empty", b"") in results


def test_retains_beginning_and_ends_after_silence():
    segmenter = Segmenter(silence_seconds=0.16)
    for _ in range(3):
        assert segmenter.feed(FRAME, 0) is None
    for _ in range(6):
        assert segmenter.feed(FRAME, 0.9) is None
    outputs = [segmenter.feed(FRAME, 0) for _ in range(5)]
    result = next(x for x in outputs if x)
    assert result[0] == "audio"
    assert len(result[1]) == 14 * FRAME_BYTES


def test_reset_discards_previous_phrase():
    segmenter = Segmenter(wait_seconds=0.16)
    for _ in range(10):
        segmenter.feed(FRAME, 0.99)
    segmenter.reset()
    assert [segmenter.feed(FRAME, 0) for _ in range(5)][-1] == ("empty", b"")


def test_maximum_bounds_continuous_speech():
    segmenter = Segmenter(maximum_seconds=0.32)
    result = [segmenter.feed(FRAME, 0.99) for _ in range(10)][-1]
    assert result == ("audio", FRAME * 10)


def test_rejects_incorrect_audio_format():
    with pytest.raises(ValueError):
        Segmenter().feed(b"x", 1)
