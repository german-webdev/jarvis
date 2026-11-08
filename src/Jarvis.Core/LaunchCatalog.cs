using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace Jarvis.Core;

public sealed record LaunchTarget(string Id, string Name, string Kind, string Target, string Source, string[] Aliases, bool Enabled = false)
{
    [JsonIgnore] public string[] AutomaticAliases => NameVariants.Automatic(Name);
    [JsonIgnore] public IEnumerable<string> Names => new[] { Name }.Concat(AutomaticAliases).Concat((Aliases ?? []).SelectMany(NameVariants.Expand)).Distinct();
    [JsonIgnore] public bool IsValid => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 150
        && (Aliases == null || Aliases.Length <= 20 && Aliases.All(a => a != null && a.Length <= 100))
        && !string.IsNullOrWhiteSpace(Target) && Target.Length <= 2048 && (Kind switch
        {
            "steam" => Regex.IsMatch(Target, @"^[1-9][0-9]{0,9}$"),
            "app" => Regex.IsMatch(Target, @"^[\p{L}\p{N}_.!{}\-]{1,256}$") || Regex.IsMatch(Target, "^\\{[A-Fa-f0-9-]{36}\\}\\\\[^\"<>|:]{1,220}$"),
            "exe" => Path.IsPathFullyQualified(Target) && Path.GetExtension(Target).Equals(".exe", StringComparison.OrdinalIgnoreCase),
            "shortcut" => Path.IsPathFullyQualified(Target) && Path.GetExtension(Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase),
            _ => false
        });
}
public sealed class LaunchCatalogStore(string path)
{
    public (List<LaunchTarget> Targets, string? Warning) Load()
    {
        if (!File.Exists(path)) return ([], null);
        try
        {
            var values = JsonSerializer.Deserialize<List<LaunchTarget>>(File.ReadAllText(path)) ?? [];
            if (values.Any(t => t is null || !t.IsValid)) throw new JsonException();
            return (values.DistinctBy(t => t.Id).ToList(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return ([], "Каталог не прочитан. Исходный файл сохранён, приложения нужно проверить в настройках."); }
    }
    public void Save(IEnumerable<LaunchTarget> targets)
    {
        var values = targets.ToArray();
        if (values.Any(t => !t.IsValid)) throw new InvalidDataException("В каталоге есть некорректная запись.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
