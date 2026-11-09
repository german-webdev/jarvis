using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

// Uses the existing single STA Application. Every device, packet, worker and HTTP request is fake.
internal static class TaskVoiceInterruptViewModelChecks
{
    internal static async Task VerifyAsync(string parent)
    {
        await ExactStopAndStaleEvents(parent);
        await CompletionBeforeSpeech(parent);
        await NoMicrophoneForTextOrUnauthorizedVoice(parent);
        foreach (var reason in new[] { "stop", "model", "aba", "account", "toggle", "disabled", "restart", "dispose", "error", "transport", "audio" })
            await Invalidation(parent, reason);
    }

    private static async Task ExactStopAndStaleEvents(string parent)
    {
        await using var f = new Fixture(parent);
        await f.Ready();
        var pending = f.BeginVoiceTask();
        Assert.Equal(1, f.Requests);
        var capture = f.Audio.Session;
        Assert.True(capture < 0); Assert.True(f.Runtime.MicrophoneActive); Assert.True(f.Runtime.CanStop);
        Assert.Equal(Ui.Text("JARVIS слушает"), f.Runtime.MicrophoneLabel);
        var log = f.Runtime.History.ToArray(); var transcript = f.Runtime.Transcript; var status = f.Runtime.Status;
        foreach (var phrase in new[] { "стоп", "stop", "отмена", "не останови задачу", "отмени задачу и открой браузер", "спроси нейросеть новое" })
            await f.Receive(new { type = "task_stop", session = capture, text = phrase });
        foreach (var type in new[] { "transcript", "processing", "empty", "command_timeout", "awaiting_command", "activated" })
        {
            await f.Receive(new { type, session = capture, text = "спроси нейросеть новое", elapsed_ms = 1 });
            await f.Receive(new { type, session = f.VoiceSession, text = "late ordinary transcript", elapsed_ms = 1 });
        }
        Assert.Equal(log, f.Runtime.History); Assert.Equal(transcript, f.Runtime.Transcript);
        Assert.Equal(status, f.Runtime.Status); Assert.Equal(1, f.Requests); Assert.Equal(0, f.Actions);
        f.Audio.Packet(capture);
        Assert.Equal(capture, f.Worker.Messages.Last().GetProperty("session").GetInt64());
        var messages = f.Worker.Messages.Count;
        f.Audio.Packet(f.VoiceSession); // A callback from the original capture races with interrupt Start.
        Assert.Equal(messages, f.Worker.Messages.Count);

        await f.Receive(new { type = "task_stop", session = capture, text = "Джарвис, останови задачу!" });
        await Canceled(pending);
        Assert.False(f.Runtime.Busy); Assert.False(f.Runtime.MicrophoneActive); Assert.False(f.Audio.IsCapturing);

        var old = f.Pending;
        f.Pending = NewResponse();
        var next = f.BeginVoiceTask(); var nextCapture = f.Audio.Session;
        Assert.NotEqual(capture, nextCapture);
        var nextLog = f.Runtime.History.ToArray();
        await f.Receive(new { type = "task_stop", session = capture, text = "cancel task" });
        await f.Receive(new { type = "error", session = capture, text = "late capture error" });
        f.Audio.Packet(capture); f.Audio.Error(capture);
        old.SetResult(AiLocalActionServiceTests.Tool());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(f.Runtime.Busy); Assert.True(f.Runtime.MicrophoneActive); Assert.Equal(nextCapture, f.Audio.Session);
        Assert.Equal(nextLog, f.Runtime.History); Assert.Equal(0, f.Actions); Assert.Equal(2, f.Requests);
        await f.Receive(new { type = "task_stop", session = nextCapture, text = "cancel task" });
        await Canceled(next);
        f.Pending.SetResult(AiLocalActionServiceTests.Tool());
    }

    private static async Task CompletionBeforeSpeech(string parent)
    {
        await using var f = new Fixture(parent);
        await f.Ready();
        var pending = f.BeginVoiceTask(); var capture = f.Audio.Session;
        f.Pending.SetResult(AiLocalActionServiceTests.Tool());
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Actions);
        Assert.False(f.Runtime.MicrophoneActive); Assert.False(f.Audio.IsCapturing);
        Assert.False(f.Worker.CapturedDuringSpeech);
        Assert.Equal(AssistantPhase.Synthesizing, f.Runtime.Phase);
        var speechIndex = f.Worker.Messages.FindIndex(x => x.GetProperty("type").GetString() == "synthesize");
        Assert.True(speechIndex > 0);
        Assert.Equal("cancel", f.Worker.Messages[speechIndex - 1].GetProperty("type").GetString());
        var reply = f.Runtime.Reply; var logs = f.Runtime.History.ToArray();
        await f.Receive(new { type = "task_stop", session = capture, text = "stop task" });
        f.Audio.Packet(capture); f.Audio.Error(capture);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(AssistantPhase.Synthesizing, f.Runtime.Phase);
        Assert.Equal(reply, f.Runtime.Reply); Assert.Equal(logs, f.Runtime.History);
        Assert.False(f.Runtime.MicrophoneActive);
        f.Runtime.Stop(); // Do not play speech or resume follow-up in this check.
    }

    private static async Task NoMicrophoneForTextOrUnauthorizedVoice(string parent)
    {
        await using var f = new Fixture(parent);
        await f.Ready();
        var textTask = f.Runtime.RunTextAsync("AI task: open Fixture");
        Assert.Equal(0, f.Audio.Starts); Assert.False(f.Runtime.MicrophoneActive);
        await f.Receive(new { type = "task_stop", session = -1, text = "stop task" });
        Assert.True(f.Runtime.Busy);
        f.Runtime.Stop(); await textTask;
        f.Pending.SetResult(AiLocalActionServiceTests.Tool());

        f.Pending = NewResponse();
        f.Audio.AllowCapture = false;
        var voice = f.BeginVoiceTask(expectCapture: false);
        Assert.Equal(1, f.Audio.Starts); Assert.False(f.Runtime.MicrophoneActive);
        Assert.DoesNotContain(f.Worker.Messages, m => m.TryGetProperty("mode", out var mode) && mode.GetString() == "task_interrupt");
        f.Runtime.Stop(); await Canceled(voice); f.Pending.SetResult(AiLocalActionServiceTests.Tool());

        // Ordinary voice AI keeps the existing capture-off behavior while awaiting the model.
        f.Pending = NewResponse(); f.Audio.AllowCapture = true;
        var ordinary = f.BeginVoiceTask(command: "Спроси нейросеть два варианта");
        Assert.False(f.Runtime.MicrophoneActive);
        Assert.DoesNotContain(f.Worker.Messages, m => m.TryGetProperty("mode", out var mode) && mode.GetString() == "task_interrupt");
        f.Runtime.Stop(); f.Pending.SetResult(AiLocalActionServiceTests.TextAnswer()); await Canceled(ordinary);
    }

    private static async Task Invalidation(string parent, string reason)
    {
        await using var f = new Fixture(parent);
        await f.Ready();
        var pending = f.BeginVoiceTask(); var capture = f.Audio.Session;
        Assert.False(pending.IsCompleted, reason);
        var profile = f.Runtime.Ai.Configuration.Active!;
        switch (reason)
        {
            case "stop": f.Runtime.Stop(); break;
            case "model": f.Runtime.Ai.Save(f.Runtime.Ai.Configuration with { Connections = [profile with { Model = "other" }] }); break;
            case "aba":
                f.Runtime.Ai.Save(f.Runtime.Ai.Configuration with { Connections = [profile with { Model = "other" }] });
                f.Runtime.Ai.Save(f.Runtime.Ai.Configuration with { Connections = [profile] }); break;
            case "account": await f.Runtime.Ai.ChatGpt.SignOutAsync(profile, CancellationToken.None); break;
            case "toggle": f.Runtime.Ai.Save(f.Runtime.Ai.Configuration with { AllowLocalActions = false }); break;
            case "disabled": f.Runtime.Ai.Save(f.Runtime.Ai.Configuration with { Enabled = false }); break;
            case "restart": await f.Runtime.RestartAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); break;
            case "dispose": await f.Runtime.DisposeAsync(); break;
            case "error": await f.Receive(new { type = "error", session = capture, text = "fixture" }); break;
            case "transport": f.Worker.TransportError(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); break;
            case "audio": f.Audio.Error(capture); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); break;
        }
        var cancellation = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(cancellation is OperationCanceledException, reason + ": " + (cancellation?.ToString() ?? "completed without cancellation"));
        Assert.False(f.Runtime.Busy); Assert.False(f.Runtime.MicrophoneActive); Assert.False(f.Audio.IsCapturing);
        var starts = f.Audio.Starts; var reply = f.Runtime.Reply; var log = f.Runtime.History.ToArray();
        f.Pending.SetResult(AiLocalActionServiceTests.Tool());
        await f.Receive(new { type = "task_stop", session = capture, text = "stop task" });
        await f.Receive(new { type = "transcript", session = f.VoiceSession, text = "AI task: open Fixture", elapsed_ms = 1 });
        f.Audio.Packet(capture); f.Audio.Error(capture);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, f.Actions); Assert.Equal(starts, f.Audio.Starts); Assert.Equal(reply, f.Runtime.Reply); Assert.Equal(log, f.Runtime.History);
        Assert.False(f.Runtime.MicrophoneActive);
    }

    private static TaskCompletionSource<HttpResponseMessage> NewResponse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Canceled(Task pending) => await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeAudio Audio { get; } = new();
        public FakeWorker Worker { get; private set; } = null!;
        public MainViewModel Runtime { get; }
        public TaskCompletionSource<HttpResponseMessage> Pending = NewResponse();
        public int Requests;
        public int Actions;
        public long VoiceSession;
        private TaskCompletionSource<HttpResponseMessage>? _lastPending;

        public Fixture(string parent)
        {
            var root = Path.Combine(parent, "voice-task-" + Guid.NewGuid());
            var http = new HttpClient(new AiLocalActionServiceTests.Handler(_ =>
            {
                Requests++;
                if (ReferenceEquals(Pending, _lastPending)) return Task.FromResult(AiLocalActionServiceTests.TextAnswer());
                _lastPending = Pending; return Pending.Task;
            }));
            var ai = new AiService(root, http);
            var skills = new SkillDispatcher([new DelegateSkill([Intent.LaunchTarget, Intent.FocusTarget], (_, _) =>
            { Actions++; return Task.FromResult(new SkillResult(false, "fixture")); })]);
            Runtime = new MainViewModel(root, [AiLocalActionServiceTests.FixtureTarget], skills: skills, ai: ai,
                actionWindows: new AiLocalActionServiceTests.Windows(), audio: Audio,
                workerFactory: () => Worker = new FakeWorker(Audio));
            AiLocalActionServiceTests.Configure(ai, root);
        }

        public async Task Ready() { await Runtime.RestartAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.True(Runtime.Ready); }
        public Task Receive(object message) => (Task)typeof(MainViewModel).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Runtime, [JsonSerializer.SerializeToElement(message)])!;
        public Task BeginVoiceTask(bool expectCapture = true, string command = "AI task: open Fixture")
        {
            Runtime.Listen(); VoiceSession = Audio.Session;
            Assert.Equal(expectCapture, Runtime.MicrophoneActive);
            // Button-mode recognition stops capture before transcript; authorization must survive this.
            Receive(new { type = "processing", session = VoiceSession }).GetAwaiter().GetResult();
            Assert.False(Runtime.MicrophoneActive);
            return Receive(new { type = "transcript", session = VoiceSession, text = command, elapsed_ms = 1 });
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class FakeWorker(FakeAudio audio) : IWorkerClient
    {
        public List<JsonElement> Messages { get; } = [];
        public bool CapturedDuringSpeech;
        public event Action<JsonElement>? Message;
        public event Action<string>? Failed;
        public void Start(string root) => Message?.Invoke(JsonSerializer.SerializeToElement(new { type = "ready", session = 0 }));
        public bool Send(object message)
        {
            var element = JsonSerializer.SerializeToElement(message); Messages.Add(element);
            if (element.GetProperty("type").GetString() == "synthesize") CapturedDuringSpeech |= audio.IsCapturing;
            return true;
        }
        public void TransportError() => Failed?.Invoke("fixture transport error");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAudio : IAudioService
    {
        public event Action<long, byte[]>? Audio;
        public event Action<float>? Level { add { } remove { } }
        public event Action<float>? PlaybackLevel { add { } remove { } }
        public event Action<long, string>? Failed;
        public event Action? CaptureStateChanged;
        public bool IsCapturing { get; private set; }
        public bool AllowCapture = true;
        public int Starts;
        public long Session;
        public IReadOnlyList<Microphone> GetDevices() => [new("fake", "Fixture microphone")];
        public string GetDefaultDeviceId() => "fake";
        public void Start(string id, long session) { Starts++; Session = session; IsCapturing = AllowCapture; CaptureStateChanged?.Invoke(); }
        public void StopCapture() { IsCapturing = false; CaptureStateChanged?.Invoke(); }
        public Task PlayAsync(byte[] wav, CancellationToken token, float volume = 0.8f) => throw new InvalidOperationException("No playback permitted in fixture");
        public void StopPlayback() { }
        public void Packet(long session) => Audio?.Invoke(session, new byte[1024]);
        public void Error(long session) => Failed?.Invoke(session, "fixture capture error");
        public void Dispose() => StopCapture();
    }
}
