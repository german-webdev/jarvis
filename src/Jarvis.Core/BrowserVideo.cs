using System.Text.RegularExpressions;

namespace Jarvis.Core;

public sealed record BrowserVideo(string Id, string Title, string Channel)
{
    public string Url => "https://www.youtube.com/watch?v=" + Id;
}
public sealed record BrowserVideoResult(bool Verified, string Message, IReadOnlyList<BrowserVideo> Videos, string? Url = null, string? Title = null);

/// <summary>Explicitly opened, isolated browser only. No general browser control or model-generated code.</summary>
public interface IBrowserVideoService : IAsyncDisposable
{
    bool Connected { get; }
    Task ConnectAsync(CancellationToken token);
    Task<BrowserVideoResult> SearchAsync(string query, CancellationToken token);
    Task<BrowserVideoResult> OpenAsync(BrowserVideo video, CancellationToken token);
    void Stop();
}

public static class BrowserVideoCommands
{
    public static string? Query(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(?:найди видео|найти видео|find video|search video)(?:\s+(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    public static int? Choice(string text)
    {
        var value = CommandRouter.Normalize(text);
        value = Regex.Replace(value, @"^(?:вариант|видео|открой видео|открой вариант|video|option|open video)\s+", "");
        return value switch
        {
            "1" or "один" or "первый" or "первое" or "one" or "first" => 0,
            "2" or "два" or "второй" or "второе" or "two" or "second" => 1,
            "3" or "три" or "третий" or "третье" or "three" or "third" => 2,
            "4" or "четыре" or "четвёртый" or "четвертый" or "четвертое" or "four" => 3,
            "5" or "пять" or "пятый" or "пятое" or "five" => 4,
            _ => null
        };
    }
    public static bool ValidId(string? id) => id != null && Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);
    public static bool TryVideoUrl(string? url, out string id)
    {
        id = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || uri.Host != "www.youtube.com" || uri.AbsolutePath != "/watch" || uri.Fragment.Length != 0) return false;
        var query = uri.Query.TrimStart('?').Split('&');
        var values = query.Where(v => v.StartsWith("v=", StringComparison.Ordinal)).ToArray();
        if (values.Length != 1 || !ValidId(values[0][2..])) return false;
        id = values[0][2..]; return true;
    }
}
