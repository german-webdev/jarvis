using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace Jarvis.App;

public static class Ui
{
    private static readonly IReadOnlyDictionary<string, string> English = Load();
    private static readonly (Regex Pattern, string Translation)[] Templates = English
        .Where(pair => pair.Key.Contains("{0}", StringComparison.Ordinal))
        .Select(pair => (new Regex("^" + Regex.Replace(Regex.Escape(pair.Key), @"\\\{(\d+)}", "(.*?)") + "$", RegexOptions.CultureInvariant), pair.Value))
        .ToArray();
    public static string Language { get; private set; } = "ru";
    public static event Action? Changed;
    public static string Key(string source) => "Ui" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    public static IReadOnlyDictionary<string, string> Translations => English;

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream("Jarvis.Localization.strings.json")
            ?? throw new InvalidOperationException("Missing interface translations.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    public static void Apply(string language)
    {
        Language = language == "en" ? "en" : "ru";
        if (Application.Current != null)
            foreach (var pair in English) Application.Current.Resources[Key(pair.Key)] = Language == "en" ? pair.Value : pair.Key;
        Changed?.Invoke();
    }

    public static string Text(string source)
    {
        if (Language != "en") return source;
        if (English.TryGetValue(source, out var translation)) return translation;
        // Keep the timestamp and user-provided names/transcripts intact.
        if (source.Length > 10 && source[2] == ':' && source[5] == ':' && source[8] == ' ')
            return source[..10] + Text(source[10..]);
        foreach (var (pattern, translated) in Templates)
        {
            var match = pattern.Match(source);
            if (match.Success) return string.Format(CultureInfo.InvariantCulture, translated,
                match.Groups.Cast<Group>().Skip(1).Select(g => (object)g.Value).ToArray());
        }
        return source;
    }

    public static string Format(string source, params object[] arguments) => string.Format(CultureInfo.InvariantCulture, Text(source), arguments);
}
