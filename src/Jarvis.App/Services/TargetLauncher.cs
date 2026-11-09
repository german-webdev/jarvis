using System.Diagnostics;
using System.IO;
using Jarvis.Core;

namespace Jarvis.App.Services;

public static class TargetLauncher
{
    public static Task<SkillResult> OpenMediaFolderAsync(LaunchTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!target.Enabled || !target.IsValid) return Task.FromResult(new SkillResult(false, "Запись мультимедиа выключена или неверно настроена."));
        var path = target.Kind == "media-folder" ? target.Target : Path.GetDirectoryName(target.Target)!;
        if (!Directory.Exists(path)) return Task.FromResult(new SkillResult(false, "Папка мультимедиа недоступна. Проверь путь или подключение диска."));
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.FromResult(new SkillResult(false, $"Открываю папку: {target.Name}."));
    }
    public static Task<SkillResult> OpenAsync(LaunchTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!target.Enabled || !target.IsValid) return Task.FromResult(new SkillResult(false, "Это приложение выключено или неверно настроено в каталоге."));
        if (target.Kind == "url") return BrowserLauncher.OpenSiteAsync(target, token);
        if (target.Kind == "media-folder") return OpenMediaFolderAsync(target, token);
        ProcessStartInfo start;
        if (target.Kind is "exe" or "shortcut" or "audio" or "video")
        {
            if (!File.Exists(target.Target)) return Task.FromResult(new SkillResult(false, "Файл приложения не найден. Обнови его запись в каталоге."));
            start = new(target.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.Target)! };
        }
        else if (target.Kind == "steam")
        {
            if (!AppDiscovery.IsSteamInstalled(target.Target)) return Task.FromResult(new SkillResult(false, "Эта игра больше не найдена среди установленных игр Steam. Обнови каталог."));
            start = new("steam://rungameid/" + target.Target) { UseShellExecute = true };
        }
        else start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = true, ArgumentList = { "shell:AppsFolder\\" + target.Target } };
        using var process = Process.Start(start);
        if (target.Kind is "audio" or "video") return Task.FromResult(new SkillResult(false, $"Передал файл приложению по умолчанию: {target.Name}."));
        // A launcher can reuse a process or display its own login/update screen. Do not claim gameplay is ready.
        return Task.FromResult(new SkillResult(false, $"Передал команду запуска: {target.Name}."));
    }
}
