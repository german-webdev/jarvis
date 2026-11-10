using System.Windows;
using System.Windows.Threading;
using Jarvis.App.Services;
using Jarvis.Core;
using Windows.Media.Control;

namespace Jarvis.App;

public partial class MediaPage
{
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _playbackLifetime;
    private GlobalSystemMediaTransportControlsSession? _displayedSession;
    private bool _refreshingPlayback;
    private bool _controllingPlayback;

    private void InitializePlayback(bool monitor)
    {
        PlaybackState.Text = Ui.Text("Текущий источник");
        PlaybackTitle.Text = Ui.Text("Нет активного воспроизведения");
        PlaybackSource.Text = Ui.Text("Запустите музыку или видео в любом приложении.");
        PlaybackButton.Content = Ui.Text("Пауза");
        if (!monitor) return;
        _playbackTimer.Tick += async (_, _) => await RefreshPlaybackAsync();
        Loaded += async (_, _) =>
        {
            _playbackLifetime = new CancellationTokenSource();
            _playbackTimer.Start(); await RefreshPlaybackAsync();
        };
        Unloaded += (_, _) =>
        {
            _playbackTimer.Stop(); _playbackLifetime?.Cancel(); _playbackLifetime?.Dispose(); _playbackLifetime = null;
        };
    }

    private async Task RefreshPlaybackAsync()
    {
        if (_refreshingPlayback || _controllingPlayback || _playbackLifetime == null) return;
        _refreshingPlayback = true;
        var token = _playbackLifetime.Token;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(timeout.Token);
            var sessions = manager.GetSessions().ToArray();
            var current = manager.GetCurrentSession();
            var targets = _runtime.Targets.Concat(_runtime.Media).ToArray();
            var choice = MusicSelection.Choose(sessions.Select(s => MusicPlayback.Describe(s, targets) with {
                IsCurrent = s.SourceAppUserModelId == current?.SourceAppUserModelId }).ToArray(), targets,
                _runtime.Settings.MusicTargetId, MediaScope.Current);
            var matching = sessions.Where(s => s.SourceAppUserModelId == choice.SourceId).ToArray();
            var session = matching.Length == 1 ? matching[0] : null;
            if (session == null)
            {
                token.ThrowIfCancellationRequested(); _displayedSession = null; PlaybackButton.IsEnabled = false;
                PlaybackState.Text = Ui.Text("Текущий источник");
                PlaybackTitle.Text = Ui.Text("Нет выбранного источника");
                PlaybackSource.Text = sessions.Length == 0 ? Ui.Text("Запустите музыку или видео в любом приложении.") : Ui.Text(choice.Error ?? "Выберите источник в приложении.");
                return;
            }
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(timeout.Token);
            token.ThrowIfCancellationRequested();
            _displayedSession = session;
            var info = session.GetPlaybackInfo();
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            PlaybackState.Text = Ui.Text(info.PlaybackStatus switch {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Сейчас воспроизводится",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Воспроизведение приостановлено",
                _ => "Источник готов к воспроизведению" });
            PlaybackTitle.Text = string.IsNullOrWhiteSpace(properties.Title) ? Ui.Text("Название недоступно") : properties.Title;
            var candidate = MusicPlayback.Describe(session, targets);
            PlaybackSource.Text = targets.FirstOrDefault(t => t.Id == candidate.CatalogId)?.Name ?? session.SourceAppUserModelId;
            PlaybackButton.Content = Ui.Text(playing ? "Пауза" : "Продолжить");
            PlaybackButton.IsEnabled = playing ? info.Controls.IsPauseEnabled : info.Controls.IsPlayEnabled;
        }
        catch (OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                _displayedSession = null; PlaybackButton.IsEnabled = false;
                PlaybackTitle.Text = Ui.Text("Источник временно недоступен");
            }
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                _displayedSession = null; PlaybackButton.IsEnabled = false;
                PlaybackTitle.Text = Ui.Text("Источник временно недоступен");
                PlaybackSource.Text = Ui.Text("Windows не передала сведения о воспроизведении.");
            }
        }
        finally { _refreshingPlayback = false; }
    }

    private async void PlaybackClick(object sender, RoutedEventArgs e)
    {
        if (_displayedSession is not { } displayed || _playbackLifetime == null || _controllingPlayback) return;
        _controllingPlayback = true; PlaybackButton.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_playbackLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(timeout.Token);
            var matching = manager.GetSessions().Where(s => s.SourceAppUserModelId == displayed.SourceAppUserModelId).ToArray();
            if (matching.Length != 1) { Status.Text = Ui.Text("Источник изменился. Повторите действие."); return; }
            var session = matching[0];
            var playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var result = await MediaControl.ApplyAsync(session, playing ? Intent.MediaPause : Intent.MediaPlay, timeout.Token);
            Status.Text = Ui.Text(result.Message);
        }
        catch (OperationCanceledException) { }
        catch { Status.Text = Ui.Text("Не удалось управлять воспроизведением."); }
        finally { _controllingPlayback = false; await RefreshPlaybackAsync(); }
    }
}
