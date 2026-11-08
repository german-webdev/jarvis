using System.IO;
using Microsoft.Win32;

namespace Jarvis.App.Services;

/// <summary>Per-user startup; never requires elevation or changes machine-wide settings.</summary>
public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JarvisVoiceAssistant";

    public static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public static void Restore(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (command == null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled) { Restore(null); return; }
        var executable = Path.Combine(AppContext.BaseDirectory, "Jarvis.exe");
        if (!File.Exists(executable)) throw new IOException(Ui.Text("Не найден Jarvis.exe. Запусти приложение через Start-Jarvis.cmd."));
        Restore($"\"{executable}\" --autostart");
    }
}
