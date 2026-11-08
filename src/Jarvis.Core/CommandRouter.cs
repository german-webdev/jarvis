using System.Text.RegularExpressions;

namespace Jarvis.Core;

public enum Intent { OpenBrowser, Cancel, Unknown, LaunchTarget, Screenshot, OpenScreenshots, VolumeUp, VolumeDown, SetVolume, Mute, Unmute, Time, Date, Help }
public record CommandRequest(Intent Intent, string? TargetId = null, int? Value = null, string? Explanation = null, string[]? CandidateIds = null);

public static partial class CommandRouter
{
    public static Intent Parse(string text) => Resolve(text).Intent;
    public static string Normalize(string text) => Spaces().Replace(Punctuation().Replace(text.ToLowerInvariant().Replace('ё', 'е'), " "), " ").Trim();
    public static CommandRequest Resolve(string text, IEnumerable<LaunchTarget>? targets = null)
    {
        if (text.Length > 500 || Regex.IsMatch(text, @"[-−]\s*\d")) return new(Intent.Unknown);
        var phrase = WakePrefix().Replace(Normalize(text), "");
        phrase = Politeness().Replace(phrase, "").Trim();
        if (text.Contains('?') && phrase is not ("сколько времени" or "который час" or "какое сегодня число" or "что ты умеешь")) return new(Intent.Unknown);
        if (phrase is "отмена" or "отмени" or "стоп") return new(Intent.Cancel);
        if (OpenPhrase().IsMatch(phrase)) return new(Intent.OpenBrowser);
        var simple = phrase switch
        {
            "сделай скриншот" or "сделай снимок экрана" or "сними экран" => Intent.Screenshot,
            "открой скриншоты" or "открой папку скриншотов" => Intent.OpenScreenshots,
            "громче" or "сделай громче" or "увеличь громкость" => Intent.VolumeUp,
            "тише" or "сделай тише" or "уменьши громкость" => Intent.VolumeDown,
            "выключи звук" or "отключи звук" => Intent.Mute,
            "включи звук" or "верни звук" => Intent.Unmute,
            "сколько времени" or "который час" or "скажи время" => Intent.Time,
            "какое сегодня число" or "скажи дату" or "сегодняшняя дата" => Intent.Date,
            "что ты умеешь" or "покажи команды" or "список команд" => Intent.Help,
            _ => Intent.Unknown
        };
        if (simple != Intent.Unknown) return new(simple);
        var volume = VolumePhrase().Match(phrase);
        if (volume.Success && TryNumber(volume.Groups[1].Value, out var value) && value is >= 0 and <= 100)
            return new(Intent.SetVolume, Value: value);
        var launch = LaunchPhrase().Match(phrase);
        if (!launch.Success) return new(Intent.Unknown);
        var name = launch.Groups[1].Value;
        var matches = (targets ?? []).Where(t => t.Enabled && t.Names.Any(n => Normalize(n) == name)).DistinctBy(t => t.Id).ToArray();
        return matches.Length switch
        {
            1 => new(Intent.LaunchTarget, matches[0].Id),
            > 1 => new(Intent.Unknown, Explanation: "Подходят несколько приложений.", CandidateIds: matches.Select(t => t.Id).ToArray()),
            _ => new(Intent.Unknown)
        };
    }
    private static bool TryNumber(string text, out int value)
    {
        if (int.TryParse(text, out value)) return true;
        string[] units = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
        var index = Array.IndexOf(units, text);
        if (index >= 0) { value = index; return true; }
        if (text == "сто") { value = 100; return true; }
        var parts = text.Split(' ');
        string[] tens = ["двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
        index = Array.IndexOf(tens, parts[0]);
        if (index < 0 || parts.Length > 2) return false;
        value = (index + 2) * 10;
        if (parts.Length == 1) return true;
        var unit = Array.IndexOf(units, parts[1]);
        if (unit is < 1 or > 9) return false;
        value += unit; return true;
    }
    [GeneratedRegex(@"[^\p{L}\p{N}\s]")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^(?:джарвис|джарвисc|jarvis)\s+")] private static partial Regex WakePrefix();
    [GeneratedRegex(@"^пожалуйста | пожалуйста$")] private static partial Regex Politeness();
    [GeneratedRegex(@"^(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)?(?: интернет)? браузер$")] private static partial Regex OpenPhrase();
    [GeneratedRegex(@"^(?:установи |поставь |сделай )?громкость(?: на)? ([а-я0-9 ]+?)(?: процентов| процента| процент)?$")] private static partial Regex VolumePhrase();
    [GeneratedRegex(@"^(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)?(?: игру| приложение| программу)? (.+)$")] private static partial Regex LaunchPhrase();
}
