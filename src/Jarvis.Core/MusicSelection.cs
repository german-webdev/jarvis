using System.Text.RegularExpressions;

namespace Jarvis.Core;

public enum MediaScope { Current, Music, Video }
public record MediaCandidate(string SourceId, string? CatalogId, bool IsPlaying, bool IsCurrent = false);
public record MediaChoice(string? SourceId, string? Error = null);

public static class MusicSelection
{
    public static bool IsMusicApp(LaunchTarget target) => IsApplication(target) &&
        Regex.IsMatch(CommandRouter.Normalize(target.Name + " " + Path.GetFileNameWithoutExtension(target.Target)), @"\b(?:музыка|music|spotify|aimp|foobar2000|winamp|deezer|musicbee)\b");
    public static bool IsVideoApp(LaunchTarget target) => IsApplication(target) &&
        Regex.IsMatch(CommandRouter.Normalize(target.Name + " " + Path.GetFileNameWithoutExtension(target.Target)), @"\b(?:vlc|potplayer\w*|mpc hc\w*|mpc be\w*|kmplayer|mpv|wmplayer|mediaplayer|медиаплеер|медиапроигрыватель|media player|кино и тв|фильмы и тв|movies tv|films tv)\b");
    public static bool IsMediaApp(LaunchTarget target) => IsMusicApp(target) || IsVideoApp(target) || IsApplication(target) && target.MediaPlayer;
    private static bool IsApplication(LaunchTarget target) => target.Kind is "app" or "exe" or "shortcut";

    public static MediaChoice Choose(IReadOnlyList<MediaCandidate> sessions, IReadOnlyList<LaunchTarget> catalog, string? preferredId,
        MediaScope scope = MediaScope.Current, string? lastControlled = null)
    {
        // The launch preference applies only when the user explicitly names music.
        if (scope == MediaScope.Music && !string.IsNullOrEmpty(preferredId))
            return Unique(sessions.Where(s => s.CatalogId == preferredId).ToArray(), "Выбранный музыкальный плеер недоступен. Скажите «включи музыку».");
        var eligible = sessions.Where(s => scope switch
        {
            MediaScope.Music => catalog.Any(t => t.Enabled && t.Id == s.CatalogId && IsMediaApp(t)),
            MediaScope.Video => !catalog.Any(t => t.Id == s.CatalogId && IsMusicApp(t) && !IsVideoApp(t)),
            _ => true
        }).ToArray();
        var playing = eligible.Where(s => s.IsPlaying).ToArray();
        var pool = playing.Length > 0 ? playing : eligible;
        if (pool.Length == 1 && sessions.Count(s => s.SourceId == pool[0].SourceId) > 1)
            return Unique(sessions.Where(s => s.SourceId == pool[0].SourceId).ToArray(), "");
        if (pool.Length <= 1) return Unique(pool, "Активного плеера не нашёл. Сначала включите музыку или видео.");
        // A browser can publish several sessions with the same source ID. Never guess between them.
        var current = pool.Where(s => s.IsCurrent).ToArray();
        if (current.Length == 1 && sessions.Count(s => s.SourceId == current[0].SourceId) == 1) return new(current[0].SourceId);
        if (playing.Length == 0 && lastControlled != null)
        {
            var recent = pool.Where(s => s.SourceId == lastControlled).ToArray();
            if (recent.Length == 1) return new(lastControlled);
        }
        return new(null, "Доступно несколько источников. Выберите нужное видео или плеер в Windows либо уточните: «останови музыку» или «пауза видео».");
    }
    private static MediaChoice Unique(IReadOnlyList<MediaCandidate> sessions, string missing) => sessions.Count switch {
        1 => new(sessions[0].SourceId), 0 => new(null, missing),
        _ => new(null, "У приложения несколько источников воспроизведения. Оставьте нужный и повторите команду.") };
}
