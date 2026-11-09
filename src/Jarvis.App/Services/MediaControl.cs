using Jarvis.Core;
using Windows.Media.Control;

namespace Jarvis.App.Services;

public static class MediaControl
{
    public static async Task<SkillResult> ApplyAsync(GlobalSystemMediaTransportControlsSession session, Intent intent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = session.GetPlaybackInfo();
        if (intent is Intent.MediaSeekForward or Intent.MediaSeekBackward)
        {
            if (!info.Controls.IsPlaybackPositionEnabled) return new(false, "Этот источник не поддерживает перемотку через Windows.");
            var timeline = session.GetTimelineProperties();
            if (timeline.MaxSeekTime <= timeline.MinSeekTime) return new(false, "У этого источника нет доступного диапазона перемотки.");
            var position = timeline.Position + TimeSpan.FromSeconds(intent == Intent.MediaSeekForward ? 10 : -10);
            var ticks = Math.Clamp(position.Ticks, timeline.MinSeekTime.Ticks, timeline.MaxSeekTime.Ticks);
            bool acceptedSeek = await session.TryChangePlaybackPositionAsync(ticks).AsTask(token);
            return new(false, acceptedSeek ? "Передал команду перемотки на 10 секунд." : "Источник не принял перемотку.");
        }
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
