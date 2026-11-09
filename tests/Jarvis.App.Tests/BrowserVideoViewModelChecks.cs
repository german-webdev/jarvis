using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;
namespace Jarvis.App.Tests;
internal static class BrowserVideoViewModelChecks
{
    internal static async Task VerifyAsync(string root)
    {
        var audio = new FakeAudio(); var worker = new FakeWorker(audio); var browser = new FakeBrowser();
        await using var runtime = new MainViewModel(Path.Combine(root, "browser-vm"), [], skills: new SkillDispatcher([]), audio: audio, workerFactory: () => worker, videoBrowser: browser);
        await runtime.RestartAsync(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(runtime.Ready); Assert.Equal(0, browser.Connects);
        await runtime.ConnectVideoBrowserAsync(); Assert.Equal(1, browser.Connects);
        await runtime.RunTextAsync("найди видео fixture");
        Assert.Equal("fixture", browser.Query); Assert.Equal(2, runtime.VideoResults.Count); Assert.True(runtime.AwaitingChoice);
        Assert.Equal(0, audio.Starts);
        await runtime.RunTextAsync("вариант два"); Assert.Equal("12345678901", browser.Opened); Assert.Empty(runtime.VideoResults);
        Assert.Contains("https://www.youtube.com/watch?v=12345678901", runtime.Reply);
        await runtime.SearchVideoAsync("fixture"); runtime.Stop(); Assert.Empty(runtime.VideoResults); Assert.False(runtime.AwaitingChoice);
        await runtime.SearchVideoAsync("fixture");
        runtime.Listen(); Assert.Empty(runtime.VideoResults); runtime.Stop();
        // UI search consumes any old power confirmation, so its numeric answer goes to video selection.
        await runtime.RunTextAsync("выключи компьютер"); Assert.True(runtime.AwaitingChoice);
        await runtime.SearchVideoAsync("fixture"); await runtime.RunTextAsync("вариант один"); Assert.Equal("abcdefghijk", browser.Opened);
        browser.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Listen(); var session = audio.Session;
        Task Receive(object value) => (Task)typeof(MainViewModel).GetMethod("HandleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(runtime, [JsonSerializer.SerializeToElement(value)])!;
        await Receive(new { type = "processing", session });
        var pending = Receive(new { type = "transcript", session, text = "найди видео fixture", elapsed_ms = 1 });
        Assert.True(runtime.BrowserPending); Assert.True(audio.Session < 0); Assert.True(runtime.MicrophoneActive);
        await Receive(new { type = "task_stop", session = audio.Session, text = "останови задачу" });
        Assert.False(runtime.MicrophoneActive); Assert.False(runtime.Busy);
        var reply = runtime.Reply; browser.Pending.SetResult(FakeBrowser.Result()); await pending;
        Assert.Equal(reply, runtime.Reply); Assert.Empty(runtime.VideoResults);
        browser.Pending = null;
        await runtime.SearchVideoAsync("fixture");
        Assert.False(runtime.MicrophoneActive); Assert.False(worker.CapturedDuringSpeech);
    }
    private sealed class FakeBrowser : IBrowserVideoService
    {
        public int Connects; public string? Query; public string? Opened; public TaskCompletionSource<BrowserVideoResult>? Pending;
        public bool Connected => Connects > 0;
        public Task ConnectAsync(CancellationToken token) { Connects++; return Task.CompletedTask; }
        public static BrowserVideoResult Result() => new(true, "Выбери видео по номеру или в списке.", [new("abcdefghijk", "First", "A"), new("12345678901", "Second", "B")]);
        public Task<BrowserVideoResult> SearchAsync(string query, CancellationToken token) { Query = query; return Pending?.Task ?? Task.FromResult(Result()); }
        public Task<BrowserVideoResult> OpenAsync(BrowserVideo video, CancellationToken token) { Opened = video.Id; return Task.FromResult(new BrowserVideoResult(true, "Страница видео открыта. Воспроизведение не проверено.", [], video.Url, video.Title)); }
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeWorker(FakeAudio audio) : IWorkerClient
    {
        public List<JsonElement> Messages = []; public bool CapturedDuringSpeech;
        public event Action<JsonElement>? Message;
        public event Action<string>? Failed { add { } remove { } }
        public void Start(string root) => Message?.Invoke(JsonSerializer.SerializeToElement(new { type = "ready", session = 0 }));
        public bool Send(object value)
        {
            var message = JsonSerializer.SerializeToElement(value); Messages.Add(message);
            if (message.GetProperty("type").GetString() == "synthesize") CapturedDuringSpeech |= audio.IsCapturing;
            return true;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeAudio : IAudioService
    {
        public event Action<long, byte[]>? Audio { add { } remove { } }
        public event Action<float>? Level { add { } remove { } }
        public event Action<float>? PlaybackLevel { add { } remove { } }
        public event Action<long, string>? Failed { add { } remove { } }
        public event Action? CaptureStateChanged;
        public bool IsCapturing { get; private set; }
        public int Starts; public long Session;
        public IReadOnlyList<Microphone> GetDevices() => [new("fake", "Fixture microphone")];
        public string GetDefaultDeviceId() => "fake";
        public void Start(string id, long session) { Starts++; Session = session; IsCapturing = true; CaptureStateChanged?.Invoke(); }
        public void StopCapture() { IsCapturing = false; CaptureStateChanged?.Invoke(); }
        public Task PlayAsync(byte[] wav, CancellationToken token, float volume = .8f) => throw new InvalidOperationException("Playback prohibited");
        public void StopPlayback() { }
        public void Dispose() => StopCapture();
    }
}
