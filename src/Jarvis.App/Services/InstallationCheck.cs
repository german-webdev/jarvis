using System.IO;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App.Services;

/// <summary>Offline package check: no microphone, playback, accounts, app launches or power actions.</summary>
public static class InstallationCheck
{
    public static async Task<int> RunAsync(InstallationLayout layout, string report)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speech = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerClient();
        worker.Failed += error => { ready.TrySetException(new IOException(error)); speech.TrySetException(new IOException(error)); };
        worker.Message += message =>
        {
            var type = message.GetProperty("type").GetString();
            if (type == "ready") ready.TrySetResult();
            if (type == "speech") speech.TrySetResult(Convert.FromBase64String(message.GetProperty("wav").GetString()!).Length);
            if (type is "error" or "fatal")
            {
                var error = new IOException(message.GetProperty("text").GetString());
                ready.TrySetException(error); speech.TrySetException(error);
            }
        };
        object result;
        var code = 0;
        try
        {
            worker.Start(Path.GetTempPath());
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(2));
            if (!worker.Send(new { type = "synthesize", session = 1, text = "Подтверждаю", voice = "dmitri", style = "natural", amount = 0.0, rate = 1.0 })) throw new IOException("Worker send failed");
            var bytes = await speech.Task.WaitAsync(TimeSpan.FromSeconds(45));
            if (bytes < 1000) throw new IOException("Empty synthesized audio");
            var browserTools = 0;
            if (layout.Installed)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var browser = await PlaywrightBrowserTools.StartAsync(layout.UserRoot, null, deadline.Token);
                browserTools = browser.Tools.Count;
            }
            result = new { success = true, ready = true, speechBytes = bytes, browserTools, assets = layout.Assets, runtime = typeof(object).Assembly.Location };
        }
        catch (Exception ex) { code = 1; result = new { success = false, error = ex.Message }; }
        var path = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return code;
    }
}
