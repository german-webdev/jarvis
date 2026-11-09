using System.IO;
using System.Text.Json;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

[CollectionDefinition("Managed browser fixture", DisableParallelization = true)]
public sealed class ManagedBrowserCollection { }

[Collection("Managed browser fixture")]
public class ManagedVideoBrowserTests
{
    // Real installed Chromium, own headless process/profile; all documents fulfilled from local fixtures,
    // DNS blocked, no user tabs/accounts/audio/screenshots. This is NOT a live YouTube test.
    [Fact]
    public async Task RealBrowserFixturesSearchChooseGuardAndCancel()
    {
        Assert.NotNull(ManagedVideoBrowser.FindBrowser());
        var root = FindRoot();
        var fixtures = Path.Combine(root, "tests", "fixtures", "browser-video");
        string Read(string name) => File.ReadAllText(Path.Combine(fixtures, name + ".html"));
        string mode = "search", watch = "watch";
        ManagedVideoBrowser? browser = null;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        await using var service = new BrowserVideoService(async token =>
        {
            browser = (ManagedVideoBrowser)await ManagedVideoBrowser.LaunchAsync(root, url => Read(url.Contains("/watch?") ? watch : mode), token);
            return browser;
        });
        await service.ConnectAsync(limit.Token);
        var results = await service.SearchAsync("fixture", limit.Token);
        Assert.True(results.Verified, results.Message); Assert.Equal(2, results.Videos.Count);
        var opened = await service.OpenAsync(results.Videos[1], limit.Token);
        Assert.True(opened.Verified, opened.Message); Assert.Equal("https://www.youtube.com/watch?v=12345678901", opened.Url);
        Assert.Equal("Fixture second video - YouTube", opened.Title); Assert.Contains("Воспроизведение не проверено", opened.Message);

        watch = "watch-delayed";
        results = await service.SearchAsync("delayed", limit.Token);
        opened = await service.OpenAsync(results.Videos[1], limit.Token);
        Assert.True(opened.Verified, opened.Message); Assert.Equal("Fixture second video - YouTube", opened.Title);

        results = await service.SearchAsync("fixture", limit.Token);
        await browser!.CallAsync("Runtime.evaluate", new { expression = "document.querySelector('ytd-video-renderer').remove()" }, limit.Token);
        var stale = await service.OpenAsync(results.Videos[0], limit.Token);
        Assert.False(stale.Verified); Assert.Contains("устарел", stale.Message);

        mode = "unsupported";
        var unsupported = await service.SearchAsync("fixture", limit.Token);
        Assert.False(unsupported.Verified); Assert.Contains("не распознана", unsupported.Message);
        mode = "consent";
        var consent = await service.SearchAsync("fixture", limit.Token);
        Assert.False(consent.Verified); Assert.Contains("согласие", consent.Message);

        mode = "redirect";
        var redirect = await service.SearchAsync("fixture", limit.Token);
        Assert.False(redirect.Verified); Assert.Contains("адрес", redirect.Message);
        Assert.False(service.Connected);
        await service.ConnectAsync(limit.Token); // recovery actually creates a fresh owned session
        mode = "newtab";
        var newtab = await service.SearchAsync("fixture", limit.Token);
        Assert.False(newtab.Verified); Assert.Contains("вкладк", newtab.Message);
        Assert.False(service.Connected);
        await service.ConnectAsync(limit.Token);

        mode = "search";
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser!.BeforeSendAsync = method =>
        {
            if (method != "Fetch.fulfillRequest") return Task.CompletedTask;
            paused.TrySetResult(); return release.Task;
        };
        var pending = service.SearchAsync("cancel", limit.Token);
        await paused.Task.WaitAsync(limit.Token);
        service.Stop(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        browser.BeforeSendAsync = null;
        var observed = await browser.CallAsync("Runtime.evaluate", new { expression = "location.href", returnByValue = true }, limit.Token);
        Assert.Equal("about:blank", observed.GetProperty("result").GetProperty("value").GetString());
        // New search works after Stop; canceled late fulfillment did not navigate the page.
        results = await service.SearchAsync("again", limit.Token);
        Assert.True(results.Verified, results.Message);

        // A new page while a navigation is queued cancels that navigation before socket send.
        paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.BeforeSendAsync = method =>
        {
            if (method != "Page.navigate") return Task.CompletedTask;
            paused.TrySetResult(); return release.Task;
        };
        pending = service.SearchAsync("must-not-navigate", limit.Token);
        await paused.Task.WaitAsync(limit.Token);
        await browser.CallAsync("Runtime.evaluate", new { expression = "window.open('about:blank', '_blank')" }, limit.Token);
        while (browser.Problem == null) await Task.Delay(10, limit.Token);
        release.SetResult();
        var canceledNavigation = await pending;
        Assert.False(canceledNavigation.Verified); Assert.Contains("вкладк", canceledNavigation.Message);
        observed = await browser.CallAsync("Runtime.evaluate", new { expression = "location.href", returnByValue = true }, limit.Token);
        Assert.DoesNotContain("must-not-navigate", observed.GetProperty("result").GetProperty("value").GetString());
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Jarvis.App"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run fixture tests from repository build output.");
    }
}
