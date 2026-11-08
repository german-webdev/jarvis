using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Jarvis.Core;
using Windows.Management.Deployment;

namespace Jarvis.App.Services;

public static class AppLocations
{
    public static async Task<string?> ResolveAsync(LaunchTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!target.Enabled || !target.IsValid) return null;
        if (target.Kind == "steam") return AppDiscovery.SteamTargets().FirstOrDefault(t => t.Target == target.Target)?.InstallDirectory;
        if (target.Kind == "exe") return File.Exists(target.Target) ? Path.GetDirectoryName(target.Target) : null;
        if (target.Kind == "app" && target.Target.Contains('!'))
        {
            return await Task.Run(() =>
            {
                var family = target.Target.Split('!')[0];
                try { return new PackageManager().FindPackagesForUser("").FirstOrDefault(p => p.Id.FamilyName == family)?.InstalledLocation.Path; }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { return null; }
            }, token);
        }
        // Resolve the registered shell item / shortcut, never run or parse a shell command string.
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? shell = null, folder = null, item = null, shortcut = null;
            try
            {
                string? path;
                if (target.Kind == "shortcut")
                {
                    if (!File.Exists(target.Target)) { completion.TrySetResult(null); return; }
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                    shortcut = ((dynamic)shell!).CreateShortcut(target.Target);
                    path = ((dynamic)shortcut).TargetPath as string;
                }
                else
                {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                    folder = ((dynamic)shell!).NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
                    item = ((dynamic)folder!).ParseName(target.Target);
                    path = item == null ? null : ((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath") as string;
                }
                completion.TrySetResult(path != null && Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetDirectoryName(path) : null);
            }
            catch { completion.TrySetResult(null); }
            finally { foreach (var value in new[] { shortcut, item, folder, shell }) if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        }) { IsBackground = true, Name = "JARVIS folder lookup" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
    }

    public static async Task<SkillResult> OpenAsync(LaunchTarget target, CancellationToken token)
    {
        var path = await ResolveAsync(target, token);
        if (path == null || !Directory.Exists(path)) return new(false, "Папку программы определить или открыть не удалось. Возможно, она защищена Windows или программа удалена.");
        token.ThrowIfCancellationRequested();
        // A single validated directory argument; no command interpolation or wildcard lookup.
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = true, ArgumentList = { path } });
        return new(false, $"Открываю папку: {target.Name}.");
    }
}
