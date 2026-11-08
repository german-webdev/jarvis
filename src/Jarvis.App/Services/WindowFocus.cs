using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Jarvis.App.Services;

/// <summary>Bounded UI Automation fallback. Separate process prevents a hung provider blocking JARVIS.</summary>
public static class WindowFocus
{
    public static async Task TryAsync(nint window, int pid, long started, CancellationToken token)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Jarvis.exe");
        if (!File.Exists(path)) return;
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--focus-window", window.ToString(), pid.ToString(), started.ToString() }) start.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var helper = Process.Start(start);
        if (helper == null) return;
        try { await helper.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(3), token); }
        catch (TimeoutException) { }
        finally { if (!helper.HasExited) helper.Kill(); }
    }
    public static int Run(string[] args)
    {
        if (args.Length != 4 || !long.TryParse(args[1], out var handle) || !int.TryParse(args[2], out var pid) || !long.TryParse(args[3], out var started)) return 2;
        try
        {
            GetWindowThreadProcessId((nint)handle, out uint actualPid);
            using var process = Process.GetProcessById(pid);
            if (actualPid != pid || process.StartTime.ToUniversalTime().Ticks != started) return 2;
            AutomationElement.FromHandle((nint)handle)?.SetFocus();
            return 0;
        }
        catch { return 1; }
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
}
