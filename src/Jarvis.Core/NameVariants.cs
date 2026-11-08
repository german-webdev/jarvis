using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

/// <summary>Local, bounded naming rules. Never guess a target by edit distance.</summary>
public static class NameVariants
{
    public static IEnumerable<string> SplitCustom(IEnumerable<string>? aliases) => (aliases ?? [])
        .SelectMany(a => a.Split([';', ',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase);
    private sealed record Family(string Pattern, string[] Names);
    private static readonly Family[] Families =
    [
        new(@"^subnautica(?: |$)", ["сабнавтика", "субнавтика"]),
        new(@"^forza horizon(?: |$)", ["форза", "forza", "форза хорайзон", "форза хоризон"]),
        new(@"^forza motorsport(?: |$)", ["форза", "forza", "форза моторспорт"]),
        new(@"^counter strike(?: |$)", ["контра", "контр страйк", "каунтер страйк", "кс", "ка эс", "cs"]),
        new(@"^call of duty(?: |$)", ["колда", "кал оф дьюти", "кол оф дьюти", "cod"]),
        new(@"^pubg(?: |$)", ["пабг", "пубг", "пабджи", "pubg"]),
        new(@"^remnant(?: |$)", ["ремнант", "ремнэнт", "remnant"]),
        new(@"^wardogs(?: |$)", ["вардогс", "вордогс", "вар догс", "war dogs"]),
        new(@"^grain rot(?: |$)", ["грейн рот", "грэйн рот", "грин рот"]),
        new(@"^the planet crafter(?: |$)", ["планет крафтер", "планета крафтер"]),
        new(@"^battlefield(?: |$)", ["батлфилд", "баттлфилд", "батла", "battlefield"]),
        new(@"^telegram$", ["телеграм", "телеграмм", "телега"]),
        new(@"^steam$", ["стим"]),
        new(@"^xbox$", ["иксбокс", "икс бокс", "эксбокс"]),
        new(@"^(?:microsoft |office )?word(?: \d{4})?$", ["ворд", "уорд", "word", "ward", "wardo", "вордо"]),
        new(@"^(?:microsoft |office )?excel(?: \d{4})?$", ["эксель", "экссель", "экзель", "excel"])
    ];
    private static readonly string[] Cardinals = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать", "двадцать"];
    private static readonly string[] Ordinals = ["", "первую", "вторую", "третью", "четвертую", "пятую", "шестую", "седьмую", "восьмую", "девятую", "десятую"];
    private static readonly Dictionary<string, string> Roman = new() { ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10" };

    public static string[] Automatic(string name)
    {
        var normalized = CommandRouter.Normalize(name);
        var numbered = NormalizeNumbers(normalized);
        var seeds = new List<string> { normalized, numbered };
        // Transliteration is only a spelling aid for the FULL title, not arbitrary shortened words.
        var family = Families.FirstOrDefault(f => Regex.IsMatch(normalized, f.Pattern));
        if (family != null)
        {
            foreach (var alias in family.Names)
            {
                seeds.Add(alias);
                foreach (var number in VersionNumbers(name)) seeds.Add(alias + " " + number);
            }
        }
        if (Regex.IsMatch(numbered, "[a-z]")) seeds.Add(Transliterate(numbered));
        seeds.AddRange(Pronunciations(numbered));
        return seeds.SelectMany(Expand).Select(CommandRouter.Normalize).Where(s => s.Length > 1 && s.Length <= 150 && s != normalized)
            .Distinct(StringComparer.Ordinal).Take(100).ToArray();
    }

    public static IEnumerable<string> Expand(string text)
    {
        var normalized = NormalizeNumbers(CommandRouter.Normalize(text));
        yield return normalized;
        var tokens = normalized.Split(' ');
        // Inflect the final Russian word, preserving brand words and a trailing part number.
        int last = tokens.Length - 1;
        if (last > 0 && int.TryParse(tokens[last], out _)) last--;
        var word = tokens[last];
        string Replace(string form) => string.Join(" ", tokens.Select((t, i) => i == last ? form : t));
        if (Regex.IsMatch(word, @"^[а-я]+а$"))
        {
            var inflected = Replace(word[..^1] + "у");
            yield return inflected;
            yield return SpokenNumbers(inflected);
            yield return Replace(word[..^1] + (Regex.IsMatch(word, @"[кгхжчшщ]а$") ? "и" : "ы"));
            yield return Replace(word[..^1] + "ой");
        }
        if (Regex.IsMatch(word, @"^[а-я]+я$")) { yield return Replace(word[..^1] + "ю"); yield return Replace(word[..^1] + "и"); yield return Replace(word[..^1] + "ей"); }
        if (Regex.IsMatch(word, @"^[а-я]+[бвгджзклмнпрстфхцчшщ]$")) { yield return Replace(word + "а"); yield return Replace(word + "ом"); }
        yield return SpokenNumbers(normalized);
    }
    private static IEnumerable<string> Pronunciations(string name)
    {
        // Word-level pronunciations can be reused by any catalogue title, not app-specific aliases.
        var words = new Dictionary<string, string[]> { ["kaspersky"] = ["касперский"], ["password"] = ["пасворд", "пассворд"],
            ["manager"] = ["менеджер"], ["yandex"] = ["яндекс"], ["music"] = ["музыка", "мьюзик"],
            ["word"] = ["ворд"], ["excel"] = ["эксель", "экссель"], ["microsoft"] = ["майкрософт"] };
        IEnumerable<string> phrases = [""];
        foreach (var word in name.Split(' '))
        {
            var options = words.GetValueOrDefault(word) ?? [Regex.IsMatch(word, "[a-z]") ? Transliterate(word) : word];
            phrases = phrases.SelectMany(prefix => options.Select(option => (prefix + " " + option).Trim())).Distinct().Take(16).ToArray();
        }
        return phrases;
    }

    public static string NormalizeNumbers(string text) => string.Join(" ", text.Split(' ').Select(w => Roman.TryGetValue(w, out var n) ? n : w));
    public static int[] VersionNumbers(string name) => Regex.Matches(NormalizeNumbers(CommandRouter.Normalize(name)), @"\b\d{1,2}\b")
        .Select(m => int.Parse(m.Value)).Where(n => n is > 0 and <= 20).Distinct().ToArray();
    private static string SpokenNumbers(string text) => Regex.Replace(text, @"\b\d{1,2}\b", m => int.TryParse(m.Value, out int n) && n < Cardinals.Length ? Cardinals[n] : m.Value);
    public static int? SelectionNumber(string text)
    {
        if (int.TryParse(text, out var number) && number is > 0 and <= 20) return number;
        var index = Array.IndexOf(Cardinals, text);
        if (index > 0) return index;
        index = Array.IndexOf(Ordinals, text);
        if (index > 0) return index;
        string[] masculine = ["", "первый", "второй", "третий", "четвертый", "пятый", "шестой", "седьмой", "восьмой", "девятый", "десятый"];
        index = Array.IndexOf(masculine, text); return index > 0 ? index : null;
    }
    private static string Transliterate(string text)
    {
        var value = text.Replace("shch", "щ").Replace("sh", "ш").Replace("ch", "ч").Replace("zh", "ж").Replace("th", "т").Replace("ph", "ф").Replace("qu", "кв");
        const string latin = "abcdefghijklmnopqrstuvwxyz";
        string[] cyrillic = ["а", "б", "к", "д", "е", "ф", "г", "х", "и", "дж", "к", "л", "м", "н", "о", "п", "к", "р", "с", "т", "у", "в", "в", "кс", "и", "з"];
        var result = new StringBuilder();
        foreach (char c in value) { var index = latin.IndexOf(c); result.Append(index >= 0 ? cyrillic[index] : c.ToString()); }
        return result.ToString();
    }
}
