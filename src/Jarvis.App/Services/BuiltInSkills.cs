using System.Diagnostics;
using System.Globalization;
using System.IO;
using Jarvis.Core;

namespace Jarvis.App.Services;

public static class BuiltInSkills
{
    public const string HelpText = "Открой YouTube / Кинопоиск / Ozon / Яндекс Еду / ПлатиМаркет\nОткрой почту — Gmail / Открой почту Яндекса\nСайты: меню JARVIS → Сайты. Можно менять адреса и названия.\n\nВключи музыку — выбор приложения, запуск и Play\nПауза / Стоп / Останови / Выключи музыку — пауза плеера\nПродолжи / Дальше / Назад / Следующий / Предыдущий\nЗапусти Яндекс Музыку / Запусти ворд / Запусти эксель\nПлеер выбирается в настройках → Музыка\nСвои названия: через запятую или точку с запятой\n\nЗакрой телеграм / Выключи стим — обычное закрытие окна\nЗакрой игру — если найдена одна запущенная игра Steam\nЗакрой окно / Сверни окно — активное внешнее окно\nПереключись на браузер / Переключись на телеграм\nОткрой папку с Сабнавтикой / Открой каталог Форзы\nОткрой папку Телеграма\nПауза / Продолжи / Следующий трек / Предыдущий трек\n\nОткрой браузер\nЗапусти телеграм / стим / икс бокс — после настройки каталога\nЗапусти игру <название из каталога>\nСделай скриншот / Открой скриншоты\nГромче / Погромче / Сделай громче / Сделай погромче — музыка +10 пунктов\nТише / Потише / Сделай тише / Сделай потише — музыка −10 пунктов\nСистему громче / Систему тише — общая громкость Windows\nГромкость 30 процентов / Громкость тридцать процентов\nВыключи звук / Включи звук\nСколько времени / Какое сегодня число\nПокажи команды\n\nПриложения и игры: меню JARVIS → Приложения и игры.\nСкриншоты сохраняются локально в data/screenshots, все экраны одним PNG.\nКнопка «Стоп» и Esc выключают прослушивание.\nГолосовые «стоп» и «останови» ставят плеер на паузу; во время вопроса отменяют выбор.";

    public static SkillDispatcher Create(string root, Func<IReadOnlyList<LaunchTarget>> targets, WindowControl windows, MusicPlayback music) => new([
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
