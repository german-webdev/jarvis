using System.Diagnostics;
using System.IO;
using Jarvis.Core;

namespace Jarvis.App.Services;

public interface IPowerControl
{
    Task RequestAsync(Intent intent, CancellationToken token);
}

public sealed class PowerControl : IPowerControl
{
    // /t > 0 implies /f in Windows. Always use zero, and never force applications closed.
    public static ProcessStartInfo ShutdownRequest(Intent intent)
    {
        if (intent is not (Intent.ShutdownComputer or Intent.RestartComputer))
            throw new ArgumentOutOfRangeException(nameof(intent));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(intent == Intent.ShutdownComputer ? "/s" : "/r");
        start.ArgumentList.Add("/t");
        start.ArgumentList.Add("0");
        return start;
    }

    public async Task RequestAsync(Intent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (intent is Intent.SleepComputer or Intent.HibernateComputer)
        {
            var accepted = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return System.Windows.Forms.Application.SetSuspendState(
                    intent == Intent.SleepComputer ? System.Windows.Forms.PowerState.Suspend : System.Windows.Forms.PowerState.Hibernate,
                    force: false, disableWakeEvent: false);
            }, token);
            if (!accepted) throw new InvalidOperationException("Windows не разрешила сон или гибернацию. Этот режим может быть недоступен в настройках питания.");
            return;
        }
        using var process = Process.Start(ShutdownRequest(intent)) ?? throw new IOException("Не удалось передать команду Windows.");
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows отклонила команду питания (код {process.ExitCode}).");
    }
}
