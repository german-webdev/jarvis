using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Core;

public record MediaScanResult(IReadOnlyList<LaunchTarget> Files, IReadOnlyList<string> Warnings);

/// <summary>Opt-in, bounded indexing of local audio/video filenames. Never follows directory links.</summary>
public static class MediaLibrary
{
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".ape" };
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".webm", ".mov", ".wmv", ".m4v", ".mpeg", ".mpg", ".ts" };
    public static string? KindForPath(string path) => Audio.Contains(Path.GetExtension(path)) ? "audio" : Video.Contains(Path.GetExtension(path)) ? "video" : null;
    public static bool IsLibraryEntry(LaunchTarget target) => target.Kind is "audio" or "video" or "media-folder";
    public static string IdFor(string path) => "media:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    public static LaunchTarget Folder(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var name = Path.GetFileName(path);
        return new(IdFor(path), string.IsNullOrEmpty(name) ? path : name, "media-folder", path, "Мультимедиа", [], true);
    }
    public static LaunchTarget FileEntry(string path)
    {
        path = Path.GetFullPath(path);
        var kind = KindForPath(path) ?? throw new InvalidDataException("Выбери аудиофайл или видео поддерживаемого формата.");
        var name = Path.GetFileNameWithoutExtension(path);
        return new(IdFor(path), name[..Math.Min(name.Length, 150)], kind, path, Path.GetFileName(Path.GetDirectoryName(path)) ?? "", [], true);
    }
    public static MediaScanResult Scan(IEnumerable<LaunchTarget> folders, CancellationToken token, int limit = 10000)
    {
        var files = new List<LaunchTarget>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>();
        foreach (var folder in folders.Where(t => t.Enabled && t.Kind == "media-folder")) pending.Push((folder.Target, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
        int entries = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(Path.GetFullPath(directory.Path))) continue;
            try
            {
                if ((File.GetAttributes(directory.Path) & FileAttributes.ReparsePoint) != 0)
                { warnings.Add("Папка-ссылка пропущена: " + directory.Path); continue; }
                foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 100000 || files.Count >= limit)
                    { warnings.Add("Достигнут предел сканирования. Добавь более конкретную папку."); return new(files, warnings); }
                    if (Directory.Exists(path))
                    {
                        if (directory.Depth < 32) pending.Push((path, directory.Depth + 1));
                        else warnings.Add("Слишком глубокая папка пропущена: " + path);
                    }
                    else if (KindForPath(path) != null) files.Add(FileEntry(path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add("Папка недоступна: " + directory.Path); }
        }
        return new(files.DistinctBy(t => t.Id).ToArray(), warnings);
    }
}
