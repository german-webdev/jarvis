using System.Text.RegularExpressions;

namespace Jarvis.Core;

public enum Intent { OpenBrowser, Cancel, Unknown }

public static partial class CommandRouter
{
    // Deliberately bounded grammar: negation, questions and extra clauses cannot launch anything.
    public static Intent Parse(string text)
    {
        if (text.Contains('?')) return Intent.Unknown;
        var normalized = Punctuation().Replace(text.ToLowerInvariant().Replace('ё', 'е'), " ");
        normalized = Spaces().Replace(normalized, " ").Trim();
        normalized = WakePrefix().Replace(normalized, "");
        if (normalized is "отмена" or "отмени" or "стоп") return Intent.Cancel;
        return OpenPhrase().IsMatch(normalized) ? Intent.OpenBrowser : Intent.Unknown;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}\s]")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^(?:джарвис|джарвисc|jarvis)\s+")] private static partial Regex WakePrefix();
    [GeneratedRegex(@"^(?:пожалуйста )?(?:открой|запусти|включи)(?: пожалуйста)?(?: мне)?(?: интернет)? браузер(?: пожалуйста)?$")]
    private static partial Regex OpenPhrase();
}
