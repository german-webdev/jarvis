using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Jarvis.Core;

namespace Jarvis.App.Services;

public record BrowserResult(bool Verified, string Message);

public static class BrowserLauncher
{
    public static Task<SkillResult> OpenSiteAsync(LaunchTarget site, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!site.Enabled || !site.IsValid || site.Kind != "url" || !WebSites.TryNormalizeAddress(site.Target, out var address))
            return Task.FromResult(new SkillResult(false, "Адрес сайта неверный или сайт выключен в каталоге."));
        OpenPage(new Uri(address), token);
        // The OS accepted the URL; this does not prove that the page loaded successfully.
        return Task.FromResult(new SkillResult(false, $"Открываю сайт {site.Name}."));
    }

    internal static void OpenPage(Uri address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }

    // Read the registered https handler; do not parse or execute its shell command string.
    public static string? GetDefaultExecutable()
    {
        uint length = 32768;
        var value = new StringBuilder((int)length);
        var result = AssocQueryString(0x1000, 2, "https", "open", value, ref length);
        return result == 0 && System.IO.File.Exists(value.ToString()) ? value.ToString() : null;
    }

    public static async Task<BrowserResult> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = GetDefaultExecutable();
        if (executable == null)
            return new(false, "Не удалось определить браузер по умолчанию. Выберите браузер для HTTPS в Windows.");
        var name = System.IO.Path.GetFileNameWithoutExtension(executable);
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            ArgumentList = { "about:blank" }
        });
        // Existing browsers may reuse their process. Verify a visible window of the registered executable.
        for (int attempt = 0; attempt < 30; attempt++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var candidate in Process.GetProcessesByName(name))
            {
                using (candidate)
                {
                    try
                    {
                        if (candidate.MainWindowHandle != IntPtr.Zero && IsWindowVisible(candidate.MainWindowHandle)
                            && string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                            return new(true, "Браузер открыт.");
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            await Task.Delay(200, token);
        }
        return new(false, "Команда запуска отправлена, но окно браузера подтвердить не удалось.");
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
    private static extern int AssocQueryString(uint flags, uint str, string assoc, string extra,
        StringBuilder output, ref uint length);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
