from jarvis_speech.interaction import Interaction, address_only
from jarvis_speech.segmenter import FRAME_BYTES

NAME = b"\x01\x00" * (FRAME_BYTES // 2)
SPEECH = b"\x02\x00" * (FRAME_BYTES // 2)
SILENCE = bytes(FRAME_BYTES)


class FakeEngine:
    def __init__(self, detect_at=1, replies=None):
        self.detect_at = detect_at
        self.replies = iter(replies or ["Открой браузер"])
        self.vad = self
        self.frames = 0

    def reset(self):
        self.frames = 0

    def wake_boundary(self, frame):
        self.frames += 1
        return 512 if self.frames == self.detect_at else None

    def probability(self, frame):
        return 1 if frame == SPEECH else 0

    def recognize(self, pcm):
        return next(self.replies), 1


def fixture(**kwargs):
    events = []
    interaction = Interaction(FakeEngine(**kwargs), lambda kind, **data: events.append((kind, data)))
    interaction.start("wake", wait_seconds=8)
    return interaction, events


def test_name_then_two_second_pause_waits_for_command():
    gate, events = fixture()
    gate.feed(NAME)
    for _ in range(63):
        gate.feed(SILENCE)
    assert [kind for kind, _ in events] == ["activated"]
    for frame in [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert [kind for kind, _ in events] == ["activated", "processing", "transcript"]
    assert events[-1][1]["text"] == "Открой браузер"


def test_silent_timeout_never_becomes_unknown_command():
    gate, events = fixture()
    gate.feed(NAME)
    for _ in range(260):
        gate.feed(SILENCE)
    assert [kind for kind, _ in events] == ["activated", "command_timeout"]


def test_immediate_command_survives_delayed_wake_detection():
    gate, events = fixture(detect_at=4)
    for frame in [NAME] + [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert events[-1][0] == "transcript"


def test_name_only_transcription_does_not_escape_to_command_router():
    gate, events = fixture(replies=["Джарвис.", "Открой браузер"])
    gate.feed(NAME)
    for frame in ([SPEECH] * 8 + [SILENCE] * 30) * 2:
        gate.feed(frame)
    assert [kind for kind, _ in events].count("transcript") == 1
    assert "awaiting_command" in [kind for kind, _ in events]


def test_cancel_discards_pending_command():
    gate, events = fixture()
    gate.feed(NAME)
    gate.feed(SPEECH)
    gate.reset()
    for frame in [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert [kind for kind, _ in events] == ["activated"]


def test_name_filter_does_not_swallow_actual_commands():
    assert address_only("Джарвис!")
    assert not address_only("Джарвис, открой браузер")
    assert not address_only("не открывай браузер")
