using System.IO;
using Jarvis.Core;
using Windows.Media.Control;

namespace Jarvis.App.Services;

public sealed class MusicPlayback(Func<IReadOnlyList<LaunchTarget>> catalog, Func<string?> preferred, Action<string> remember)
{
    private string? _lastControlled;
    public void Remember(LaunchTarget target) { if (MusicSelection.IsMusicApp(target) || target.Id == preferred()) remember(target.Id); }

    public static MediaCandidate Describe(GlobalSystemMediaTransportControlsSession session, IReadOnlyList<LaunchTarget> targets)
    {
        string source = session.SourceAppUserModelId;
        var target = targets.Where(t => t.Enabled).FirstOrDefault(t => string.Equals(t.Target, source, StringComparison.OrdinalIgnoreCase)
            || t.Kind == "exe" && (string.Equals(Path.GetFileName(t.Target), source, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(t.Target), source, StringComparison.OrdinalIgnoreCase)));
        return new(source, target?.Id, session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
    }

    public async Task<SkillResult> ExecuteAsync(CommandRequest request, CancellationToken token)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
        var sessions = manager.GetSessions().ToArray();
        var current = manager.GetCurrentSession();
        var choice = MusicSelection.Choose(sessions.Select(s => Describe(s, catalog()) with { IsCurrent = s.SourceAppUserModelId == current?.SourceAppUserModelId }).ToArray(),
            catalog(), preferred(), request.MediaScope, _lastControlled);
        if (choice.SourceId == null) return new(false, choice.Error ?? "Не удалось выбрать плеер.");
        var matching = sessions.Where(s => s.SourceAppUserModelId == choice.SourceId).ToArray();
        if (matching.Length != 1) return new(false, "У приложения несколько источников воспроизведения. Оставь нужный и повтори команду.");
        var session = matching[0];
        if (request.Intent is Intent.MusicVolumeUp or Intent.MusicVolumeDown)
        {
            var selected = Describe(session, catalog());
            var target = catalog().FirstOrDefault(t => t.Enabled && t.Id == selected.CatalogId);
            if (target == null) return new(false, "Не удалось определить громкость этого источника. Скажи «систему тише» или «систему громче».");
            string? executable = target.Kind == "exe" ? target.Target : null;
            if (target.Kind == "app") executable = (await AppLocations.ExecutablesAsync(token)).GetValueOrDefault(target.Target);
            if (executable == null) return new(false, "Громкость этого плеера пока недоступна. Общую громкость можно изменить командой «систему тише» или «систему громче».");
            return PlayerVolume.Change(executable, target.Name, request.Intent == Intent.MusicVolumeUp ? 0.1f : -0.1f, token);
        }
        var result = await MediaControl.ApplyAsync(session, request.Intent, token);
        if (result.Verified) _lastControlled = choice.SourceId;
        var label = catalog().FirstOrDefault(t => t.Id == Describe(session, catalog()).CatalogId)?.Name ?? session.SourceAppUserModelId;
        return result with { Message = label + ": " + result.Message };
    }

    public async Task<SkillResult> StartAsync(CommandRequest request, CancellationToken token)
    {
        var target = catalog().SingleOrDefault(t => t.Id == request.TargetId && t.Enabled);
        if (target == null || !MusicSelection.IsMediaApp(target)) return new(false, "Выбранное музыкальное приложение исчезло из каталога мультимедиа.");
        remember(target.Id);
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
        GlobalSystemMediaTransportControlsSession[] Matching() => manager.GetSessions().Where(s => Describe(s, [target]).CatalogId == target.Id).ToArray();
        var sessions = Matching();
        if (sessions.Length == 0)
        {
            await TargetLauncher.OpenAsync(target, token);
            for (int i = 0; i < 30 && sessions.Length == 0; i++)
            {
                await Task.Delay(400, token); sessions = Matching();
            }
        }
        if (sessions.Length == 0) return new(false, $"Открываю {target.Name}. Плеер ещё не готов: выбери музыку в приложении, затем скажи «продолжи».");
        if (sessions.Length != 1) return new(false, "У этого приложения несколько медиасессий. Оставь нужный плеер открытым и повтори команду.");
        var result = await MediaControl.ApplyAsync(sessions[0], Intent.MediaPlay, token);
        return result with { Message = target.Name + ": " + result.Message };
    }
}
