using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.App.Services;

internal sealed partial class PlaywrightBrowserTools(McpStdioClient client, DirectoryInfo workspace, Action<string>? progress) : IAiToolSession
{
    private const string Navigate = "browser_navigate";
    private const string Snapshot = "browser_snapshot";
    private const string Unavailable = "Браузерный инструмент недоступен. Проверь установку JARVIS и Microsoft Edge.";
    private const string NavigationError = "Браузер не смог прочитать страницу. Проверь интернет и доступ к сайту.";
    private const string NoResults = "Браузер открыл YouTube, но список видео не подтвердился.";
    private const string Origins = "https://www.youtube.com;https://*.ytimg.com;https://*.googlevideo.com;https://en.wikipedia.org;https://ru.wikipedia.org;https://*.wikimedia.org;https://example.org";
    private static readonly AiToolDefinition ReadPage = new(Navigate,
        "Open and read one public HTTPS page in the browser. For a YouTube search, navigate directly to " +
        "https://www.youtube.com/results?search_query=<URL-encoded query>. Supported pages: YouTube results and watch, " +
        "English and Russian Wikipedia articles, and example.org. Returns actual page data or a specific failure.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { url = new { type = "string" } },
            required = new[] { "url" }, additionalProperties = false }));

    public IReadOnlyList<AiToolDefinition> Tools { get; } = [ReadPage];

    public static async Task<IAiToolSession> StartAsync(string root, Action<string>? progress, CancellationToken token, bool headless = false)
    {
        var assets = InstallationLayout.AssetRoot(AppContext.BaseDirectory, root);
        var runtime = File.Exists(Path.Combine(assets, InstallationLayout.Marker))
            ? Path.Combine(assets, "runtime", "browser") : Path.Combine(assets, ".tools", "playwright-mcp");
        var node = Path.Combine(runtime, "node.exe");
        var cli = Path.Combine(runtime, "node_modules", "@playwright", "mcp", "cli.js");
        if (!File.Exists(node) || !File.Exists(cli)) throw new InvalidDataException(Unavailable);
        var workspace = Directory.CreateTempSubdirectory("jarvis-browser-");
        McpStdioClient? client = null;
        try
        {
            var config = Path.Combine(workspace.FullName, "mcp.json");
            await File.WriteAllTextAsync(config, """{"browser":{"contextOptions":{"acceptDownloads":false}}}""", token);
            var start = new ProcessStartInfo(node) { WorkingDirectory = workspace.FullName };
            // Do not inherit Node preload options, Playwright overrides, or credentials from the app's environment.
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "SystemDrive", "WINDIR", "TEMP", "TMP", "PATH", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            foreach (var argument in new[] { cli, "--config", config, "--browser=msedge", "--isolated", "--sandbox", "--no-webmcp",
                "--block-service-workers", "--codegen=none", "--image-responses=omit", "--snapshot-mode=none", "--timeout-navigation=16000",
                "--output-max-size=1000000", "--allowed-origins=" + Origins }) start.ArgumentList.Add(argument);
            if (headless) start.ArgumentList.Add("--headless");
            client = await McpStdioClient.StartAsync(start, token);
            var names = (await client.ListToolsAsync(token)).Select(tool => tool.GetProperty("name").GetString()).ToHashSet();
            if (!names.Contains(Navigate) || !names.Contains(Snapshot)) throw new InvalidDataException(Unavailable);
            return new PlaywrightBrowserTools(client, workspace, progress);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            if (client != null) await client.DisposeAsync();
            DeleteWorkspace(workspace);
            throw new InvalidDataException(Unavailable);
        }
        catch
        {
            if (client != null) await client.DisposeAsync();
            DeleteWorkspace(workspace);
            throw;
        }
    }

    public async Task<AiToolObservation> InvokeAsync(string name, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (name != Navigate || arguments.ValueKind != JsonValueKind.Object) return Error("Браузерное действие недоступно.");
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Name != "url" || fields[0].Value.ValueKind != JsonValueKind.String
            || !TryPublicPage(fields[0].Value.GetString(), out var requested))
            return Error("Этот адрес недоступен. Браузер поддерживает открытые страницы YouTube и Википедии.");
        try
        {
            progress?.Invoke("Открываю браузер…");
            var navigation = await client.CallAsync(Navigate, JsonSerializer.SerializeToElement(new { url = requested.AbsoluteUri }), token);
            if (Failed(navigation)) return Error(NavigationError);
            var search = IsVideoSearch(requested);
            progress?.Invoke(search ? "Ищу…" : "Читаю страницу…");
            for (var attempt = 0; attempt < (search ? 5 : 1); attempt++)
            {
                if (attempt > 0) await Task.Delay(600, token);
                var result = await client.CallAsync(Snapshot, JsonSerializer.SerializeToElement(new { }), token);
                if (Failed(result)) return Error(NavigationError);
                var text = string.Join('\n', result.GetProperty("content").EnumerateArray()
                    .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
                var observation = Observe(requested, text);
                if (observation.Verified || observation.Display != NoResults) return observation;
            }
            return Error(NoResults);
        }
        catch (TimeoutException) { return Error("Браузер не ответил вовремя. Повтори запрос."); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return Error(NavigationError); }
    }

    private static bool Failed(JsonElement result) => result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;

    internal static AiToolObservation Observe(Uri requested, string text)
    {
        var split = text.IndexOf("\n### Snapshot\n", StringComparison.Ordinal);
        if (split < 0) return Error(NavigationError);
        var headers = text[..split].Split('\n');
        var address = Header("- Page URL: ");
        var title = Header("- Page Title: ");
        if (!TryPublicPage(address, out var actual) || actual.Host != requested.Host
            || (IsVideoSearch(requested) && (!IsVideoSearch(actual) || QueryValue(actual, "search_query") != QueryValue(requested, "search_query"))))
            return Error("Сайт перенаправил браузер или не показал ожидаемую страницу.");
        if (string.IsNullOrWhiteSpace(title)) return Error(IsVideoSearch(actual) ? NoResults : NavigationError);
        var snapshot = text[(split + "\n### Snapshot\n".Length)..];
        var end = snapshot.IndexOf("\n### ", StringComparison.Ordinal);
        if (end >= 0) snapshot = snapshot[..end];
        if (snapshot.Length < 20 || snapshot.StartsWith("- [Snapshot]", StringComparison.Ordinal)) return Error(NavigationError);
        title = Clean(title, 180);
        if (IsVideoSearch(actual))
        {
            var videos = VideoLinks().Matches(snapshot).Select(match => new {
                title = Clean(Regex.Replace(match.Groups[1].Value, "\\\\([\"\\\\])", "$1"), 180),
                url = "https://www.youtube.com/watch?v=" + match.Groups[2].Value
            }).Where(video => video.title.Length > 0).DistinctBy(video => video.url).Take(5).ToArray();
            if (videos.Length == 0) return Error(NoResults);
            return new(true, JsonSerializer.Serialize(new { url = actual.AbsoluteUri, title, videos }),
                title + "\n" + string.Join('\n', videos.Select(video => "• " + video.title)), actual.AbsoluteUri);
        }
        // The page excerpt is evidence and is passed to the model as untrusted data.
        var excerpt = string.Join('\n', snapshot.Split('\n').Where(line => !line.TrimStart().StartsWith("- /url:", StringComparison.Ordinal)));
        return new(true, JsonSerializer.Serialize(new { url = actual.AbsoluteUri, title, snapshot = excerpt[..Math.Min(excerpt.Length, 5500)] }),
            title, actual.AbsoluteUri);

        string? Header(string prefix) => headers.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();
    }

    internal static bool TryPublicPage(string? value, out Uri uri)
    {
        uri = null!;
        if (value == null || value.Length > 2000 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || parsed.UserInfo.Length != 0) return false;
        var allowed = parsed.Host switch
        {
            "www.youtube.com" => IsVideoSearch(parsed) || (parsed.AbsolutePath == "/watch" && VideoId().IsMatch(QueryValue(parsed, "v"))),
            "en.wikipedia.org" or "ru.wikipedia.org" => parsed.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal) && parsed.Query.Length == 0,
            "example.org" => parsed.AbsolutePath == "/" && parsed.Query.Length == 0,
            _ => false
        };
        if (!allowed) return false;
        uri = parsed;
        return true;
    }

    private static bool IsVideoSearch(Uri uri) => uri.Host == "www.youtube.com" && uri.AbsolutePath == "/results"
        && QueryValue(uri, "search_query").Length is > 0 and <= 300;

    private static string QueryValue(Uri uri, string key) => uri.Query.StartsWith("?" + key + "=", StringComparison.Ordinal) && !uri.Query.Contains('&')
        ? Uri.UnescapeDataString(uri.Query[(key.Length + 2)..].Replace('+', ' ')).Trim() : "";

    private static string Clean(string text, int limit) => new(text.Where(ch => !char.IsControl(ch) && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format).Take(limit).ToArray());
    private static AiToolObservation Error(string message) => new(false, message, message);

    [GeneratedRegex("""(?m)^[ \t]*- link "((?:\\[^\r\n]|[^"\\\r\n])+)"[^\r\n]*:\r?\n[ \t]*- /url: /watch\?v=([A-Za-z0-9_-]{11})(?:[&\s]|$)""")]
    private static partial Regex VideoLinks();
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        DeleteWorkspace(workspace);
    }

    private static void DeleteWorkspace(DirectoryInfo directory)
    {
        try { directory.Delete(recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
