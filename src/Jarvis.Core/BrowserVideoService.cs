using System.Text.Json;

namespace Jarvis.Core;

public interface IVideoBrowserSession : IAsyncDisposable
{
    bool Alive { get; }
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken token);
    string? Problem { get; }
    void ExpectNavigation(string url);
    void Stop();
}

/// <summary>Fixed DOM readers, bounded output, one selected page. The page supplies data, never instructions.</summary>
public sealed class BrowserVideoService(Func<CancellationToken, Task<IVideoBrowserSession>> connect, TimeSpan? operationTimeout = null) : IBrowserVideoService
{
    private IVideoBrowserSession? _browser;
    private CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1);
    private BrowserVideo[] _results = [];
    private string? _searchUrl;
    private long _generation;
    private bool _disposed;
    public bool Connected => !_disposed && _browser?.Alive == true && _browser.Problem == null;
    // These expressions are constants. Query text, URL, title and model output are never evaluated as code.
    public const string ReadPage = """
        (() => ({url:location.href, title:document.title.slice(0,300), ready:document.readyState,
          canonical:document.querySelector('link[rel="canonical"]')?.href || '',
          consent:!!document.querySelector('ytd-consent-bump-v2-lightbox, ytd-consent-bump-lightbox, form[action*="consent.youtube"], form[action*="consent.google"]'),
          search:!!document.querySelector('ytd-search'),
          videos:Array.from(document.querySelectorAll('ytd-video-renderer')).slice(0,20).map(e => ({
            url:e.querySelector('a#video-title')?.href || '',
            title:(e.querySelector('a#video-title')?.textContent || '').trim().slice(0,300),
            channel:(e.querySelector('ytd-channel-name a')?.textContent || '').trim().slice(0,150)}))}))()
        """;

    public async Task ConnectAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var generation = _generation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BrowserVideoService));
            if (Connected) return;
            if (_browser != null) { await _browser.DisposeAsync(); _browser = null; }
            var browser = await connect(timeout.Token);
            if (generation != _generation || timeout.IsCancellationRequested || _disposed)
            { await browser.DisposeAsync(); throw new OperationCanceledException(); }
            _browser = browser; _results = []; _searchUrl = null;
        }
        finally { _gate.Release(); }
    }
    public Task<BrowserVideoResult> SearchAsync(string query, CancellationToken token) => RunAsync(async (browser, ct) =>
    {
        _results = []; _searchUrl = null;
        if (string.IsNullOrWhiteSpace(query) || query.Length > 300 || query.Any(char.IsControl)) return Failure("Укажите запрос от 1 до 300 символов.");
        var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query.Trim());
        var page = await NavigateAsync(browser, url, ct);
        if (!page.GetProperty("search").GetBoolean()) return Failure("Страница поиска не распознана. Проверьте браузер и повторите поиск.");
        List<BrowserVideo> videos = [];
        foreach (var item in page.GetProperty("videos").EnumerateArray())
        {
            if (!BrowserVideoCommands.TryVideoUrl(item.GetProperty("url").GetString(), out var id)) continue;
            var title = Clean(item.GetProperty("title").GetString(), 300);
            var channel = Clean(item.GetProperty("channel").GetString(), 150);
            if (title.Length == 0 || channel.Length == 0 || videos.Any(v => v.Id == id)) continue;
            videos.Add(new(id, title, channel)); if (videos.Count == 5) break;
        }
        ct.ThrowIfCancellationRequested(); Check(browser);
        _results = videos.ToArray(); _searchUrl = url;
        return videos.Count == 0 ? Failure("Видео не найдены или страница изменилась. Попробуйте другой запрос.")
            : new(true, "Выберите видео по номеру или в списке.", Array.AsReadOnly(_results));
    }, token);

    public Task<BrowserVideoResult> OpenAsync(BrowserVideo video, CancellationToken token) => RunAsync(async (browser, ct) =>
    {
        if (!BrowserVideoCommands.ValidId(video.Id) || !_results.Contains(video) || _searchUrl == null)
            return Failure("Выбор устарел. Повторите поиск видео.");
        var before = await ReadAsync(browser, ct);
        if (before.GetProperty("url").GetString() != _searchUrl || before.GetProperty("consent").GetBoolean())
        { _results = []; return Failure("Вкладка изменилась. Повторите поиск видео."); }
        var stillPresent = before.GetProperty("videos").EnumerateArray().Any(item =>
            BrowserVideoCommands.TryVideoUrl(item.GetProperty("url").GetString(), out var id) && id == video.Id
            && Clean(item.GetProperty("title").GetString(), 300) == video.Title
            && Clean(item.GetProperty("channel").GetString(), 150) == video.Channel);
        if (!stillPresent) { _results = []; return Failure("Выбор устарел. Повторите поиск видео."); }
        _results = []; _searchUrl = null; // consume once, including timeout/failure
        var page = await NavigateAsync(browser, video.Url, ct);
        if (!BrowserVideoCommands.TryVideoUrl(page.GetProperty("canonical").GetString(), out var canonicalId) || canonicalId != video.Id)
            return Failure("Адрес видео не подтверждён. Проверьте вкладку браузера.");
        var title = Clean(page.GetProperty("title").GetString(), 300);
        if (title.Length == 0 || title is "YouTube" or "YouTube - YouTube") return Failure("Название видео не подтверждено. Проверьте вкладку браузера.");
        return new(true, "Страница видео открыта. Воспроизведение не проверено.", [], video.Url, title);
    }, token);

    private async Task<BrowserVideoResult> RunAsync(Func<IVideoBrowserSession, CancellationToken, Task<BrowserVideoResult>> action, CancellationToken token)
    {
        var generation = _generation;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(operationTimeout is { } duration && duration > TimeSpan.Zero && duration < TimeSpan.FromSeconds(30) ? duration : TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(timeout.Token);
        try
        {
            if (generation != _generation) throw new OperationCanceledException();
            if (!Connected) return Failure("Сначала откройте управляемый браузер в разделе «Сайты → Видео YouTube».");
            using var stopOnCancel = timeout.Token.Register(() => _browser?.Stop());
            var result = await action(_browser!, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (generation != _generation) throw new OperationCanceledException();
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && generation == _generation && !_lifetime.IsCancellationRequested)
        { _browser?.Stop(); _results = []; return Failure(_browser?.Problem ?? "Браузер не ответил вовремя. Повторите поиск."); }
        catch (InvalidDataException ex) { _results = []; return Failure(ex.Message); }
        catch (Exception) when (!token.IsCancellationRequested && generation == _generation)
        { _results = []; return Failure("Связь с браузером потеряна. Откройте управляемый браузер заново."); }
        finally { _gate.Release(); }
    }
    private static async Task<JsonElement> NavigateAsync(IVideoBrowserSession browser, string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Check(browser);
        browser.ExpectNavigation(url);
        var nav = await browser.CallAsync("Page.navigate", new { url }, token);
        if (nav.TryGetProperty("errorText", out _)) { Check(browser); throw new InvalidDataException("Страница недоступна. Проверьте браузер и подключение к интернету."); }
        var loader = nav.TryGetProperty("loaderId", out var loaderId) ? loaderId.GetString() : null;
        var started = DateTime.UtcNow;
        while (true)
        {
            token.ThrowIfCancellationRequested(); Check(browser);
            if (loader != null)
            {
                var tree = await browser.CallAsync("Page.getFrameTree", null, token);
                if (tree.GetProperty("frameTree").GetProperty("frame").GetProperty("loaderId").GetString() != loader)
                { await Task.Delay(150, token); continue; }
            }
            JsonElement page;
            try { page = await ReadAsync(browser, token); }
            catch (InvalidOperationException) { await Task.Delay(150, token); continue; }
            var observed = page.GetProperty("url").GetString();
            if (page.GetProperty("consent").GetBoolean()) throw new InvalidDataException("Сайт требует согласие или вход. Автоматическое действие остановлено. Откройте видео самостоятельно в обычном браузере.");
            // The old document can remain briefly while navigation commits. Never accept it.
            if (observed == url && page.GetProperty("ready").GetString() == "complete")
            {
                if (url.Contains("/results?", StringComparison.Ordinal) && page.GetProperty("search").GetBoolean()
                    && page.GetProperty("videos").GetArrayLength() == 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(3)) { await Task.Delay(250, token); continue; }
                if (url.Contains("/watch?", StringComparison.Ordinal) && DateTime.UtcNow - started < TimeSpan.FromSeconds(3)
                    && (!BrowserVideoCommands.TryVideoUrl(page.GetProperty("canonical").GetString(), out var id)
                        || !BrowserVideoCommands.TryVideoUrl(url, out var wanted) || id != wanted
                        || string.IsNullOrWhiteSpace(page.GetProperty("title").GetString()) || page.GetProperty("title").GetString() == "YouTube"))
                { await Task.Delay(150, token); continue; }
                return page;
            }
            await Task.Delay(150, token);
        }
    }
    private static async Task<JsonElement> ReadAsync(IVideoBrowserSession browser, CancellationToken token)
    {
        Check(browser);
        var response = await browser.CallAsync("Runtime.evaluate", new { expression = ReadPage, returnByValue = true }, token);
        token.ThrowIfCancellationRequested(); Check(browser);
        if (response.TryGetProperty("exceptionDetails", out _) || !response.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value))
            throw new InvalidOperationException("Document changed.");
        return value;
    }
    private static void Check(IVideoBrowserSession browser)
    { if (browser.Problem != null) throw new InvalidDataException(browser.Problem); if (!browser.Alive) throw new IOException(); }
    private static string Clean(string? value, int max) => new((value ?? "").Where(c => !char.IsControl(c)).Take(max).ToArray());
    private static BrowserVideoResult Failure(string message) => new(false, message, []);
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _results = []; _searchUrl = null; _browser?.Stop();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; Stop();
        await _gate.WaitAsync();
        try { if (_browser != null) await _browser.DisposeAsync(); _browser = null; }
        finally { _gate.Release(); _lifetime.Dispose(); }
    }
}
