using Jarvis.Core;
using Windows.Media.Control;

namespace Jarvis.App.Services;

public static class MediaControl
{
    public static async Task<SkillResult> ExecuteAsync(CommandRequest request, CancellationToken token)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
        var session = manager.GetCurrentSession();
        if (session == null) return new(false, "Активного плеера не нашёл. Сначала включи музыку или видео.");
        return await ApplyAsync(session, request.Intent, token);
    }

    public static async Task<SkillResult> ApplyAsync(GlobalSystemMediaTransportControlsSession session, Intent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = session.GetPlaybackInfo();
        if (intent == Intent.MediaPause && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
            return new(true, "Воспроизведение уже на паузе.");
        if (intent == Intent.MediaPlay && info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return new(true, "Воспроизведение уже идёт.");
        bool available = intent switch {
            Intent.MediaPause => info.Controls.IsPauseEnabled, Intent.MediaPlay => info.Controls.IsPlayEnabled,
            Intent.MediaNext => info.Controls.IsNextEnabled, Intent.MediaPrevious => info.Controls.IsPreviousEnabled, _ => false };
        if (!available) return new(false, "Этот плеер не поддерживает такую команду через Windows.");
        bool accepted = await (intent switch {
            Intent.MediaPause => session.TryPauseAsync(), Intent.MediaPlay => session.TryPlayAsync(),
            Intent.MediaNext => session.TrySkipNextAsync(), _ => session.TrySkipPreviousAsync()
        }).AsTask(token);
        if (!accepted) return new(false, "Плеер не принял команду.");
        if (intent is Intent.MediaPlay or Intent.MediaPause)
        {
            var expected = intent == Intent.MediaPlay ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            for (int i = 0; i < 8; i++)
            {
                if (session.GetPlaybackInfo().PlaybackStatus == expected) return new(true, intent == Intent.MediaPlay ? "Продолжаю воспроизведение." : "Поставил на паузу.");
                await Task.Delay(150, token);
            }
        }
        return new(false, "Команда передана плееру.");
    }
}
