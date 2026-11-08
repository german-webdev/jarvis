using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Jarvis.Core;

namespace Jarvis.App.Services;

public sealed class WindowControl
{
    private sealed record Entry(LaunchTarget Target, nint Handle, int Pid, long Started);
    private Dictionary<string, Entry> _entries = [];
    private IReadOnlyDictionary<string, string> _executables = new Dictionary<string, string>();
    private DateTime _pathsUpdated;
    public async Task<IReadOnlyList<LaunchTarget>> CaptureAsync(IReadOnlyList<LaunchTarget> catalog, CancellationToken token)
    {
        if (DateTime.UtcNow - _pathsUpdated > TimeSpan.FromSeconds(30))
        {
            _pathsUpdated = DateTime.UtcNow;
            try { _executables = await AppLocations.ExecutablesAsync(token); }
            catch (TimeoutException) { } // Keep known identities; a slow shell must not disable all voice commands.
        }
        return Capture(catalog);
    }
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "dwm", "winlogon", "csrss", "lsass", "services", "sihost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost" };

    public IReadOnlyList<LaunchTarget> Capture(IReadOnlyList<LaunchTarget> catalog)
    {
        var steam = AppDiscovery.SteamTargets();
        var browser = BrowserLauncher.GetDefaultExecutable();
        var entries = new Dictionary<string, Entry>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0 || GetWindowTextLength(window) == 0) return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == Environment.ProcessId || pid == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (Excluded.Contains(process.ProcessName)) return true;
                var className = new StringBuilder(256); GetClassName(window, className, className.Capacity);
                if (className.ToString() is "Shell_TrayWnd" or "Progman" or "WorkerW") return true;
                string? executable = process.MainModule?.FileName;
                if (executable == null) return true;
                var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
                string? appId = null;
                uint count = 512; var idBuffer = new StringBuilder((int)count);
                if (GetApplicationUserModelId(process.Handle, ref count, idBuffer) == 0) appId = idBuffer.ToString();
                var matched = catalog.Where(t => t.Enabled && (
                    t.Kind == "exe" && SamePath(t.Target, executable)
                    || t.Kind == "app" && appId != null && t.Target == appId
                    || t.Kind == "app" && _executables.TryGetValue(t.Target, out var registered) && SamePath(registered, executable)
                    || t.Kind == "steam" && steam.Any(s => s.Target == t.Target && Inside(executable, s.InstallDirectory))
                    || t.Kind == "app" && KnownProcess(t.Name, process.ProcessName))).ToArray();
                var primary = matched.FirstOrDefault();
                var name = primary?.Name ?? process.ProcessName;
                var aliases = matched.SelectMany(t => t.Names).ToList();
                aliases.Add(process.ProcessName);
                if (browser != null && SamePath(browser, executable)) aliases.Add("браузер");
                if (matched.Any(t => t.Kind == "steam")) { aliases.Add("игру"); aliases.Add("игра"); }
                var started = process.StartTime.ToUniversalTime().Ticks;
                string id = $"window:{pid}:{started}:{window}";
                var target = new LaunchTarget(id, name, "window", window.ToString(), title.ToString(), aliases.Distinct().ToArray(), true);
                entries[id] = new(target, window, (int)pid, started);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { }
            return true;
        }, 0);
        _entries = entries;
        return entries.Values.Select(e => e.Target).ToArray();
    }

    public async Task<SkillResult> ExecuteAsync(CommandRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Entry? entry = request.TargetId != null ? _entries.GetValueOrDefault(request.TargetId)
            : _entries.Values.FirstOrDefault(e => e.Handle == GetForegroundWindow());
        if (entry == null || !Current(entry)) return new(false, "Подходящего открытого окна не нашёл. Назови приложение или переключись на нужное окно.");
        if (request.Intent is Intent.CloseTarget or Intent.CloseWindow)
        {
            if (!PostMessage(entry.Handle, 0x0010, 0, 0)) return new(false, "Windows не разрешила закрыть это окно.");
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(150, token);
                if (!IsWindow(entry.Handle) || !IsWindowVisible(entry.Handle))
                    return new(true, $"Окно закрыто: {entry.Target.Name}.");
            }
            return new(false, "Запрос на закрытие отправлен. Возможно, приложение ждёт сохранения или подтверждения на экране.");
        }
        if (request.Intent == Intent.MinimizeWindow)
        {
            ShowWindowAsync(entry.Handle, 6);
            await Task.Delay(200, token);
            return IsIconic(entry.Handle) ? new(true, "Окно свёрнуто.") : new(false, "Сворачивание окна не подтверждено.");
        }
        if (request.Intent == Intent.FocusTarget)
        {
            if (IsIconic(entry.Handle)) ShowWindowAsync(entry.Handle, 9);
            SetForegroundWindow(entry.Handle);
            await Task.Delay(200, token);
            if (GetForegroundWindow() != entry.Handle && Current(entry))
                await WindowFocus.TryAsync(entry.Handle, entry.Pid, entry.Started, token);
            return GetForegroundWindow() == entry.Handle ? new(true, $"Переключился на {entry.Target.Name}.")
                : new(false, "Windows не разрешила перевести окно на передний план. Выбери его на панели задач.");
        }
        return new(false, "Действие с окном не поддерживается.");
    }

    private static bool Current(Entry entry)
    {
        if (!IsWindow(entry.Handle) || !IsWindowVisible(entry.Handle)) return false;
        GetWindowThreadProcessId(entry.Handle, out uint pid);
        if (pid != entry.Pid || pid == Environment.ProcessId) return false;
        try { using var process = Process.GetProcessById(entry.Pid); return process.StartTime.ToUniversalTime().Ticks == entry.Started; }
        catch { return false; }
    }
    public static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    public static bool Inside(string file, string? directory) => directory != null
        && Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool KnownProcess(string name, string process) => CommandRouter.Normalize(name) == CommandRouter.Normalize(process)
        || CommandRouter.Normalize(name) == "steam" && process.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)
        || CommandRouter.Normalize(name) == "xbox" && process.Equals("XboxPcApp", StringComparison.OrdinalIgnoreCase);

    private delegate bool EnumProc(nint window, nint param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder id);
}
