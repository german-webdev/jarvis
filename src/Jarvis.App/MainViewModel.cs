using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Jarvis.App.Services;
using Jarvis.Core;
using NAudio.Wave;

namespace Jarvis.App;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly string _root;
    public AiService Ai { get; }
    private readonly AudioService _audio = new();
    private readonly SettingsStore _settingsStore;
    private readonly LaunchCatalogStore _catalogStore;
    private readonly LaunchCatalogStore _siteStore;
    private List<LaunchTarget> _sites = [];
    public IReadOnlyList<LaunchTarget> Sites => _sites;
    private IReadOnlyList<LaunchTarget> CommandTargets => Targets.Concat(Sites).ToArray();
    private readonly SkillDispatcher _skills;
    private readonly WindowControl _windows = new();
    private readonly MusicPlayback _music;
    private IReadOnlyList<LaunchTarget> _runningTargets = [];
    private readonly LaunchConversation _conversation = new();
    private bool _voiceInteraction;
    private bool _askingChoice;
    public bool AwaitingChoice => _conversation.HasPending;
    public event Action<string>? ClarificationReady;
    private List<LaunchTarget> _targets = [];
    private string? _artifactPath;
    public IReadOnlyList<LaunchTarget> Targets => _targets;
    public string? ArtifactPath { get => _artifactPath; private set { SetProperty(ref _artifactPath, value); OnPropertyChanged(nameof(HasArtifact)); } }
    public bool HasArtifact => ArtifactPath != null;
    private AssistantSettings _settings = new();
    private AssistantPhase _phase = AssistantPhase.Loading;
    private bool _microphoneActive;
    private float _replyVolume = 0.8f;
    private WorkerClient? _worker;
    private CancellationTokenSource _operation = new();
    private readonly DispatcherTimer _watchdog;
    private long _session;
    private bool _acceptTranscript;
    private bool _resumeWake;
    private DateTime _deadline = DateTime.MaxValue;
    private bool _disposed;
    private bool _restarting;
    private string _status = "Подготавливаю голосовой модуль…";
    private string _transcript = "Здесь появится распознанная фраза";
    private const string InitialReply = "Здесь появится ответ JARVIS";
    private string _reply = InitialReply;
    private bool _ready;
    private bool _busy;
    private double _level;
    private Microphone? _selected;

    public string Status { get => Ui.Text(_status); private set => SetProperty(ref _status, value); }
    public string Transcript { get => _transcript == "Здесь появится распознанная фраза" ? Ui.Text(_transcript) : _transcript; private set => SetProperty(ref _transcript, value); }
    public string Reply { get => _reply == InitialReply ? Ui.Text(_reply) : _reply; private set => SetProperty(ref _reply, value); }
    public bool Ready { get => _ready; private set { SetProperty(ref _ready, value); Changed(); } }
    public bool Busy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); } }
    public bool WakeMode => Settings.WakeMode;
    public AssistantSettings Settings => _settings;
    public bool ReduceMotion => Settings.ReduceMotion;
    public string ModeLabel => Ui.Text(WakeMode ? "По имени · JARVIS" : "По кнопке");
    public string VoiceLabel => Ui.Text(Settings.VoiceStyle switch { "robot" => "Синтез", "radio" => "Радиоканал", _ => "Естественный голос" });
    public bool ShowLog { get => Settings.ShowLog; set { if (value != Settings.ShowLog) { try { ApplySettings(Settings with { ShowLog = value }); } catch (Exception ex) { Log("Настройки: " + ex.Message); } } } }
    public bool MicrophoneActive { get => _microphoneActive; private set { SetProperty(ref _microphoneActive, value); OnPropertyChanged(nameof(MicrophoneLabel)); } }
    public string MicrophoneLabel => Ui.Text(MicrophoneActive ? "Микрофон включён" : "Микрофон выключен");
    public AssistantPhase Phase { get => _phase; private set { SetProperty(ref _phase, value); OnPropertyChanged(nameof(PhaseTitle)); } }
    public string PhaseTitle => Ui.Text(Phase switch
    {
        AssistantPhase.Loading => "Знакомлюсь с системой",
        AssistantPhase.WaitingForName => "Я на связи",
        AssistantPhase.Listening => "Слушаю тебя",
        AssistantPhase.Recognizing => "Разбираю команду",
        AssistantPhase.Executing => "Выполняю",
        AssistantPhase.Synthesizing => "Готовлю ответ",
        AssistantPhase.Speaking => "Отвечаю",
        AssistantPhase.Fault => "Нужно внимание",
        _ => "Готов к работе"
    });
    public double Level { get => _level; private set => SetProperty(ref _level, value); }
    public Microphone? SelectedMicrophone { get => _selected; set { SetProperty(ref _selected, value); Changed(); } }
    public bool CanListen => Ready && !Busy && SelectedMicrophone != null;
    public bool CanSpeak => Ready && !Busy;
    public bool CanConfigure => !Busy;
    public ObservableCollection<Microphone> Microphones { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    private readonly List<string> _rawHistory = [];
    public event Action? Initialized;
    public event Action<bool>? CycleCompleted;

    public MainViewModel(string root, IReadOnlyList<LaunchTarget>? targets = null, SkillDispatcher? skills = null)
    {
        _root = root;
        Ai = new AiService(root);
        if (Ai.LoadWarning != null) Log(Ai.LoadWarning);
        Ui.Changed += RefreshLanguage;
        _catalogStore = new(Path.Combine(root, "data", "apps.json"));
        var catalog = _catalogStore.Load(); _targets = targets?.ToList() ?? catalog.Targets;
        if (catalog.Warning != null) Log(catalog.Warning);
        var sitePath = Path.Combine(root, "data", "sites.json");
        _siteStore = new(sitePath);
        var websites = _siteStore.Load();
        _sites = File.Exists(sitePath) ? websites.Targets.Where(t => t.Kind == "url").ToList() : WebSites.Defaults();
        if (websites.Warning != null) Log("Сайты: " + websites.Warning);
        _settingsStore = new SettingsStore(Path.Combine(root, "data", "settings.json"));
        var loaded = _settingsStore.Load();
        _settings = loaded.Settings;
        _music = new MusicPlayback(() => Targets, () => Settings.MusicTargetId, id =>
        {
            if (Settings.MusicTargetId != id) { ApplySettings(Settings with { MusicTargetId = id }); Log("Музыкальное приложение выбрано: " + Targets.FirstOrDefault(t => t.Id == id)?.Name); }
        });
        _skills = skills ?? BuiltInSkills.Create(root, () => CommandTargets, _windows, _music);
        if (loaded.Warning != null) Log(loaded.Warning);
        _audio.Audio += data =>
        {
            if (_worker?.Send(new { type = "audio", session = _session, pcm = Convert.ToBase64String(data) }) != true)
                Dispatch(() => Fail("Очередь звука переполнена. Перезапусти модуль."));
        };
        _audio.Level += value => Dispatch(() => { if (Phase != AssistantPhase.Speaking) Level = Math.Min(100, value * 200); });
        _audio.PlaybackLevel += value => Dispatch(() => { if (Phase == AssistantPhase.Speaking) Level = Math.Min(100, value * 160); });
        _audio.Failed += error => Dispatch(() => Fail("Микрофон: " + error));
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) =>
        { if (DateTime.UtcNow > _deadline) Fail("Модуль не ответил вовремя. Нажми «Перезапустить модуль»."); };
        _watchdog.Start();
        RefreshDevices();
    }

    private void Changed() { OnPropertyChanged(nameof(CanListen)); OnPropertyChanged(nameof(CanSpeak)); OnPropertyChanged(nameof(CanConfigure)); }
    private static void Dispatch(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
    private void Log(string text)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {text}";
        _rawHistory.Insert(0, entry);
        while (_rawHistory.Count > 30) _rawHistory.RemoveAt(30);
        History.Insert(0, Ui.Text(entry));
        while (History.Count > 30) History.RemoveAt(History.Count - 1);
    }

    public void RefreshDevices()
    {
        if (Busy) return;
        try
        {
            var previous = Settings.MicrophoneId ?? SelectedMicrophone?.Id ?? AudioService.DefaultDeviceId();
            Microphones.Clear();
            foreach (var mic in AudioService.Devices()) Microphones.Add(mic);
            SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == previous) ?? Microphones.FirstOrDefault();
            if (Microphones.Count == 0) Status = "Микрофон не найден. Подключи его и обнови список.";
        }
        catch (Exception ex) { Status = "Не удалось получить микрофоны: " + ex.Message; }
    }

    public async Task RestartAsync()
    {
        if (_restarting || _disposed) return;
        _restarting = true;
        try
        {
        Stop();
        Ready = false;
        var previous = _worker;
        _worker = null;
        if (previous != null) await previous.DisposeAsync();
        if (_disposed) return;
        Phase = AssistantPhase.Loading;
        Status = "Загружаю голос и распознавание…";
        _deadline = DateTime.UtcNow.AddMinutes(2);
        var worker = new WorkerClient();
        _worker = worker;
        worker.Message += message => Dispatch(async () =>
        {
            if (_worker != worker || _disposed) return;
            try { await HandleAsync(message); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex.Message); }
        });
        worker.Failed += text => Dispatch(() => { if (_worker == worker && !_disposed) Fail(text); });
        try { worker.Start(_root); }
        catch (Exception ex) { Fail("Запуск модуля: " + ex.Message); }
        }
        finally { _restarting = false; }
    }

    private long NewSession()
    {
        _operation.Cancel(); _operation.Dispose(); _operation = new();
        return ++_session;
    }

    public void Listen()
    {
        if (!CanListen) return;
        _conversation.Clear(); _voiceInteraction = true; _askingChoice = false;
        var session = NewSession();
        _acceptTranscript = true;
        _resumeWake = WakeMode;
        Busy = true;
        Transcript = "…";
        Phase = WakeMode ? AssistantPhase.WaitingForName : AssistantPhase.Listening;
        Status = WakeMode ? "Скажи «Джарвис», затем команду. Между ними можно сделать паузу." : "Скажи команду. Например: открой браузер.";
        _deadline = WakeMode ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(Settings.CommandWaitSeconds + 14);
        try
        {
            Send(new { type = "start", session, mode = WakeMode ? "wake" : "command", wait_seconds = Settings.CommandWaitSeconds, vocabulary = Vocabulary() });
            _audio.Start(SelectedMicrophone!.Id);
            MicrophoneActive = true;
            Log(WakeMode ? "Ожидание имени включено" : "Микрофон включён");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    public void Stop(bool keepChoice = false)
    {
        if (!keepChoice) _conversation.Clear();
        _askingChoice = false; _voiceInteraction = false;
        _acceptTranscript = false;
        _resumeWake = false;
        StopMicrophone();
        _audio.StopPlayback();
        var session = NewSession();
        _worker?.Send(new { type = "cancel", session });
        _deadline = DateTime.MaxValue;
        Busy = false;
        Phase = AssistantPhase.Idle;
        Level = 0;
        Status = "Нажми «Начать», чтобы включить прослушивание.";
    }

    public async Task TestVoiceAsync(AssistantSettings? preview = null)
    {
        if (!Ready || Busy) return;
        _conversation.Clear(); _askingChoice = false; _voiceInteraction = false;
        var session = NewSession();
        Busy = true;
        _lastVerified = false;
        _resumeWake = false;
        Reply = "Системы готовы. Я Джарвис. Слушаю ваши команды.";
        Phase = AssistantPhase.Synthesizing;
        Status = "Готовлю голос…";
        _deadline = DateTime.UtcNow.AddSeconds(30);
        Speak(session, Reply, preview ?? Settings);
        await Task.CompletedTask;
    }

    public void ImportTestAudio(string path)
    {
        if (!Ready || Busy) return;
        _conversation.Clear(); _askingChoice = false; _voiceInteraction = false;
        using var input = new WaveFileReader(path);
        if (input.WaveFormat.SampleRate != 16000 || input.WaveFormat.Channels != 1 || input.WaveFormat.BitsPerSample != 16)
            throw new InvalidDataException("Для теста нужен WAV PCM16 mono 16 kHz.");
        if (input.Length > 640000) throw new InvalidDataException("Тестовая запись длиннее 20 секунд.");
        using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var session = NewSession();
        _acceptTranscript = true;
        _resumeWake = false;
        Busy = true;
        _deadline = DateTime.UtcNow.AddSeconds(45);
        Send(new { type = "transcribe", session, pcm = Convert.ToBase64String(buffer.ToArray()), vocabulary = Vocabulary() });
    }

    private bool _lastVerified;
    private async Task HandleAsync(JsonElement message)
    {
        string type = message.GetProperty("type").GetString()!;
        string Text() => message.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        if (type == "loading") { Status = Text(); return; }
        if (type is "error" or "fatal")
        {
            if (type == "fatal" || message.GetProperty("session").GetInt64() == _session) Fail(Text());
            return;
        }
        if (type == "ready")
        {
            Ready = true; _deadline = DateTime.MaxValue;
            Phase = AssistantPhase.Idle;
            Status = "Нажми «Начать». Голос и микрофон можно изменить в настройках.";
            Log("Модели готовы · локально · CPU"); Initialized?.Invoke(); return;
        }
        var session = message.GetProperty("session").GetInt64();
        if (session != _session) return;
        var token = _operation.Token;
        switch (type)
        {
            case "wake_heard":
                if (Phase == AssistantPhase.WaitingForName)
                    Log("В ожидании имени услышал: «" + Text() + "»");
                break;
            case "activated":
                Phase = AssistantPhase.Listening;
                Status = $"Имя услышано. Жду команду до {Settings.CommandWaitSeconds} секунд…";
                _deadline = DateTime.UtcNow.AddSeconds(Settings.CommandWaitSeconds + 14); Log("Услышал имя"); break;
            case "awaiting_command":
                Phase = AssistantPhase.Listening;
                Status = "Слушаю продолжение…";
                _deadline = DateTime.UtcNow.AddSeconds(Settings.CommandWaitSeconds + 14); break;
            case "command_timeout":
                StopMicrophone(); _acceptTranscript = false;
                Phase = AssistantPhase.Idle; Busy = false; _deadline = DateTime.MaxValue;
                Status = "Команды не было. Возвращаюсь к ожиданию имени.";
                Log("После имени команда не прозвучала");
                await ResumeWakeAsync(session, token, 200); break;
            case "processing":
                if (!message.TryGetProperty("keep_capture", out var keep) || !keep.GetBoolean()) StopMicrophone();
                Phase = AssistantPhase.Recognizing; Status = "Распознаю фразу…";
                _deadline = DateTime.UtcNow.AddSeconds(45); break;
            case "empty":
                StopMicrophone(); _acceptTranscript = false;
                var wasChoosing = _conversation.HasPending;
                _conversation.Clear();
                Phase = AssistantPhase.Idle; Status = wasChoosing ? "Ответа не было. Выбор отменён." : "Речь не обнаружена. Попробуй ещё раз.";
                Busy = false; _deadline = DateTime.MaxValue; CycleCompleted?.Invoke(false);
                if (wasChoosing) await ResumeWakeAsync(session, token, 200);
                break;
            case "transcript":
                if (!_acceptTranscript) return;
                _acceptTranscript = false;
                StopMicrophone(); Transcript = Text();
                var elapsed = message.GetProperty("elapsed_ms").GetInt32();
                Log($"Распознавание: {elapsed} мс · {Transcript}");
                await ExecuteAsync(Transcript, session, token); break;
            case "speech":
                Phase = AssistantPhase.Speaking; Status = "Можно остановить ответ кнопкой «Стоп».";
                _deadline = DateTime.UtcNow.AddSeconds(90);
                await _audio.PlayAsync(Convert.FromBase64String(message.GetProperty("wav").GetString()!), token, _replyVolume);
                if (session != _session) return;
                Busy = false; _deadline = DateTime.MaxValue;
                Phase = AssistantPhase.Idle; Level = 0; Status = "Готов к следующей команде.";
                if (_askingChoice && _conversation.HasPending)
                {
                    _askingChoice = false; _conversation.Renew(DateTimeOffset.UtcNow);
                    if (_voiceInteraction)
                    {
                        // No microphone while TTS plays; a short tail delay prevents hearing our own question.
                        Busy = true;
                        await Task.Delay(700, token);
                        if (session == _session) ListenForChoice();
                    }
                    else { Status = "Жду выбор через меню «Команда текстом»."; ClarificationReady?.Invoke(Reply); }
                    break;
                }
                CycleCompleted?.Invoke(_lastVerified);
                await ResumeWakeAsync(session, token, 700);
                break;
        }
    }

    private void StopMicrophone()
    {
        _audio.StopCapture(); MicrophoneActive = false;
    }

    public void SaveCatalog(IEnumerable<LaunchTarget> targets)
    {
        _conversation.Clear();
        var updated = targets.ToList(); _catalogStore.Save(updated); _targets = updated;
        OnPropertyChanged(nameof(Targets)); Log($"Каталог сохранён · включено {_targets.Count(t => t.Enabled)}");
    }
    public void SaveSites(IEnumerable<LaunchTarget> sites)
    {
        var updated = sites.ToList();
        if (updated.Any(t => t.Kind != "url") || updated.Select(t => t.Id).Distinct().Count() != updated.Count)
            throw new InvalidDataException("В каталоге сайтов есть неверные или повторяющиеся записи.");
        _siteStore.Save(updated); _sites = updated; _conversation.Clear();
        OnPropertyChanged(nameof(Sites)); Log($"Сайты сохранены · включено {_sites.Count(t => t.Enabled)}");
    }
    private string Vocabulary()
    {
        var enabled = (_conversation.ChoosingWindow ? _runningTargets : CommandTargets).Where(t => t.Enabled).ToArray();
        if (_conversation.HasPending)
            enabled = enabled.Where(t => _conversation.CandidateIds.Contains(t.Id)).ToArray();
        return LaunchVocabulary.Build(enabled, _conversation.HasPending);
    }

    public async Task RunTextAsync(string text)
    {
        if (!CanSpeak || string.IsNullOrWhiteSpace(text)) return;
        _voiceInteraction = false;
        var session = NewSession(); Busy = true; _resumeWake = false; _acceptTranscript = false;
        Transcript = text.Trim(); Log("Команда текстом: " + Transcript);
        try { await ExecuteAsync(Transcript, session, _operation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task ExecuteAsync(string text, long session, CancellationToken token)
    {
        Phase = AssistantPhase.Executing; Status = "Выполняю команду…";
        _deadline = DateTime.UtcNow.AddSeconds(30);
        ArtifactPath = null;
        var explicitQuestion = AiRouting.ExplicitQuestion(text);
        if (explicitQuestion != null)
        {
            _conversation.Clear(); _askingChoice = false;
            await AnswerWithAiAsync(explicitQuestion, session, token);
            return;
        }
        var wasChoosing = _conversation.HasPending;
        var preliminary = CommandRouter.Resolve(text, CommandTargets, musicTargetId: Settings.MusicTargetId);
        if (_conversation.ChoosingWindow || preliminary.Intent is Intent.CloseWindow or Intent.MinimizeWindow
            || preliminary.CandidateIntent is Intent.CloseTarget or Intent.FocusTarget)
            _runningTargets = await _windows.CaptureAsync(Targets, token);
        var turn = _conversation.Resolve(text, CommandTargets, DateTimeOffset.UtcNow, _runningTargets, Settings.MusicTargetId);
        if (turn.NeedsAnswer)
        {
            _lastVerified = false; _askingChoice = true; Reply = turn.Question!; Log(Reply);
            Phase = AssistantPhase.Synthesizing; Status = "Уточняю, что запустить…";
            _deadline = DateTime.UtcNow.AddSeconds(30); Speak(session, Reply, Settings); return;
        }
        _askingChoice = false;
        if (AiRouting.CanFallback(Ai.Configuration, turn.Command!, wasChoosing))
        {
            await AnswerWithAiAsync(text, session, token);
            return;
        }
        var result = await _skills.ExecuteAsync(turn.Command!, token);
        if (session != _session) return;
        _lastVerified = result.Verified; Reply = result.Message; ArtifactPath = result.ArtifactPath;
        Log(Reply);
        if (ArtifactPath != null) Log(ArtifactPath);
        if (result.Silent)
        {
            Busy = false; _deadline = DateTime.MaxValue; Phase = AssistantPhase.Idle;
            Status = "Готов. Ответ показан на экране."; CycleCompleted?.Invoke(_lastVerified);
            await ResumeWakeAsync(session, token, 200); return;
        }
        Phase = AssistantPhase.Synthesizing; Status = "Готовлю ответ…";
        _deadline = DateTime.UtcNow.AddSeconds(30); Speak(session, Reply, Settings);
    }

    private async Task AnswerWithAiAsync(string question, long session, CancellationToken token)
    {
        Status = "Жду ответ нейросети…";
        _deadline = DateTime.UtcNow.AddSeconds(120);
        var result = await Ai.AskAsync(question, token);
        if (session != _session) return;
        _lastVerified = result.Verified;
        Reply = result.Message;
        Log(Reply);
        if (!_voiceInteraction)
        {
            Busy = false; _deadline = DateTime.MaxValue; Phase = AssistantPhase.Idle;
            Status = "Готов. Ответ показан на экране.";
            CycleCompleted?.Invoke(result.Verified);
            return;
        }
        Phase = AssistantPhase.Synthesizing; Status = "Готовлю ответ…";
        _deadline = DateTime.UtcNow.AddSeconds(90);
        Speak(session, Reply, Settings);
    }

    private void Speak(long session, string text, AssistantSettings settings)
    {
        _replyVolume = (float)settings.Volume;
        Send(new { type = "synthesize", session, text, voice = settings.Voice,
            style = settings.VoiceStyle, amount = settings.RobotAmount, rate = settings.SpeechRate });
    }

    private void ListenForChoice()
    {
        if (!_conversation.HasPending || SelectedMicrophone == null)
        { _conversation.Clear(); Busy = false; Status = "Для голосового выбора нужен микрофон. Повтори команду."; return; }
        var session = NewSession(); _acceptTranscript = true; Busy = true;
        Phase = AssistantPhase.Listening; Status = "Назови часть или вариант. Повторять «Джарвис» не нужно. Жду 10 секунд.";
        _deadline = DateTime.UtcNow.AddSeconds(24);
        try
        {
            Send(new { type = "start", session, mode = "command", wait_seconds = 10, vocabulary = Vocabulary(), choosing = true });
            _audio.Start(SelectedMicrophone.Id); MicrophoneActive = true; Log("Слушаю уточнение");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task ResumeWakeAsync(long session, CancellationToken token, int delay)
    {
        if (!_resumeWake || _disposed) return;
        await Task.Delay(delay, token);
        if (session == _session) Listen();
    }

    public void ApplySettings(AssistantSettings settings)
    {
        settings = settings.Normalize();
        _settingsStore.Save(settings);
        _settings = settings;
        Ui.Apply(settings.InterfaceLanguage);
        if (!Busy) RefreshDevices();
        foreach (var name in new[] { nameof(Settings), nameof(WakeMode), nameof(ModeLabel), nameof(VoiceLabel), nameof(ReduceMotion), nameof(ShowLog) }) OnPropertyChanged(name);
    }

    public void ClearHistory() { _rawHistory.Clear(); History.Clear(); }

    private void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Status), nameof(Transcript), nameof(Reply), nameof(ModeLabel), nameof(VoiceLabel), nameof(MicrophoneLabel), nameof(PhaseTitle) }) OnPropertyChanged(name);
        History.Clear();
        foreach (var entry in _rawHistory) History.Add(Ui.Text(entry));
    }

    private void Send(object message)
    { if (_worker?.Send(message) != true) throw new IOException("Голосовой модуль не принимает команды."); }

    private void Fail(string text)
    {
        Stop(); Ready = false; Phase = AssistantPhase.Fault; Status = text; Log("Ошибка: " + text); CycleCompleted?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Ui.Changed -= RefreshLanguage;
        Ai.Dispose();
        _watchdog.Stop(); Stop(); _audio.Dispose();
        if (_worker != null) await _worker.DisposeAsync().ConfigureAwait(false);
        _operation.Dispose();
    }
}
