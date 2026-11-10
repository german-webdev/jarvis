using System.Text.Json;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class BrowserVideoTests
{
    [Theory]
    [InlineData("найди видео природа", "природа")]
    [InlineData("Find video test", "test")]
    [InlineData("найди видео", "")]
    [InlineData("открой браузер", null)]
    public void ExplicitCommands(string text, string? expected) => Assert.Equal(expected, BrowserVideoCommands.Query(text));
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk", true)]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk&list=test", true)]
    [InlineData("https://www.youtube.com.evil.test/watch?v=abcdefghijk", false)]
    [InlineData("https://user@www.youtube.com/watch?v=abcdefghijk", false)]
    [InlineData("https://www.youtube.com:444/watch?v=abcdefghijk", false)]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk&v=12345678901", false)]
    [InlineData("https://www.youtube.com/watch?v=../evil", false)]
    [InlineData("javascript:alert(1)", false)]
    public void VideoUrlsAreBounded(string url, bool valid) => Assert.Equal(valid, BrowserVideoCommands.TryVideoUrl(url, out _));

    [Fact]
    public async Task ProductionAlgorithmSearchSelectConfirmsPageWithoutPlayer()
    {
        var fake = new Browser(); await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(fake));
        Assert.False((await service.SearchAsync("test", default)).Verified); Assert.Empty(fake.Methods);
        await service.ConnectAsync(default);
        var result = await service.SearchAsync("one & two", default);
        Assert.True(result.Verified); Assert.Equal(2, result.Videos.Count);
        Assert.Equal("https://www.youtube.com/results?search_query=one%20%26%20two", fake.Url);
        Assert.Single(fake.Methods, m => m == "Page.navigate"); // no automatic selection
        result = await service.OpenAsync(result.Videos[1], default);
        Assert.True(result.Verified); Assert.Equal("https://www.youtube.com/watch?v=12345678901", result.Url);
        Assert.Equal("Fixture video — YouTube", result.Title); Assert.Contains("Воспроизведение не проверено", result.Message);
        Assert.False((await service.OpenAsync(new("12345678901", "Second", "Channel B"), default)).Verified);
    }
    [Theory]
    [InlineData("dom")]
    [InlineData("title")]
    [InlineData("channel")]
    [InlineData("url")]
    [InlineData("stop")]
    public async Task StaleChoiceNeverNavigates(string reason)
    {
        var fake = new Browser(); await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(fake));
        await service.ConnectAsync(default); var result = await service.SearchAsync("test", default);
        switch (reason)
        {
            case "dom": fake.Items = []; break;
            case "title": fake.Items[0] = fake.Items[0] with { Title = "changed" }; break;
            case "channel": fake.Items[0] = fake.Items[0] with { Channel = "changed" }; break;
            case "url": fake.Url = "https://example.test/"; break;
            case "stop": service.Stop(); break;
        }
        Assert.False((await service.OpenAsync(result.Videos[0], default)).Verified);
        Assert.Single(fake.Methods, m => m == "Page.navigate");
    }
    [Theory]
    [InlineData("consent")]
    [InlineData("canonical")]
    [InlineData("title")]
    [InlineData("redirect")]
    public async Task UnconfirmedPageIsNotSuccess(string reason)
    {
        var fake = new Browser(); await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(fake));
        await service.ConnectAsync(default); var result = await service.SearchAsync("test", default);
        fake.Failure = reason;
        Assert.False((await service.OpenAsync(result.Videos[0], default)).Verified);
    }
    [Fact]
    public async Task BrokenButAliveSessionIsReplacedOnlyOnExplicitConnect()
    {
        var fake = new Browser(); var next = new Browser(); int connects = 0;
        await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(++connects == 1 ? fake : next));
        await service.ConnectAsync(default); fake.Problem = "Blocked";
        Assert.False(service.Connected); Assert.False((await service.SearchAsync("test", default)).Verified); Assert.Equal(1, connects);
        await service.ConnectAsync(default); Assert.True(fake.Disposed); Assert.Equal(2, connects); Assert.True(service.Connected);
    }
    [Fact]
    public async Task LateTransportAfterStopCannotRestoreResults()
    {
        var fake = new Browser { Pause = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(fake));
        await service.ConnectAsync(default); var work = service.SearchAsync("test", default);
        service.Stop(); fake.Pause.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.False((await service.OpenAsync(fake.Items[0], default)).Verified);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public async Task InvalidQueryDoesNotNavigate(int count)
    {
        var fake = new Browser(); await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(fake));
        await service.ConnectAsync(default); Assert.False((await service.SearchAsync(new string('x', count), default)).Verified); Assert.Empty(fake.Methods);
    }

    [Fact]
    public async Task TimeoutStopsBrowserAndCannotReportSuccess()
    {
        var fake = new Browser { NeverReady = true };
        await using var service = new BrowserVideoService(_ => Task.FromResult<IVideoBrowserSession>(fake), TimeSpan.FromMilliseconds(200));
        await service.ConnectAsync(default);
        var result = await service.SearchAsync("timeout", default);
        Assert.False(result.Verified); Assert.Contains("вовремя", result.Message); Assert.True(fake.Stops > 0);
    }

    private sealed class Browser : IVideoBrowserSession
    {
        public bool Alive => !Disposed;
        public bool Disposed;
        public string? Problem { get; set; }
        public string Url = "about:blank";
        public string? Failure;
        public List<string> Methods = [];
        public BrowserVideo[] Items = [new("abcdefghijk", "First", "Channel A"), new("12345678901", "Second", "Channel B")];
        public TaskCompletionSource? Pause;
        public bool NeverReady; public int Stops;
        public void ExpectNavigation(string url) { }
        public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken token)
        {
            Methods.Add(method);
            if (method == "Page.navigate")
            {
                Url = JsonSerializer.SerializeToElement(parameters).GetProperty("url").GetString()!;
                if (Failure == "redirect") Problem = "Переход на другой адрес заблокирован. Повторите поиск в управляемой вкладке.";
                return JsonSerializer.SerializeToElement(new { });
            }
            Assert.Equal("Runtime.evaluate", method);
            Assert.Equal(BrowserVideoService.ReadPage, JsonSerializer.SerializeToElement(parameters).GetProperty("expression").GetString());
            if (Pause != null) await Pause.Task;
            if (NeverReady) await Task.Delay(Timeout.Infinite, token);
            return JsonSerializer.SerializeToElement(new { result = new { value = new { url = Url, title = Failure == "title" ? "YouTube" : "Fixture video — YouTube", ready = "complete",
                canonical = Failure == "canonical" ? "https://evil.test" : Url, consent = Failure == "consent", search = true,
                videos = Items.Select(v => new { url = v.Url, title = v.Title, channel = v.Channel }).ToArray() } } });
        }
        public void Stop() { Stops++; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
