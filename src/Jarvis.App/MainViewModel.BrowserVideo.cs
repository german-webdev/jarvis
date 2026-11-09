using System.Collections.ObjectModel;
using Jarvis.App.Services;
using Jarvis.Core;

namespace Jarvis.App;

public sealed record VideoChoice(int Number, BrowserVideo Video)
{
    public string Title => Number + ". " + Video.Title;
    public string Channel => Video.Channel;
}

public sealed partial class MainViewModel
{
    private readonly IBrowserVideoService _videoBrowser;
    private bool _browserPending;
    private string _browserStatus = "Открой отдельный браузер для поиска видео. Личные вкладки не используются.";
    public string BrowserStatus { get => _browserStatus.Contains('\n') ? Ui.Text(_browserStatus[.._browserStatus.IndexOf('\n')]) + _browserStatus[_browserStatus.IndexOf('\n')..] : Ui.Text(_browserStatus); private set => SetProperty(ref _browserStatus, value); }
    public bool BrowserConnected => _videoBrowser.Connected;
    public bool BrowserPending => _browserPending;
    public bool CanStopVideo => CanStop || VideoResults.Count > 0;
    public bool CanUseVideo => !Busy && !_browserPending;
    public ObservableCollection<VideoChoice> VideoResults { get; } = [];
    public async Task ConnectVideoBrowserAsync()
    {
        if (!CanUseVideo) return;
        StopVideoChoice();
        _conversation.Clear(); _powerConfirmation.Clear(); _askingChoice = false;
        _voiceInteraction = false; var session = NewSession(); Busy = true;
        var revision = Ai.ConnectionRevision; _aiResponseSession = session;
        bool Current() => session == _session && !_disposed && revision == Ai.ConnectionRevision;
        SetBrowserPending(true); BrowserStatus = "Открываю управляемый браузер…";
        try
        {
            await _videoBrowser.ConnectAsync(_operation.Token);
            if (!Current()) return;
            BrowserStatus = "Управляемый браузер открыт. Введи запрос видео.";
        }
        catch (OperationCanceledException) { if (Current()) BrowserStatus = "Браузер не ответил вовремя. Повтори поиск."; }
        catch (Exception ex) { if (Current()) BrowserStatus = ex is System.IO.InvalidDataException ? ex.Message : "Не удалось открыть браузер. Проверь, установлен ли Edge или Chrome."; }
        finally { if (session == _session) { Busy = false; SetBrowserPending(false); OnPropertyChanged(nameof(BrowserConnected)); } }
    }
    public async Task SearchVideoAsync(string query)
    {
        if (!CanUseVideo) return;
        _conversation.Clear(); _powerConfirmation.Clear(); _askingChoice = false;
        _voiceInteraction = false; var session = NewSession(); Busy = true;
        Transcript = "найди видео " + query;
        await RunBrowserVideoAsync(query, null, session, _operation.Token);
    }
    public async Task OpenVideoAsync(VideoChoice choice)
    {
        if (!CanUseVideo || !VideoResults.Contains(choice)) return;
        _conversation.Clear(); _powerConfirmation.Clear(); _askingChoice = false;
        _voiceInteraction = false; var session = NewSession(); Busy = true;
        await RunBrowserVideoAsync(null, choice.Video, session, _operation.Token);
    }
    private void SetBrowserPending(bool value)
    { _browserPending = value; OnPropertyChanged(nameof(BrowserPending)); OnPropertyChanged(nameof(CanUseVideo)); OnPropertyChanged(nameof(CanEnterText)); }
    private void StopVideoChoice()
    { VideoResults.Clear(); _videoBrowser.Stop(); OnPropertyChanged(nameof(AwaitingChoice)); OnPropertyChanged(nameof(CanStopVideo)); }
    private void StopBrowserVideo()
    {
        if (_browserPending) BrowserStatus = "Действие в браузере остановлено. Уже открытая страница может остаться открытой.";
        StopVideoChoice(); SetBrowserPending(false);
    }
    private async Task RunBrowserVideoAsync(string? query, BrowserVideo? video, long session, CancellationToken token)
    {
        var revision = Ai.ConnectionRevision;
        bool Current() => !_disposed && session == _session && revision == Ai.ConnectionRevision && !token.IsCancellationRequested;
        _aiResponseSession = session;
        _askingChoice = false; VideoResults.Clear(); SetBrowserPending(true);
        Phase = AssistantPhase.Executing; _deadline = DateTime.UtcNow.AddSeconds(40);
        BrowserStatus = Status = query != null ? "Ищу видео в управляемом браузере…" : "Открываю выбранное видео…";
        TaskVoiceInterrupt? interrupt = null;
        BrowserVideoResult result;
        try
        {
            interrupt = BeginTaskVoiceInterrupt(session);
            result = query != null ? await _videoBrowser.SearchAsync(query, token) : await _videoBrowser.OpenAsync(video!, token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { result = new(false, "Связь с браузером потеряна. Открой управляемый браузер заново.", []); }
        finally
        {
            if (interrupt != null) EndTaskVoiceInterrupt(interrupt);
            if (session == _session) { SetBrowserPending(false); OnPropertyChanged(nameof(BrowserConnected)); }
        }
        if (!Current()) return;
        foreach (var entry in result.Videos.Select((v, i) => new VideoChoice(i + 1, v))) VideoResults.Add(entry);
        OnPropertyChanged(nameof(AwaitingChoice)); OnPropertyChanged(nameof(CanStopVideo));
        _askingChoice = VideoResults.Count > 0;
        _lastVerified = result.Verified;
        BrowserStatus = result.Message + (result.Url == null ? "" : "\n" + result.Title + "\n" + result.Url);
        Reply = BrowserStatus;
        if (_askingChoice) Reply += "\n" + string.Join("\n", VideoResults.Select(v => v.Title + " · " + v.Channel));
        Log(Reply);
        if (!_voiceInteraction)
        {
            Busy = false; _deadline = DateTime.MaxValue; Phase = AssistantPhase.Idle;
            Status = "Готов. Ответ показан на экране."; OnPropertyChanged(nameof(CanUseVideo)); CycleCompleted?.Invoke(result.Verified); return;
        }
        Phase = AssistantPhase.Synthesizing; Status = "Готовлю ответ…"; _deadline = DateTime.UtcNow.AddSeconds(90);
        // URL is shown, not spoken. No playback claim is made.
        Speak(session, _askingChoice ? Reply : Ui.Text(result.Message) + " " + result.Title, Settings);
    }
    private async Task<bool> TryBrowserCommandAsync(string text, long session, CancellationToken token)
    {
        if (_conversation.HasPending) return false;
        var query = Ai.Configuration.UseForUnknownCommands && Ai.Configuration.Active?.SupportsBrowserTools == true
            ? null : BrowserVideoCommands.Query(text);
        if (query != null) { await RunBrowserVideoAsync(query, null, session, token); return true; }
        if (VideoResults.Count == 0) return false;
        if (BrowserVideoCommands.Choice(text) is { } index)
        {
            if (index < VideoResults.Count) await RunBrowserVideoAsync(null, VideoResults[index].Video, session, token);
            else
            {
                Reply = Ui.Text("Такого номера нет. Выбери видео из списка."); BrowserStatus = Reply;
                _askingChoice = true;
                if (_voiceInteraction) { Phase = AssistantPhase.Synthesizing; Speak(session, Reply, Settings); }
                else { Busy = false; Phase = AssistantPhase.Idle; _deadline = DateTime.MaxValue; }
            }
            return true;
        }
        if (CommandRouter.Normalize(text) is "отмена" or "стоп" or "останови" or "cancel" or "stop")
        {
            StopVideoChoice(); _askingChoice = false; Reply = BrowserStatus = "Выбор видео отменён.";
            if (_voiceInteraction) { Phase = AssistantPhase.Synthesizing; Speak(session, Ui.Text(Reply), Settings); }
            else { Reply = Ui.Text(Reply); Busy = false; Phase = AssistantPhase.Idle; _deadline = DateTime.MaxValue; }
            return true;
        }
        StopVideoChoice(); // An unrelated command must not leave an actionable stale choice.
        return false;
    }
}
