using System.Diagnostics;
using System.Globalization;
using System.IO;
using Jarvis.Core;

namespace Jarvis.App.Services;

public static class BuiltInSkills
{
    public const string HelpText = "Открой браузер\nЗапусти приложение [название] / Запусти игру [название]\nОткрой сайт [название] / Открой почту\nПереключись на приложение [название]\nЗакрой приложение [название] / Закрой игру [название]\nЗакрой окно / Сверни окно\nОткрой папку [название] / Открой каталог [название]\nВключи музыку / Пауза / Стоп / Останови / Выключи музыку\nПродолжи / Дальше / Назад / Следующий / Предыдущий\nГромче / Сделай громче / Тише / Сделай потише\nСистему громче / Систему тише / Громкость [число] процентов\nВыключи звук / Включи звук\nСделай скриншот / Открой скриншоты\nСколько времени / Какое сегодня число\nСпроси нейросеть [вопрос] — при настроенном подключении\n\nВместо [название] произнеси название из своего каталога. Каталоги приложений, игр и сайтов доступны в меню JARVIS. Подробная справка с категориями и поиском — в меню «Справка → Что я умею».";

    public static SkillDispatcher Create(string root, Func<IReadOnlyList<LaunchTarget>> targets, WindowControl windows, MusicPlayback music) => new([
        new DelegateSkill([Intent.AssistantPresent], (_, _) => Task.FromResult(new SkillResult(true, "А я кто, просячий хвостик?"))),
        new DelegateSkill([Intent.HowAreYou], (_, _) => Task.FromResult(new SkillResult(true, "Всё хорошо. На связи и готов помочь."))),
        new DelegateSkill([Intent.CloseTarget, Intent.FocusTarget, Intent.MinimizeWindow, Intent.CloseWindow], windows.ExecuteAsync),
        new DelegateSkill([Intent.MediaPause, Intent.MediaPlay, Intent.MediaNext, Intent.MediaPrevious, Intent.MusicVolumeUp, Intent.MusicVolumeDown], music.ExecuteAsync),
        new DelegateSkill([Intent.StartMusic], music.StartAsync),
        new DelegateSkill([Intent.OpenTargetFolder], (request, token) =>
        {
            var target = targets().SingleOrDefault(t => t.Enabled && t.Id == request.TargetId);
            return target == null ? Task.FromResult(new SkillResult(false, "Приложение исчезло из каталога.")) : AppLocations.OpenAsync(target, token);
        }),
        new DelegateSkill([Intent.OpenBrowser], async (_, token) => { var result = await BrowserLauncher.OpenAsync(token); return new(result.Verified, result.Message); }),
        new DelegateSkill([Intent.LaunchTarget], async (request, token) =>
        {
            var target = targets().SingleOrDefault(t => t.Id == request.TargetId);
            if (target == null) return new SkillResult(false, "Приложение исчезло из каталога.");
            var result = await TargetLauncher.OpenAsync(target, token);
            if (target.Kind != "url") music.Remember(target);
            return result;
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
        new DelegateSkill([Intent.Help], (_, _) => Task.FromResult(new SkillResult(true, "Умею открывать сайты, запускать приложения, открывать их папки, закрывать и сворачивать окна, переключаться между ними, управлять воспроизведением, делать скриншоты и менять громкость. Точные фразы — в меню «Справка».")))
    ]);
}
