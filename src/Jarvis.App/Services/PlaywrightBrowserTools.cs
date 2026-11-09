using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Jarvis.Core;

namespace Jarvis.App.Services;

internal sealed partial class PlaywrightBrowserTools(McpStdioClient client, DirectoryInfo workspace, Action<string>? progress,
    IReadOnlyList<AiToolDefinition> tools) : IAiToolSession
{
    private const string Navigate = "browser_navigate";
    private const string Snapshot = "browser_snapshot";
    private const string Click = "browser_click";
    private const string Show = "browser_show";
    private const string Unavailable = "Браузерный инструмент недоступен. Проверь установку JARVIS и Chrome или Edge.";
    private const string NavigationError = "Браузер не смог прочитать страницу. Проверь интернет и доступ к сайту.";
    private const string NoResults = "Браузер открыл YouTube, но список видео не подтвердился.";
    private const string NoVideo = "Страница видео не подтвердилась.";
    private const string NoChannel = "Не удалось подтвердить нужный канал YouTube.";
    private const string YouTube = "https://www.youtube.com";
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly AiToolDefinition ReadPage = new(Navigate,
        "Open any public HTTP or HTTPS page in the temporary browser and read it. For YouTube search, use " +
        "https://www.youtube.com/results?search_query=<URL-encoded query>. Returns an accessibility snapshot or a specific failure.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { url = new { type = "string" } },
            required = new[] { "url" }, additionalProperties = false }));
    private static readonly JsonElement NoParameters = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });
    private static readonly JsonElement SnapshotParameters = JsonSerializer.SerializeToElement(new { type = "object", properties = new {
        target = new { type = "string", description = "Element reference to read just that part of the page." },
        depth = new { type = "integer", minimum = 1, maximum = 50, description = "Optional depth limit for a very large page." }
    }, additionalProperties = false });
    private static readonly string[] BrowserActions = [Click, "browser_hover", "browser_type", "browser_press_key", "browser_tabs",
        "browser_navigate_back", "browser_select_option", "browser_fill_form", "browser_drag", "browser_handle_dialog", "browser_wait_for"];
    private string? _currentPage;

    public IReadOnlyList<AiToolDefinition> Tools { get; } = tools;

    public static async Task<IAiToolSession> StartAsync(string root, Action<string>? progress, CancellationToken token)
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
            await File.WriteAllTextAsync(config, """{"browser":{"launchOptions":{"args":["--mute-audio"]},"contextOptions":{"acceptDownloads":false}}}""", token);
            var start = new ProcessStartInfo(node) { WorkingDirectory = workspace.FullName };
            // Do not inherit Node preload options, Playwright overrides, or credentials from the app's environment.
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "SystemDrive", "WINDIR", "TEMP", "TMP", "PATH", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            var executable = BrowserLauncher.GetDefaultExecutable();
            var defaultName = Path.GetFileName(executable)?.ToLowerInvariant();
            var channel = defaultName == "chrome.exe" ? "chrome" : "msedge";
            foreach (var argument in new[] { cli, "--config", config, "--browser=" + channel, "--headless", "--isolated", "--sandbox", "--no-webmcp",
                "--block-service-workers", "--codegen=none", "--image-responses=omit", "--snapshot-mode=none", "--timeout-navigation=16000",
                "--output-max-size=1000000", "--blocked-origins=http://localhost;https://localhost;http://127.0.0.1;https://127.0.0.1" }) start.ArgumentList.Add(argument);
            if (defaultName is "chrome.exe" or "msedge.exe") start.ArgumentList.Add("--executable-path=" + executable);
            client = await McpStdioClient.StartAsync(start, token);
            var available = (await client.ListToolsAsync(token)).ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);
            if (!available.ContainsKey(Navigate) || !available.ContainsKey(Snapshot) || !available.ContainsKey(Click)) throw new InvalidDataException(Unavailable);
            List<AiToolDefinition> definitions = [ReadPage,
                new(Snapshot, "Read the current browser page and element references without changing it. Use a target or depth on very large pages.", SnapshotParameters),
                new(Show, "Finish and open the current verified page in the user's default browser. Use for requests to open or show a page.", NoParameters)];
            foreach (var name in BrowserActions)
                if (available.TryGetValue(name, out var definition))
                    definitions.Add(new(name, definition.GetProperty("description").GetString()!, JsonSerializer.SerializeToElement(
                        definition.GetProperty("inputSchema").EnumerateObject().Where(item => item.Name != "$schema").ToDictionary(item => item.Name, item => item.Value))));
            return new PlaywrightBrowserTools(client, workspace, progress, definitions);
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
        if (arguments.ValueKind != JsonValueKind.Object) return Error("Браузерное действие недоступно.");
        var fields = arguments.EnumerateObject().ToArray();
        if (name == Show)
            return fields.Length == 0 && _currentPage != null ? new(true, "", "Выполнено.", _currentPage, Complete: true)
                : Error("Адрес страницы не подтверждён.");
        _currentPage = null;
        Uri? requested = null;
        if (name == Navigate)
        {
            if (fields.Length != 1 || fields[0].Name != "url" || fields[0].Value.ValueKind != JsonValueKind.String
                || !TryPublicPage(fields[0].Value.GetString(), out requested)) return Error("Нужен адрес общедоступной страницы.");
            arguments = JsonSerializer.SerializeToElement(new { url = requested.AbsoluteUri });
        }
        else if (name == Snapshot)
        {
            if (fields.Any(field => field.Name switch {
                "target" => field.Value.ValueKind != JsonValueKind.String,
                "depth" => field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var depth) || depth is < 1 or > 50,
                _ => true
            })) return Error("Браузерное действие недоступно.");
        }
        else if (!BrowserActions.Contains(name, StringComparer.Ordinal)) return Error("Браузерное действие недоступно.");
        else if (name == "browser_tabs" && arguments.TryGetProperty("url", out var url)
                 && (url.ValueKind != JsonValueKind.String || !TryPublicPage(url.GetString(), out requested))) return Error("Нужен адрес общедоступной страницы.");
        try
        {
            if (requested != null && !await ResolvesPubliclyAsync(requested, token)) return Error("Нужен адрес общедоступной страницы.");
            var search = requested != null && IsVideoSearch(requested);
            var watch = requested != null && IsVideoWatch(requested);
            var channel = requested != null && IsChannelPage(requested);
            progress?.Invoke(search ? "Ищу…" : "Проверяю страницу…");
            string? actionData = null;
            if (name != Snapshot)
            {
                var action = await client.CallAsync(name, arguments, token);
                if (Failed(action)) return new(false, TrimObservation(ReadText(action), 1700), NavigationError);
                if (name == "browser_tabs") actionData = TrimObservation(ReadText(action), 2000);
            }
            var rejectedCookies = false;
            for (var attempt = 0; attempt < (search || watch || channel ? 5 : 1); attempt++)
            {
                if (attempt > 0) await Task.Delay(600, token);
                var result = await client.CallAsync(Snapshot, name == Snapshot ? arguments : Empty, token);
                if (Failed(result)) return new(false, TrimObservation(ReadText(result), 1700), NavigationError);
                var text = ReadText(result);
                if (channel && !rejectedCookies && TryGetConsentReject(requested!, text, out var target))
                {
                    rejectedCookies = true;
                    var reject = await client.CallAsync(Click, JsonSerializer.SerializeToElement(new { target }), token);
                    if (Failed(reject)) return Error(NoChannel);
                    continue;
                }
                var observation = Observe(requested, text);
                if (observation.Verified || observation.Display is not (NoResults or NoVideo or NoChannel))
                {
                    if (observation.SourceUrl is { } source && !await ResolvesPubliclyAsync(new Uri(source), token))
                        return Error("Нужен адрес общедоступной страницы.");
                    _currentPage = observation.SourceUrl;
                    return actionData == null ? observation : observation with { Data = actionData + "\n" + observation.Data };
                }
            }
            return Error(channel ? NoChannel : watch ? NoVideo : NoResults);
        }
        catch (TimeoutException) { return Error("Браузер не ответил вовремя. Повтори запрос."); }
        catch (McpResponseTooLargeException)
        { return new(false, "The snapshot is too large. Call browser_snapshot with a smaller depth or a specific target.", "Страница слишком большая для чтения целиком."); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return Error(NavigationError); }
    }

    private static bool Failed(JsonElement result) => result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
    private static string ReadText(JsonElement result) => string.Join('\n', result.GetProperty("content").EnumerateArray()
        .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
    private static string TrimObservation(string text, int limit) => text[..Math.Min(text.Length, limit)];

    internal static AiToolObservation Observe(Uri? requested, string text)
    {
        var split = text.IndexOf("\n### Snapshot\n", StringComparison.Ordinal);
        if (split < 0) return Error(NavigationError);
        var headers = text[..split];
        var address = Header(headers, "- Page URL: ");
        var title = Header(headers, "- Page Title: ");
        var requestedChannel = requested != null && IsChannelPage(requested);
        if (requestedChannel) address = WithoutConsentMarker(address);
        if (!TryPublicPage(address, out var actual)
            || (requested != null && IsVideoSearch(requested) && (!IsVideoSearch(actual) || QueryValue(actual, "search_query") != QueryValue(requested, "search_query")))
            || (requested != null && IsVideoWatch(requested) && (!IsVideoWatch(actual) || QueryValue(actual, "v") != QueryValue(requested, "v")))
            || (requestedChannel && !SameChannel(requested!, actual)))
            return Error("Сайт перенаправил браузер или не показал ожидаемую страницу.");
        if (int.TryParse(Header(headers, "- HTTP status: "), out var status) && status >= 400)
            return Error($"Сайт вернул ошибку HTTP {status}.");
        if (string.IsNullOrWhiteSpace(title))
        {
            if (IsVideoSearch(actual)) return Error(NoResults);
            if (IsVideoWatch(actual)) return Error(NoVideo);
            if (IsChannelPage(actual)) return Error(NoChannel);
            title = actual.Host;
        }
        var snapshot = text[(split + "\n### Snapshot\n".Length)..];
        var end = snapshot.IndexOf("\n### ", StringComparison.Ordinal);
        if (end >= 0) snapshot = snapshot[..end];
        if (snapshot.Length < 20 || snapshot.StartsWith("- [Snapshot]", StringComparison.Ordinal)) return Error(NavigationError);
        title = Clean(title, 180);
        if (IsVideoSearch(actual))
        {
            var videos = VideoLinks().Matches(snapshot).Select(match => new {
                title = Clean(UnescapeTitle(match.Groups[1].Value), 180),
                url = "https://www.youtube.com/watch?v=" + match.Groups[2].Value
            }).Where(video => video.title.Length > 0).DistinctBy(video => video.url).Take(5).ToArray();
            var channels = ChannelLinks().Matches(snapshot).Select(match => new {
                name = Clean(UnescapeTitle(match.Groups[1].Value), 180), url = YouTube + match.Groups[2].Value
            }).Where(item => TryPublicPage(item.url, out var uri) && IsChannelPage(uri))
                .DistinctBy(item => item.url, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            if (videos.Length == 0 && channels.Length == 0) return Error(NoResults);
            return Observed(actual, new { url = actual.AbsoluteUri, title, videos, channels });
        }
        const string suffix = " - YouTube";
        if (IsVideoWatch(actual))
        {
            if (!title.EndsWith(suffix, StringComparison.Ordinal) || !VideoHeading().Matches(snapshot).Any(match =>
                    Clean(UnescapeTitle(match.Groups[1].Value), 180) == title[..^suffix.Length])) return Error(NoVideo);
            return Observed(actual, new { url = actual.AbsoluteUri, title = title[..^suffix.Length] });
        }
        if (IsChannelPage(actual))
        {
            if (!title.EndsWith(suffix, StringComparison.Ordinal)) return Error(NoChannel);
            var channelName = title[..^suffix.Length];
            var heading = VideoHeading().Matches(snapshot).FirstOrDefault(match =>
            {
                var name = Clean(UnescapeTitle(match.Groups[1].Value), 180);
                return name == channelName || name.StartsWith(channelName + ", ", StringComparison.Ordinal);
            });
            if (heading == null) return Error(NoChannel);
            var handle = ChannelHandleMarker().Match(TrimObservation(snapshot[heading.Index..], 1800));
            if (!handle.Success || (actual.AbsolutePath.StartsWith("/@", StringComparison.Ordinal) &&
                !Uri.UnescapeDataString(actual.AbsolutePath.Trim('/')).Equals(handle.Groups[1].Value, StringComparison.OrdinalIgnoreCase))) return Error(NoChannel);
            return Observed(actual, new { url = actual.AbsoluteUri, title = channelName, handle = handle.Groups[1].Value,
                snapshot = TrimObservation(snapshot, 6500) });
        }
        // Public page content is an observation, including links and references needed for the next browser action.
        var excerpt = string.Join('\n', snapshot.Split('\n').Select(line => TrimObservation(line, 800)));
        return Observed(actual, new { url = actual.AbsoluteUri, title, snapshot = TrimObservation(excerpt, 12000) });
    }

    internal static bool TryPublicPage(string? value, out Uri uri)
    {
        uri = null!;
        if (value == null || value.Length > 2000 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length != 0 || !parsed.IsDefaultPort
            || parsed.HostNameType != UriHostNameType.Dns || parsed.IsLoopback) return false;
        var host = parsed.IdnHost.TrimEnd('.');
        if (!host.Contains('.') || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)) return false;
        if (host == "youtube.com") parsed = new UriBuilder(parsed) { Host = "www.youtube.com" }.Uri;
        uri = parsed;
        return true;
    }

    internal static bool IsChannelPage(Uri uri) => uri.Host == "www.youtube.com" && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && ChannelPath().IsMatch(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/')));

    private static async Task<bool> ResolvesPubliclyAsync(Uri uri, CancellationToken token)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, token);
            return addresses.Length > 0 && addresses.All(IsPublicIp);
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    internal static bool IsPublicIp(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xe0) == 0x20;
        return bytes[0] is > 0 and < 224 and not (10 or 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    private static bool SameChannel(Uri expected, Uri actual) => IsChannelPage(actual) && string.Equals(
        Uri.UnescapeDataString(expected.AbsolutePath.TrimEnd('/')), Uri.UnescapeDataString(actual.AbsolutePath.TrimEnd('/')),
        expected.AbsolutePath.StartsWith("/@", StringComparison.Ordinal) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? WithoutConsentMarker(string? address) => address?.EndsWith("?cbrd=1", StringComparison.Ordinal) == true ? address[..^7] : address;

    internal static bool TryGetConsentReject(Uri requested, string text, out string target)
    {
        target = "";
        var split = text.IndexOf("\n### Snapshot\n", StringComparison.Ordinal);
        if (split < 0 || !IsChannelPage(requested) || !Uri.TryCreate(Header(text[..split], "- Page URL: "), UriKind.Absolute, out var consent)
            || consent.Host != "consent.youtube.com" || consent.Scheme != Uri.UriSchemeHttps || consent.AbsolutePath != "/m"
            || !consent.IsDefaultPort || consent.UserInfo.Length != 0
            || !TryPublicPage(WithoutConsentMarker(HttpUtility.ParseQueryString(consent.Query)["continue"]), out var redirect)
            || !SameChannel(requested, redirect)) return false;
        var button = RejectButton().Match(text[split..]);
        if (!button.Success) return false;
        target = button.Groups[1].Value;
        return true;
    }

    private static string? Header(string headers, string prefix) => headers.Split('\n')
        .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();

    private static bool IsVideoSearch(Uri uri) => uri.Host == "www.youtube.com" && uri.AbsolutePath == "/results"
        && QueryValue(uri, "search_query").Length is > 0 and <= 300;

    private static bool IsVideoWatch(Uri uri) => uri.Host == "www.youtube.com" && uri.AbsolutePath == "/watch"
        && BrowserVideoCommands.ValidId(QueryValue(uri, "v"));

    private static string QueryValue(Uri uri, string key) => uri.Query.StartsWith("?" + key + "=", StringComparison.Ordinal) && !uri.Query.Contains('&')
        ? Uri.UnescapeDataString(uri.Query[(key.Length + 2)..].Replace('+', ' ')).Trim() : "";

    private static string Clean(string text, int limit) => new(text.Where(ch => !char.IsControl(ch) && char.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.Format).Take(limit).ToArray());
    private static string UnescapeTitle(string title) => Regex.Replace(title, "\\\\([\"\\\\])", "$1");
    private static AiToolObservation Observed(Uri address, object data) => new(true, JsonSerializer.Serialize(data), "", address.AbsoluteUri);
    private static AiToolObservation Error(string message) => new(false, message, message);

    [GeneratedRegex("""(?m)^[ \t]*- link "((?:\\[^\r\n]|[^"\\\r\n])+)"[^\r\n]*:\r?\n[ \t]*- /url: /watch\?v=([A-Za-z0-9_-]{11})(?:[&\s]|$)""")]
    private static partial Regex VideoLinks();
    [GeneratedRegex("""(?m)^[ \t]*- heading "((?:\\[^\r\n]|[^"\\\r\n])+)" \[level=1\]""")]
    private static partial Regex VideoHeading();
    [GeneratedRegex("""(?m)^[ \t]*- link "((?:\\[^\r\n]|[^"\\\r\n])+)"[^\r\n]*:\r?\n[ \t]*- /url: (/(?:@|channel/)[^\s]+)""")]
    private static partial Regex ChannelLinks();
    [GeneratedRegex(@"^(?:/@[\p{L}\p{M}\p{N}_.·-]{1,60}|/channel/UC[A-Za-z0-9_-]{22})$")]
    private static partial Regex ChannelPath();
    [GeneratedRegex("""(?m)^[ \t]*- generic \[ref=[a-z0-9]+\]: "?(@[^\s"]+)"?[ \t]*\r?$""")]
    private static partial Regex ChannelHandleMarker();
    [GeneratedRegex("""(?m)^[ \t]*- button "(?:Reject all|Отклонить все)" \[ref=([a-z0-9]+)\]""")]
    private static partial Regex RejectButton();

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
