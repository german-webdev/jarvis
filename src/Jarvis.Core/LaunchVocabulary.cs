namespace Jarvis.Core;

/// <summary>Bounded ASR hints; not a restriction on the titles that can be recognized.</summary>
public static class LaunchVocabulary
{
    public static string Build(IEnumerable<LaunchTarget> targets, bool choosing = false)
    {
        var enabled = targets.Where(t => t.Enabled).ToArray();
        var priority = enabled.Where(t => choosing || t.Kind == "steam" || t.Source == "Xbox" || MusicSelection.IsMusicApp(t) || IsDesktopPriority(t)
            || t.Aliases?.Length > 0 || CommandRouter.Normalize(t.Name) is "telegram" or "steam" or "xbox")
            .OrderByDescending(t => IsDesktopPriority(t) || MusicSelection.IsMusicApp(t))
            .Select(t => t.Names.Take(12).ToArray()).ToArray();
        IEnumerable<string> RoundRobin(int start, int count) => Enumerable.Range(start, count)
            .SelectMany(i => priority.Where(g => g.Length > i).Select(g => g[i]));
        string[] answers = choosing ? ["первую", "вторую", "третью", "четвертую", "пятую", "шестую", "вариант один", "вариант два", "отмена"] : [];
        var hints = answers.Concat(enabled.SelectMany(t => NameVariants.SplitCustom(t.Aliases)))
            .Concat(RoundRobin(0, 4)).Concat(enabled.Select(t => t.Name)).Concat(RoundRobin(4, 8));
        var selected = new List<string>();
        int length = 0;
        foreach (var hint in hints.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            int added = hint.Length + (selected.Count > 0 ? 2 : 0);
            if (length + added > 1500) continue;
            selected.Add(hint); length += added;
            if (selected.Count >= 120) break;
        }
        return string.Join(", ", selected);
    }
    private static bool IsDesktopPriority(LaunchTarget target) => System.Text.RegularExpressions.Regex.IsMatch(CommandRouter.Normalize(target.Name), @"\b(?:word|excel|password manager)\b");
}
