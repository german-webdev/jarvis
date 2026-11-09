using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.App.Services;
using Xunit;

namespace Jarvis.App.Tests;

public class TaskVoiceInterruptTransportTests
{
    [Fact]
    public async Task OrdinaryWakeKeepsItsPreviousAudioCapacity()
    {
        await using var worker = new WorkerClient();
        var queue = (Channel<string>)typeof(WorkerClient).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        Assert.True(worker.Send(new { type = "start", session = 1, mode = "wake" }));
        Assert.True(queue.Reader.TryRead(out _));
        for (var i = 0; i < 128; i++) Assert.True(worker.Send(new { type = "audio", session = 1, pcm = "AAA=" }));
        Assert.Equal(128, queue.Reader.Count);
        Assert.True(worker.Send(new { type = "cancel", session = 2 }));
        Assert.Equal(1, queue.Reader.Count);
    }

    [Fact]
    public async Task AudioBacklogIsBoundedAndCancelReplacesItBeforeSpeech()
    {
        await using var worker = new WorkerClient(); // Never starts Python or opens a device.
        var queue = (Channel<string>)typeof(WorkerClient).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
        Assert.True(worker.Send(new { type = "start", session = -1, mode = "task_interrupt" }));
        for (var i = 0; i < 1000; i++) Assert.True(worker.Send(new { type = "audio", session = -1, pcm = Convert.ToBase64String(new byte[1024]) }));
        Assert.Equal(17, queue.Reader.Count);
        Assert.True(worker.Send(new { type = "cancel", session = 4 }));
        Assert.True(worker.Send(new { type = "synthesize", session = 4, text = "done" }));
        Assert.Equal(2, queue.Reader.Count);
        Assert.True(queue.Reader.TryRead(out var cancel));
        Assert.Equal("cancel", JsonDocument.Parse(cancel!).RootElement.GetProperty("type").GetString());
        Assert.True(queue.Reader.TryRead(out var speech));
        Assert.Equal("synthesize", JsonDocument.Parse(speech!).RootElement.GetProperty("type").GetString());
        Assert.True(worker.Send(new { type = "start", session = -2, mode = "task_interrupt" }));
        Assert.True(worker.Send(new { type = "audio", session = -2, pcm = "AAA=" }));
        Assert.Equal(2, queue.Reader.Count);
    }
}
