using System.Text.RegularExpressions;

namespace Jarvis.Core;

public static partial class SteamManifest
{
    public static IEnumerable<string> LibraryPaths(string content) => Pairs().Matches(content).Cast<Match>()
        .Where(m => m.Groups[1].Value.Equals("path", StringComparison.OrdinalIgnoreCase))
        .Select(m => Unescape(m.Groups[2].Value)).Distinct(StringComparer.OrdinalIgnoreCase);
    public static LaunchTarget? ReadApp(string content, string steamAppsDirectory)
    {
        var pairs = Pairs().Matches(content).Cast<Match>().GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Unescape(g.First().Groups[2].Value), StringComparer.OrdinalIgnoreCase);
        if (!pairs.TryGetValue("appid", out var id) || !pairs.TryGetValue("name", out var name)
            || !pairs.TryGetValue("installdir", out var directory) || !pairs.TryGetValue("StateFlags", out var state)
            || !int.TryParse(state, out var flags) || (flags & 4) == 0 || Path.IsPathRooted(directory) || directory.Contains("..")) return null;
        if (!Directory.Exists(Path.Combine(steamAppsDirectory, "common", directory))) return null;
        var target = new LaunchTarget("steam:" + id, name, "steam", id, "Steam", [], true);
        return target.IsValid ? target : null;
    }
    private static string Unescape(string text) => text.Replace("\\\"", "\"").Replace("\\\\", "\\");
    [GeneratedRegex("\"([^\"]+)\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"")] private static partial Regex Pairs();
}
