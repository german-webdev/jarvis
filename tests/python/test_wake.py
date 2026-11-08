import json

import pytest
from jarvis_speech.engine import Engine
from jarvis_speech.interaction import address_only
from jarvis_speech.wake import WAKE_NAMES


class Decoder:
    def __init__(self, word, final):
        self.word = word
        self.final = final

    def AcceptWaveform(self, pcm):
        return self.final

    def PartialResult(self):
        return json.dumps({"partial": self.word})

    def Result(self):
        return json.dumps({"text": self.word, "result": [{"word": self.word, "end": 0.75}]})

    def FinalResult(self):
        return self.Result()


@pytest.mark.parametrize("word", sorted(WAKE_NAMES))
@pytest.mark.parametrize("final", [False, True])
def test_alias_activates_and_preserves_name_boundary(word, final):
    engine = Engine.__new__(Engine)
    engine.wake = Decoder(word, final)
    observations = []
    engine.wake_observer = observations.append
    assert engine.wake_boundary(b"audio") == 12000
    assert observations == []
    assert address_only(word)
    assert not address_only(word + " открой браузер")


@pytest.mark.parametrize("word", ["сервис", "чарльз", "джар", "открой браузер", "суперджарвис"])
def test_unrelated_words_are_diagnostics_not_activation(word):
    engine = Engine.__new__(Engine)
    engine.wake = Decoder(word, True)
    observations = []
    engine.wake_observer = observations.append
    assert engine.wake_boundary(b"audio") is None
    assert observations == [word]
    assert not address_only(word)


def test_partials_do_not_flood_diagnostic_log():
    engine = Engine.__new__(Engine)
    engine.wake = Decoder("сервис", False)
    observations = []
    engine.wake_observer = observations.append
    for _ in range(50):
        assert engine.wake_boundary(b"audio") is None
    assert observations == []
