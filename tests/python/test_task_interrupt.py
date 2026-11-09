import base64
from types import SimpleNamespace

import pytest
from jarvis_speech.engine import Engine
from jarvis_speech.interaction import Interaction, is_stop_task
from jarvis_speech.worker import CommandInbox, run_worker
from test_interaction import SILENCE, SPEECH, FakeEngine


@pytest.mark.parametrize("phrase", [
    "останови задачу", "отмени задачу!", "stop task", "cancel task.",
    "Джарвис, останови задачу!", "Jarvis cancel task", "Джервис отмени задачу",
    "джарвиз stop task", "чарвис отмени задачу", "  STOP TASK?!  ",
])
def test_exact_stop_phrases(phrase):
    assert is_stop_task(phrase)
    events = []
    gate = Interaction(FakeEngine(replies=[phrase]), lambda kind, **data: events.append((kind, data)))
    gate.start("task_interrupt")
    for frame in [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert events == [("task_stop", {"text": phrase})]
    assert gate.mode == "idle"
    assert not gate.pending and not gate.segmenter.frames


@pytest.mark.parametrize("phrase", [
    "стоп", "stop", "отмена", "отмени задачу и открой браузер", "не останови задачу",
    "скажи останови задачу", "спроси нейросеть что дальше", "открой приложение",
    "новый разговор", "Джарвис", "", "останови  задачу", "cancel my task",
])
def test_other_phrases_are_private_and_next_utterance_still_stops(phrase):
    assert not is_stop_task(phrase)
    events = []
    gate = Interaction(FakeEngine(replies=[phrase, "cancel task"]), lambda kind, **data: events.append((kind, data)))
    gate.start("task_interrupt")
    for frame in [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert events == []
    assert gate.mode == "task_interrupt"
    for frame in [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert events == [("task_stop", {"text": "cancel task"})]


def test_silence_reset_and_utterance_are_bounded():
    events = []
    gate = Interaction(FakeEngine(replies=["unrelated"] * 20), lambda kind, **data: events.append((kind, data)))
    gate.start("task_interrupt")
    for frame in [SILENCE] * 4000 + [SPEECH] * 2000:
        gate.feed(frame)
        assert len(gate.pending) < len(SPEECH)
        assert len(gate.segmenter.frames) <= 125
    assert events == []
    gate.reset()
    for frame in [SPEECH] * 8 + [SILENCE] * 30:
        gate.feed(frame)
    assert events == []


def test_interrupt_asr_has_bilingual_prompt_and_cannot_inherit_choice_or_catalog():
    calls = []

    def transcribe(samples, **options):
        calls.append(options)
        return [SimpleNamespace(text="stop task")], None

    engine = Engine.__new__(Engine)  # No model, file, or microphone is opened.
    engine.asr = SimpleNamespace(transcribe=transcribe)
    engine.set_vocabulary("private catalog entry", choosing=True)
    engine.set_task_interrupt()
    assert engine.transcribe(SPEECH) == "stop task"
    assert calls[-1]["language"] is None
    assert "Stop task" in calls[-1]["initial_prompt"]
    assert "Останови задачу" in calls[-1]["initial_prompt"]
    assert "private" not in calls[-1]["hotwords"]
    engine.set_vocabulary("ordinary")
    engine.transcribe(SPEECH)
    assert calls[-1]["language"] == "ru"
    assert "Stop task" not in calls[-1]["initial_prompt"]


def pcm(session, data=SPEECH):
    return {"type": "audio", "session": session, "pcm": base64.b64encode(data).decode()}


def start(session):
    return {"type": "start", "session": session, "mode": "task_interrupt"}


def utterance(inbox, session):
    inbox.put(pcm(session, SPEECH * 8 + SILENCE * 20))
    inbox.put(pcm(session, SILENCE * 10))


def test_worker_queue_is_bounded_and_controls_discard_old_pcm_and_speech():
    inbox = CommandInbox()
    inbox.put(start(-1))
    for _ in range(1000):
        inbox.put(pcm(-1))
    assert len(inbox._pending) == 16
    inbox.put({"type": "synthesize", "session": 1, "text": "obsolete"})
    inbox.put({"type": "cancel", "session": 2})
    assert len(inbox._pending) == 1
    inbox.put(pcm(-1))
    assert len(inbox._pending) == 1
    inbox.put(start(-2))
    assert inbox.get()[1] == start(-2)
    assert not inbox._pending
    with pytest.raises(ValueError):
        inbox.put(pcm(-2, bytes(32002)))


def test_normal_wake_preserves_existing_queue_capacity_during_name_recognition():
    inbox = CommandInbox()
    inbox.put({"type": "start", "session": 1, "mode": "wake"})
    inbox.get()  # Worker is now recognizing a name-only candidate with keep_capture.
    for _ in range(128):
        inbox.put(pcm(1))
    assert len(inbox._pending) == 128
    inbox.put(start(-1))
    assert len(inbox._pending) == 1
    assert inbox.audio_limit == 16


def test_worker_late_asr_cannot_stop_new_capture_and_errors_allow_next_start():
    inbox = CommandInbox()
    events = []

    class Stub(FakeEngine):
        calls = 0

        def set_task_interrupt(self):
            pass

        def recognize(self, data):
            self.calls += 1
            if self.calls == 1:
                # stdin can accept a new boundary while native ASR is still finishing.
                for _ in range(30):
                    inbox.put(pcm(-1))
                inbox.put({"type": "cancel", "session": 1})
                inbox.put(start(-2))
                return "stop task", 1
            if self.calls == 2:
                raise RuntimeError("fixture recognition error")
            return "отмени задачу", 1

    def output(kind, session, **values):
        events.append((kind, session, values))
        if kind == "listening":
            utterance(inbox, session)
        elif kind == "error":
            inbox.put(start(-3))
        elif kind == "task_stop":
            inbox.put({"type": "quit"})

    inbox.put(start(-1))
    assert run_worker(Stub(), inbox, output) == 0
    assert [(kind, session) for kind, session, _ in events] == [
        ("listening", -1), ("listening", -2), ("error", -2),
        ("listening", -3), ("task_stop", -3),
    ]


def test_worker_cancel_resets_capture_before_synthesizing_and_drops_late_pcm():
    inbox = CommandInbox()
    events = []

    class Stub(FakeEngine):
        def set_task_interrupt(self):
            pass

        def recognize(self, data):
            pytest.fail("PCM before canceled boundary must be discarded")

        def synthesize(self, text, **options):
            assert self.frames == 0
            return b"fixture"

    def output(kind, session, **values):
        events.append((kind, session))
        if kind == "listening":
            utterance(inbox, -1)
            inbox.put({"type": "cancel", "session": 4})
            inbox.put(pcm(-1))
            inbox.put({"type": "synthesize", "session": 4, "text": "done"})
            inbox.put({"type": "quit"})

    inbox.put(start(-1))
    assert run_worker(Stub(), inbox, output) == 0
    assert events == [("listening", -1), ("speech", 4)]
