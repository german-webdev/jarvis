using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Jarvis.Core;

namespace Jarvis.App.Services;

public record DiscoveryResult(List<LaunchTarget> Targets, List<string> Warnings);
public static class AppDiscovery
{
    // Shell enumeration runs on its own STA; it never launches the discovered applications.
    public static Task<DiscoveryResult> ScanAsync()
    {
        var completion = new TaskCompletionSource<DiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var result = new DiscoveryResult([], []);
            try { ReadShell(result); } catch { result.Warnings.Add("Windows не предоставила список приложений."); }
            try { ReadSteam(result); } catch { result.Warnings.Add("Часть библиотеки Steam недоступна."); }
            completion.SetResult(result with { Targets = result.Targets.DistinctBy(t => t.Id)
                .GroupBy(t => (t.Source, Name: CommandRouter.Normalize(t.Name)))
                .Select(g => g.OrderBy(t => t.Kind == "app" ? 0 : 1).ThenBy(t => t.Id, StringComparer.Ordinal).First())
                .OrderBy(t => t.Name).ToList() });
        }) { IsBackground = true, Name = "JARVIS application discovery" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static void ReadShell(DiscoveryResult result)
    {
        dynamic? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = shell!.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            items = folder.Items();
            for (int i = 0; i < (int)items.Count; i++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(i);
                    string name = item.Name, id = item.Path;
                    var kind = Path.IsPathFullyQualified(id) && Path.GetExtension(id).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "exe" : "app";
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, "uninstall|деинстал|удаление|удалить", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    string[] aliases = name.ToLowerInvariant() switch
                    {
                        "telegram" => ["телеграм", "телеграмм"],
                        "steam" => ["стим"],
                        "xbox" => ["иксбокс", "икс бокс", "эксбокс"],
                        _ => []
                    };
                    var target = new LaunchTarget("app:" + id, name, kind, id, "Windows / Xbox", aliases, true);
                    if (target.IsValid) result.Targets.Add(target);
                }
                catch { result.Warnings.Add("Одна из записей Windows пропущена."); }
                finally { Release(item); }
            }
        }
        finally { Release(items); Release(folder); Release(shell); }
    }
    private static void ReadSteam(DiscoveryResult result)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string root = key?.GetValue("SteamPath") as string ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var folders = new List<string> { root };
        var libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraries)) folders.AddRange(SteamManifest.LibraryPaths(File.ReadAllText(libraries)));
        foreach (var library in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var target = SteamManifest.ReadApp(File.ReadAllText(manifest), apps);
                        if (target != null) result.Targets.Add(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Warnings.Add("Одна из игр Steam пропущена."); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Warnings.Add("Библиотека Steam недоступна: " + library); }
        }
    }
    public static bool IsSteamInstalled(string id)
    {
        var result = new DiscoveryResult([], []); ReadSteam(result);
        return result.Targets.Any(t => t.Target == id);
    }
    public static IReadOnlyList<LaunchTarget> SteamTargets()
    {
        var result = new DiscoveryResult([], []);
        try { ReadSteam(result); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.Targets;
    }
}
