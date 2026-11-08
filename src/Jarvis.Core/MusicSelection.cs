using System.Text.RegularExpressions;

namespace Jarvis.Core;

public record MediaCandidate(string SourceId, string? CatalogId, bool IsPlaying);
public record MediaChoice(string? SourceId, string? Error = null);

public static class MusicSelection
{
    public static bool IsMusicApp(LaunchTarget target) => Regex.IsMatch(CommandRouter.Normalize(target.Name), @"\b(?:музыка|music|spotify|aimp|foobar2000|winamp|deezer)\b");
    public static MediaChoice Choose(IReadOnlyList<MediaCandidate> sessions, IReadOnlyList<LaunchTarget> catalog, string? preferredId)
    {
        // A saved choice never silently falls back to a browser when its session disappears.
        if (!string.IsNullOrEmpty(preferredId)) return Unique(sessions.Where(s => s.CatalogId == preferredId).ToArray(), "Выбранный плеер недоступен. Скажи «включи музыку» или измени приложение в настройках.");
        var music = sessions.Where(s => catalog.Any(t => t.Enabled && t.Id == s.CatalogId && IsMusicApp(t))).ToArray();
        if (music.Length > 0) return Unique(music, "");
        return Unique(sessions, "Активного плеера не нашёл. Сначала включи музыку или видео.");
    }
    private static MediaChoice Unique(IReadOnlyList<MediaCandidate> sessions, string missing) => sessions.Count switch {
        1 => new(sessions[0].SourceId), 0 => new(null, missing),
        _ => new(null, "Доступно несколько плееров. Выбери приложение в настройках музыки или скажи «включи музыку».") };
}
