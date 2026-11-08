using System.Text.RegularExpressions;

namespace Jarvis.Core;

public enum Intent { OpenBrowser, Cancel, Unknown, LaunchTarget, Screenshot, OpenScreenshots, VolumeUp, VolumeDown, SetVolume, Mute, Unmute, Time, Date, Help,
    CloseTarget, FocusTarget, MinimizeWindow, CloseWindow, OpenTargetFolder, MediaPause, MediaPlay, MediaNext, MediaPrevious, StartMusic, MusicVolumeUp, MusicVolumeDown }
public record CommandRequest(Intent Intent, string? TargetId = null, int? Value = null, string? Explanation = null, string[]? CandidateIds = null, Intent? CandidateIntent = null);

public static partial class CommandRouter
{
    public static Intent Parse(string text) => Resolve(text).Intent;
    public static string Normalize(string text) => Spaces().Replace(Punctuation().Replace(text.ToLowerInvariant().Replace('ё', 'е'), " "), " ").Trim();
    public static CommandRequest Resolve(string text, IEnumerable<LaunchTarget>? targets = null, IEnumerable<LaunchTarget>? running = null, string? musicTargetId = null)
    {
        if (text.Length > 500 || Regex.IsMatch(text, @"[-−]\s*\d")) return new(Intent.Unknown);
        var phrase = WakePrefix().Replace(Normalize(text), "");
        phrase = Politeness().Replace(phrase, "").Trim();
        // Observed ASR omission in volume commands; do not rewrite arbitrary application names.
        phrase = Regex.Replace(phrase, @"^делай (?=(?:по)?(?:тише|тиши|громче|громчи)$)", "сделай ");
        if (text.Contains('?') && phrase is not ("сколько времени" or "который час" or "какое сегодня число" or "что ты умеешь")) return new(Intent.Unknown);
        if (phrase is "отмена" or "отмени") return new(Intent.Cancel);
        if (OpenPhrase().IsMatch(phrase)) return new(Intent.OpenBrowser);
        if (phrase is "включи музыку" or "запусти музыку" or "давай музыку")
        {
            var music = (targets ?? []).Where(t => t.Enabled && t.Kind != "url" && (t.Id == musicTargetId || MusicSelection.IsMusicApp(t))).ToArray();
            var preferred = music.FirstOrDefault(t => t.Id == musicTargetId);
            if (preferred != null) return new(Intent.StartMusic, preferred.Id);
            return music.Length switch { 1 => new(Intent.StartMusic, music[0].Id),
                > 1 => new(Intent.Unknown, CandidateIds: music.Select(t => t.Id).ToArray(), CandidateIntent: Intent.StartMusic),
                _ => new(Intent.Unknown, Explanation: "Музыкальное приложение не найдено. Выбери его в настройках музыки.", CandidateIntent: Intent.StartMusic) };
        }
        var simple = phrase switch
        {
            "сделай скриншот" or "сделай снимок экрана" or "сними экран" => Intent.Screenshot,
            "открой скриншоты" or "открой папку скриншотов" => Intent.OpenScreenshots,
            "громче" or "погромче" or "сделай громче" or "сделай погромче" or "увеличь громкость" or "музыку громче" or "сделай музыку громче" or "сделай музыку погромче" or "громче музыку"
                or "громчи" or "погромчи" or "сделай громчи" or "сделай погромчи" => Intent.MusicVolumeUp,
            "тише" or "потише" or "сделай тише" or "сделай потише" or "уменьши громкость" or "музыку тише" or "сделай музыку тише" or "сделай музыку потише" or "тише музыку"
                or "тиши" or "потиши" or "сделай тиши" or "сделай потиши" => Intent.MusicVolumeDown,
            "систему громче" or "сделай систему громче" => Intent.VolumeUp,
            "систему тише" or "сделай систему тише" => Intent.VolumeDown,
            "выключи звук" or "отключи звук" => Intent.Mute,
            "включи звук" or "верни звук" => Intent.Unmute,
            "сколько времени" or "который час" or "скажи время" => Intent.Time,
            "какое сегодня число" or "скажи дату" or "сегодняшняя дата" => Intent.Date,
            "что ты умеешь" or "покажи команды" or "список команд" => Intent.Help,
            "пауза" or "стоп" or "останови" or "поставь на паузу" or "приостанови воспроизведение"
                or "выключи музыку" or "останови музыку" or "отключи музыку" => Intent.MediaPause,
            "продолжи" or "продолжи воспроизведение" or "возобнови воспроизведение" or "сними с паузы" => Intent.MediaPlay,
            "следующий трек" or "следующая песня" or "включи следующий трек" or "дальше" or "следующий" or "следующая" => Intent.MediaNext,
            "предыдущий трек" or "предыдущая песня" or "включи предыдущий трек" or "назад" or "предыдущий" or "предыдущая" => Intent.MediaPrevious,
            "сверни окно" or "сверни текущее окно" or "сверни активное окно" => Intent.MinimizeWindow,
            "закрой окно" or "закрой текущее окно" or "закрой активное окно" or "закрой текущее приложение" => Intent.CloseWindow,
            _ => Intent.Unknown
        };
        if (simple != Intent.Unknown) return new(simple);
        var folder = Regex.Match(phrase, @"^открой (?:папку|каталог)(?: (?:с|для))?(?: (?:игрой|игры|программой|программы|приложением|приложения))? (.+)$");
        if (folder.Success) return MatchTarget(folder.Groups[1].Value, targets?.Where(t => t.Kind != "url"), Intent.OpenTargetFolder);
        var window = Regex.Match(phrase, @"^(закрой|выключи|переключись на|покажи)(?: (?:игру|приложение|программу))? (.+)$");
        if (window.Success) return MatchTarget(window.Groups[2].Value, running,
            window.Groups[1].Value is "закрой" or "выключи" ? Intent.CloseTarget : Intent.FocusTarget,
            "Подходящего открытого окна не нашёл. Назови приложение точнее.");
        var volume = VolumePhrase().Match(phrase);
        if (volume.Success && TryNumber(volume.Groups[1].Value, out var value) && value is >= 0 and <= 100)
            return new(Intent.SetVolume, Value: value);
        var launch = LaunchPhrase().Match(phrase);
        var website = Regex.Match(phrase, @"^(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)? сайт (.+)$");
        if (website.Success) return MatchTarget(website.Groups[1].Value, targets?.Where(t => t.Kind == "url"), Intent.LaunchTarget,
            "Такого сайта нет в каталоге. Добавь его через меню «Джарвис → Сайты».");
        var application = Regex.Match(phrase, @"^(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)? (?:игру|приложение|программу) (.+)$");
        if (application.Success) return MatchTarget(application.Groups[1].Value, targets?.Where(t => t.Kind != "url"), Intent.LaunchTarget);
        if (!launch.Success) return new(Intent.Unknown);
        return MatchTarget(launch.Groups[1].Value, targets, Intent.LaunchTarget);
    }
    private static CommandRequest MatchTarget(string name, IEnumerable<LaunchTarget>? targets, Intent intent, string? missing = null)
    {
        var enabled = (targets ?? []).Where(t => t.Enabled).ToArray();
        var matches = enabled.Where(t => t.Names.Any(n => Normalize(n) == name)).DistinctBy(t => t.Id).ToArray();
        // Bare mail requests use the webmail aliases selected in the site catalog.
        // Explicit "открой приложение почта" already filters sites out above.
        if (intent == Intent.LaunchTarget && name is "почта" or "почту" or "почты" or "почтой")
        {
            var mailSites = matches.Where(t => t.Kind == "url").ToArray();
            if (mailSites.Length > 0) matches = mailSites;
        }
        return matches.Length switch
        {
            1 => new(intent, matches[0].Id),
            > 1 => new(Intent.Unknown, Explanation: "Подходят несколько приложений.", CandidateIds: matches.Select(t => t.Id).ToArray(), CandidateIntent: intent),
            _ => new(Intent.Unknown, Explanation: missing, CandidateIntent: intent)
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
    [GeneratedRegex(@"^(?:джарвис|джарвисc|джервис|джарвиз|чарвис|jarvis)\s+")] private static partial Regex WakePrefix();
    [GeneratedRegex(@"^пожалуйста | пожалуйста$")] private static partial Regex Politeness();
    [GeneratedRegex(@"^(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)?(?: интернет)? браузер$")] private static partial Regex OpenPhrase();
    [GeneratedRegex(@"^(?:установи |поставь |сделай )?громкость(?: на)? ([а-я0-9 ]+?)(?: процентов| процента| процент)?$")] private static partial Regex VolumePhrase();
    [GeneratedRegex(@"^(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)?(?: игру| приложение| программу)? (.+)$")] private static partial Regex LaunchPhrase();
}
