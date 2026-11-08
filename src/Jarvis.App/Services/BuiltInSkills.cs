using System.Diagnostics;
using System.Globalization;
using System.IO;
using Jarvis.Core;

namespace Jarvis.App.Services;

public static class BuiltInSkills
{
    public const string HelpText = "Закрой телеграм / Выключи стим — обычное закрытие окна\nЗакрой игру — если найдена одна запущенная игра Steam\nЗакрой окно / Сверни окно — активное внешнее окно\nПереключись на браузер / Переключись на телеграм\nОткрой папку с Сабнавтикой / Открой каталог Форзы\nОткрой папку Телеграма\nПауза / Продолжи / Следующий трек / Предыдущий трек\n\nОткрой браузер\nЗапусти телеграм / стим / икс бокс — после настройки каталога\nЗапусти игру <название из каталога>\nСделай скриншот / Открой скриншоты\nСделай громче / Сделай тише — шаг 10 процентов\nГромкость 30 процентов / Громкость тридцать процентов\nВыключи звук / Включи звук\nСколько времени / Какое сегодня число\nПокажи команды\n\nПриложения и игры: меню Джарвис → Приложения и игры.\nСкриншоты сохраняются локально в data/screenshots, все экраны одним PNG.\nСтоп и Esc выключают прослушивание.";

    public static SkillDispatcher Create(string root, Func<IReadOnlyList<LaunchTarget>> targets, WindowControl windows) => new([
        new DelegateSkill([Intent.CloseTarget, Intent.FocusTarget, Intent.MinimizeWindow, Intent.CloseWindow], windows.ExecuteAsync),
        new DelegateSkill([Intent.MediaPause, Intent.MediaPlay, Intent.MediaNext, Intent.MediaPrevious], MediaControl.ExecuteAsync),
        new DelegateSkill([Intent.OpenTargetFolder], (request, token) =>
        {
            var target = targets().SingleOrDefault(t => t.Enabled && t.Id == request.TargetId);
            return target == null ? Task.FromResult(new SkillResult(false, "Приложение исчезло из каталога.")) : AppLocations.OpenAsync(target, token);
        }),
        new DelegateSkill([Intent.OpenBrowser], async (_, token) => { var result = await BrowserLauncher.OpenAsync(token); return new(result.Verified, result.Message); }),
        new DelegateSkill([Intent.LaunchTarget], (request, token) =>
        {
            var target = targets().SingleOrDefault(t => t.Id == request.TargetId);
            return target == null ? Task.FromResult(new SkillResult(false, "Приложение исчезло из каталога.")) : TargetLauncher.OpenAsync(target, token);
        }),
        new DelegateSkill([Intent.VolumeUp, Intent.VolumeDown, Intent.SetVolume, Intent.Mute, Intent.Unmute], (request, _) => Task.FromResult(VolumeControl.Apply(request))),
        new DelegateSkill([Intent.Screenshot], (_, _) => Task.FromResult(ScreenCapture.Save(Path.Combine(root, "data", "screenshots")))),
        new DelegateSkill([Intent.OpenScreenshots], (_, _) =>
        {
            var path = Path.Combine(root, "data", "screenshots"); Directory.CreateDirectory(path);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.FromResult(new SkillResult(false, "Открываю папку скриншотов."));
        }),
        new DelegateSkill([Intent.Time, Intent.Date], (request, _) =>
        {
            var now = DateTime.Now;
            return Task.FromResult(new SkillResult(true, request.Intent == Intent.Time ? $"Сейчас {now:HH:mm}." : "Сегодня " + now.ToString("d MMMM, dddd.", CultureInfo.GetCultureInfo("ru-RU"))));
        }),
        new DelegateSkill([Intent.Help], (_, _) => Task.FromResult(new SkillResult(true, "Умею запускать приложения, открывать их папки, закрывать и сворачивать окна, переключаться между ними, управлять воспроизведением, делать скриншоты и менять громкость. Точные фразы — в меню «Справка».")))
    ]);
}
