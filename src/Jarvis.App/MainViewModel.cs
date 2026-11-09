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

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly string _root;
    public AiService Ai { get; }
    private readonly IAudioService _audio;
    private readonly Func<IWorkerClient> _workerFactory;
    private readonly SettingsStore _settingsStore;
    private readonly LaunchCatalogStore _catalogStore;
    private readonly LaunchCatalogStore _siteStore;
    private List<LaunchTarget> _sites = [];
    public IReadOnlyList<LaunchTarget> Sites => _sites;
    private readonly LaunchCatalogStore _mediaStore;
    private List<LaunchTarget> _media = [];
    public IReadOnlyList<LaunchTarget> Media => _media;
    public IReadOnlyList<LaunchTarget> MediaApps => Targets.Where(MusicSelection.IsMediaApp).Concat(Media.Where(MusicSelection.IsMediaApp)).DistinctBy(t => t.Id).ToArray();
    private IReadOnlyList<LaunchTarget> CommandTargets => Targets.Concat(Sites).Concat(Media).DistinctBy(t => t.Id).ToArray();
    private readonly SkillDispatcher _skills;
    private readonly WindowControl _windows = new();
    private readonly ILocalActionWindows _actionWindows;
    private readonly MusicPlayback _music;
    private IReadOnlyList<LaunchTarget> _runningTargets = [];
    private readonly LaunchConversation _conversation = new();
    private readonly PowerConfirmation _powerConfirmation = new();
    private readonly IPowerControl _power;
    private Intent? _powerAfterSpeech;
    private bool _voiceInteraction;
    private bool _aiPending;
    private long _aiResponseSession = -1;
    private bool _askingChoice;
    public bool AwaitingChoice => _conversation.HasPending || _powerConfirmation.HasPending || VideoResults.Count > 0;
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
    private readonly Queue<string> _speechParts = new();
    private AssistantSettings? _speechSettings;
    private IWorkerClient? _worker;
    private long _captureSession;
    private long _voiceCaptureSession = -1;
    private long _nextInterruptSession;
    private TaskVoiceInterrupt? _taskInterrupt;
    private sealed record TaskVoiceInterrupt(long CaptureSession, long OwnerSession, long Revision);
    private CancellationTokenSource _operation = new();
    private readonly DispatcherTimer _watchdog;
    private long _session;
    private bool _acceptTranscript;
    private bool _resumeWake;
    private bool ResumeWake
    {
        get => _resumeWake;
        set { if (SetProperty(ref _resumeWake, value)) Changed(); }
    }
    private bool _followUp;
    private bool _exitAfterSpeech;
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
    public bool Busy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); OnPropertyChanged(nameof(CanUseVideo)); OnPropertyChanged(nameof(CanStopVideo)); } }
    public bool WakeMode => Settings.WakeMode;
    public AssistantSettings Settings => _settings;
    public bool ReduceMotion => Settings.ReduceMotion;
    public string ModeLabel => Ui.Text(WakeMode ? "По имени · JARVIS" : "По кнопке");
    public string VoiceLabel => Ui.Text(Settings.VoiceStyle switch { "robot" => "Синтез", "radio" => "Радиоканал", _ => "Естественный голос" });
    public bool ShowLog { get => Settings.ShowLog; set { if (value != Settings.ShowLog) { try { ApplySettings(Settings with { ShowLog = value }); } catch (Exception ex) { Log("Настройки: " + ex.Message); } } } }
    public bool MicrophoneActive { get => _microphoneActive; private set { SetProperty(ref _microphoneActive, value); OnPropertyChanged(nameof(MicrophoneLabel)); Changed(); } }
    public string MicrophoneLabel => Ui.Text(MicrophoneActive ? "JARVIS слушает" : "JARVIS не слушает");
    public AssistantPhase Phase { get => _phase; private set { SetProperty(ref _phase, value); OnPropertyChanged(nameof(PhaseTitle)); OnPropertyChanged(nameof(CanRestart)); } }
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
    public bool CanEnterText => CanSpeak || _aiPending || _browserPending;
    public bool CanConfigure => !Busy;
    public bool CanRestart => !_disposed && !_restarting && !Ready && !Busy && Phase == AssistantPhase.Fault;
    public bool CanStop => Busy || MicrophoneActive || ResumeWake;
    public bool CanToggleListening => CanStop || CanListen;
    public string ListeningActionLabel => Ui.Text(CanStop ? "Остановить" : "Начать прослушивание");
    public ObservableCollection<Microphone> Microphones { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    private readonly List<string> _rawHistory = [];
    public event Action? Initialized;
    public event Action<bool>? CycleCompleted;
    public event Action? ExitRequested;

    public MainViewModel(string root, IReadOnlyList<LaunchTarget>? targets = null, SkillDispatcher? skills = null, IPowerControl? power = null, AiService? ai = null, ILocalActionWindows? actionWindows = null,
        IAudioService? audio = null, Func<IWorkerClient>? workerFactory = null, IBrowserVideoService? videoBrowser = null)
    {
        _audio = audio ?? new AudioService();
        _workerFactory = workerFactory ?? (() => new WorkerClient());
        _actionWindows = actionWindows ?? _windows;
        _root = root;
        _videoBrowser = videoBrowser ?? new BrowserVideoService(token => ManagedVideoBrowser.LaunchAsync(root, token));
        _power = power ?? new PowerControl();
        Ai = ai ?? new AiService(root);
        Ai.ConnectionChanged += OnConnectionChanged;
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
        _mediaStore = new(Path.Combine(root, "data", "media.json"));
        var media = _mediaStore.Load(); _media = media.Targets;
        if (media.Warning != null) Log("Мультимедиа: " + media.Warning);
        _settingsStore = new SettingsStore(Path.Combine(root, "data", "settings.json"));
        var loaded = _settingsStore.Load();
        _settings = loaded.Settings;
        _music = new MusicPlayback(() => CommandTargets, () => Settings.MusicTargetId, id =>
        {
            if (Settings.MusicTargetId != id) { ApplySettings(Settings with { MusicTargetId = id }); Log("Музыкальное приложение выбрано: " + CommandTargets.FirstOrDefault(t => t.Id == id)?.Name); }
        });
        _skills = skills ?? BuiltInSkills.Create(root, () => CommandTargets, _windows, _music);
        if (loaded.Warning != null) Log(loaded.Warning);
        _audio.Audio += (captureSession, data) =>
        {
            if (captureSession != Interlocked.Read(ref _captureSession) || captureSession == 0) return;
            if (_worker?.Send(new { type = "audio", session = captureSession, pcm = Convert.ToBase64String(data) }) != true)
                Dispatch(() => { if (captureSession == Interlocked.Read(ref _captureSession)) Fail("Очередь звука переполнена. Перезапусти модуль."); });
        };
        _audio.Level += value => Dispatch(() => { if (Phase != AssistantPhase.Speaking) Level = Math.Min(100, value * 200); });
        _audio.PlaybackLevel += value => Dispatch(() => { if (Phase == AssistantPhase.Speaking) Level = Math.Min(100, value * 160); });
        _audio.Failed += (captureSession, error) => Dispatch(() => { if (captureSession == Interlocked.Read(ref _captureSession)) Fail("Микрофон: " + error); });
        _audio.CaptureStateChanged += () => Dispatch(() => { if (!_disposed) MicrophoneActive = _audio.IsCapturing; });
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) =>
        { if (DateTime.UtcNow > _deadline) Fail("Модуль не ответил вовремя. Нажми «Перезапустить модуль»."); };
        _watchdog.Start();
        RefreshDevices();
    }

    private void Changed()
    {
        foreach (var name in new[] { nameof(CanListen), nameof(CanSpeak), nameof(CanEnterText), nameof(CanConfigure), nameof(CanRestart), nameof(CanStop), nameof(CanToggleListening), nameof(ListeningActionLabel) })
            OnPropertyChanged(name);
    }
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
            var previous = Settings.MicrophoneId ?? SelectedMicrophone?.Id ?? _audio.GetDefaultDeviceId();
            Microphones.Clear();
            foreach (var mic in _audio.GetDevices()) Microphones.Add(mic);
            SelectedMicrophone = Microphones.FirstOrDefault(m => m.Id == previous) ?? Microphones.FirstOrDefault();
            if (Microphones.Count == 0) Status = "Микрофон не найден. Подключи его и обнови список.";
        }
        catch (Exception ex) { Status = "Не удалось получить микрофоны: " + ex.Message; }
    }

    public async Task RestartAsync()
    {
        if (_restarting || _disposed) return;
        _restarting = true;
        OnPropertyChanged(nameof(CanRestart));
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
        var worker = _workerFactory();
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
        catch (Exception ex) { if (!_disposed) Fail("Запуск модуля: " + ex.Message); }
        finally { _restarting = false; OnPropertyChanged(nameof(CanRestart)); }
    }

    private long NewSession()
    {
        EndTaskVoiceInterrupt();
        _voiceCaptureSession = -1;
        _aiResponseSession = -1;
        _speechParts.Clear(); _speechSettings = null;
        _operation.Cancel(); _operation.Dispose(); _operation = new();
        return ++_session;
    }

    public void Listen()
    {
        if (!CanListen) return;
        _followUp = false;
        _conversation.Clear(); _powerConfirmation.Clear(); StopVideoChoice(); _voiceInteraction = true; _askingChoice = false;
        var session = NewSession();
        _acceptTranscript = true;
        ResumeWake = WakeMode;
        Busy = true;
        Transcript = "…";
        Phase = WakeMode ? AssistantPhase.WaitingForName : AssistantPhase.Listening;
        Status = WakeMode ? "Скажи «Джарвис», затем команду. Между ними можно сделать паузу." : "Скажи команду. Например: открой браузер.";
        _deadline = WakeMode ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(Settings.CommandWaitSeconds + 14);
        try
        {
            Send(new { type = "start", session, mode = WakeMode ? "wake" : "command", wait_seconds = Settings.CommandWaitSeconds, vocabulary = Vocabulary() });
            StartMicrophone(session);
            Log(WakeMode ? "Ожидание имени включено" : "Микрофон включён");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private void OnConnectionChanged()
    {
        var revision = Ai.ConnectionRevision;
        var session = _session;
        void CancelActiveRequest()
        {
            if (_disposed || revision != Ai.ConnectionRevision || session != _session) return;
            if (_taskInterrupt != null || _aiPending || _browserPending || VideoResults.Count > 0 || _aiResponseSession == _session) Stop();
        }
        if (Application.Current.Dispatcher.CheckAccess()) CancelActiveRequest(); else Dispatch(CancelActiveRequest);
    }

    private void StopTask()
    {
        Stop();
        Reply = Ui.Text("Задача остановлена. Уже выполненные действия не отменены.");
    }

    public void Stop(bool keepChoice = false)
    {
        StopBrowserVideo();
        _aiPending = false;
        _exitAfterSpeech = false;
        _powerAfterSpeech = null;
        if (!keepChoice) { _conversation.Clear(); _powerConfirmation.Clear(); }
        _askingChoice = false; _voiceInteraction = false;
        _acceptTranscript = false;
        ResumeWake = false;
        _followUp = false;
        StopMicrophone();
        _audio.StopPlayback();
        var session = NewSession();
        _worker?.Send(new { type = "cancel", session });
        _deadline = DateTime.MaxValue;
        Busy = false;
        Phase = AssistantPhase.Idle;
        Level = 0;
        Status = "Нажмите кнопку с микрофоном, чтобы начать.";
    }

    public async Task TestVoiceAsync(AssistantSettings? preview = null)
    {
        if (!Ready || Busy) return;
        _conversation.Clear(); _powerConfirmation.Clear(); _askingChoice = false; _voiceInteraction = false;
        var session = NewSession();
        Busy = true;
        _lastVerified = false;
        ResumeWake = false;
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
        _conversation.Clear(); _powerConfirmation.Clear(); _askingChoice = false; _voiceInteraction = false;
        using var input = new WaveFileReader(path);
        if (input.WaveFormat.SampleRate != 16000 || input.WaveFormat.Channels != 1 || input.WaveFormat.BitsPerSample != 16)
            throw new InvalidDataException("Для теста нужен WAV PCM16 mono 16 kHz.");
        if (input.Length > 640000) throw new InvalidDataException("Тестовая запись длиннее 20 секунд.");
        using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var session = NewSession();
        _acceptTranscript = true;
        ResumeWake = false;
        Busy = true;
        _deadline = DateTime.UtcNow.AddSeconds(45);
        Send(new { type = "transcribe", session, pcm = Convert.ToBase64String(buffer.ToArray()), vocabulary = Vocabulary() });
    }

    private bool _lastVerified;
    private async Task HandleAsync(JsonElement message)
    {
        if (_disposed) return;
        string type = message.GetProperty("type").GetString()!;
        string Text() => message.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        if (type == "loading") { Status = Text(); return; }
        // Interrupt capture has its own session and never enters the ordinary command router.
        if (message.TryGetProperty("session", out var capture) && capture.GetInt64() < 0)
        {
            var interrupt = _taskInterrupt;
            if (interrupt == null || capture.GetInt64() != interrupt.CaptureSession
                || interrupt.OwnerSession != _session || interrupt.Revision != Ai.ConnectionRevision
                || _operation.IsCancellationRequested) return;
            if (type == "task_stop" && AiRouting.IsStopTask(Text()))
            { StopTask(); }
            else if (type is "error" or "fatal") RecoverOperation(Text());
            return;
        }
        if (type is "error" or "fatal")
        {
            if (type == "fatal") Fail(Text());
            else if (message.GetProperty("session").GetInt64() == _session) RecoverOperation(Text());
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
        if (!_acceptTranscript && type is "wake_heard" or "activated" or "awaiting_command" or "command_timeout" or "processing" or "empty" or "transcript") return;
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
                _deadline = DateTime.UtcNow.AddSeconds(Settings.CommandWaitSeconds + 14); Log("Услышал имя");
                if (Settings.WakeSound)
                {
                    try { await _audio.PlayAsync(AudioService.ReadyCue(), token, (float)Settings.Volume); }
                    catch (OperationCanceledException) { throw; }
                    catch { Log("Не удалось воспроизвести сигнал готовности."); }
                }
                break;
            case "awaiting_command":
                Phase = AssistantPhase.Listening;
                Status = "Слушаю продолжение…";
                _deadline = DateTime.UtcNow.AddSeconds(Settings.CommandWaitSeconds + 14); break;
            case "command_timeout":
                StopVideoChoice();
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
                if (_followUp)
                {
                    _followUp = false; Busy = false; _deadline = DateTime.MaxValue;
                    Phase = AssistantPhase.Idle; Status = "Следующей команды нет. Жду имя «Джарвис».";
                    await ResumeWakeAsync(session, token, 100); break;
                }
                var wasChoosing = AwaitingChoice;
                _conversation.Clear(); _powerConfirmation.Clear(); StopVideoChoice();
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
                if (SendNextSpeechPart(session)) break;
                if (_exitAfterSpeech) { _exitAfterSpeech = false; ExitRequested?.Invoke(); break; }
                if (_powerAfterSpeech is { } powerIntent)
                {
                    _powerAfterSpeech = null;
                    await ExecutePowerAsync(powerIntent);
                    break;
                }
                Busy = false; _deadline = DateTime.MaxValue;
                Phase = AssistantPhase.Idle; Level = 0; Status = "Готов к следующей команде.";
                if (_askingChoice && AwaitingChoice)
                {
                    _askingChoice = false; _conversation.Renew(DateTimeOffset.UtcNow); _powerConfirmation.Renew(DateTimeOffset.UtcNow);
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
                await ResumeWakeAsync(session, token, 700, allowFollowUp: true);
                break;
        }
    }

    private void StopMicrophone()
    {
        Interlocked.Exchange(ref _captureSession, 0);
        _audio.StopCapture(); MicrophoneActive = false;
    }

    private void StartMicrophone(long session, bool taskInterrupt = false)
    {
        Interlocked.Exchange(ref _captureSession, session);
        try { _audio.Start(SelectedMicrophone!.Id, session); }
        catch { Interlocked.Exchange(ref _captureSession, 0); throw; }
        MicrophoneActive = _audio.IsCapturing;
        if (!taskInterrupt && MicrophoneActive) _voiceCaptureSession = session;
    }

    // Reusable boundary for a future local scenario runner: acquire only for an already
    // authorized voice turn, and release in finally before publishing/speaking its result.
    private TaskVoiceInterrupt? BeginTaskVoiceInterrupt(long session)
    {
        if (!_voiceInteraction || _voiceCaptureSession != session || SelectedMicrophone == null) return null;
        var interrupt = new TaskVoiceInterrupt(--_nextInterruptSession, session, Ai.ConnectionRevision);
        _taskInterrupt = interrupt;
        try
        {
            Send(new { type = "start", session = interrupt.CaptureSession, mode = "task_interrupt", wait_seconds = 8 });
            StartMicrophone(interrupt.CaptureSession, taskInterrupt: true);
        }
        catch { EndTaskVoiceInterrupt(interrupt); throw; }
        return interrupt;
    }

    private void EndTaskVoiceInterrupt(TaskVoiceInterrupt? owner = null)
    {
        if (_taskInterrupt is not { } interrupt || (owner != null && !ReferenceEquals(owner, interrupt))) return;
        _taskInterrupt = null;
        StopMicrophone();
        // Reset the worker before any TTS; the capture session is never reused.
        _worker?.Send(new { type = "cancel", session = _session });
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
    public void SaveMedia(IEnumerable<LaunchTarget> media)
    {
        var updated = media.ToList();
        if (updated.Any(t => !MediaLibrary.IsLibraryEntry(t) && !MusicSelection.IsMediaApp(t)) || updated.Select(t => t.Id).Distinct().Count() != updated.Count)
            throw new InvalidDataException("В каталоге мультимедиа есть неверные или повторяющиеся записи.");
        _mediaStore.Save(updated); _media = updated; _conversation.Clear();
        OnPropertyChanged(nameof(Media)); OnPropertyChanged(nameof(MediaApps));
        Log($"Мультимедиа сохранены · включено {_media.Count(t => t.Enabled)}");
    }
    private string Vocabulary()
    {
        if (_powerConfirmation.HasPending) return "подтверждаю, отмена, нет, стоп";
        if (VideoResults.Count > 0) return "вариант один, вариант два, вариант три, вариант четыре, вариант пять, отмена";
        var enabled = (_conversation.ChoosingWindow ? _runningTargets : CommandTargets).Where(t => t.Enabled).ToArray();
        if (_conversation.HasPending)
            enabled = enabled.Where(t => _conversation.CandidateIds.Contains(t.Id)).ToArray();
        return LaunchVocabulary.Build(enabled, _conversation.HasPending);
    }

    public async Task RunTextAsync(string text)
    {
        if (AiRouting.IsStopTask(text)) { StopTask(); return; }
        if (!CanSpeak || string.IsNullOrWhiteSpace(text)) return;
        _voiceInteraction = false;
        var session = NewSession(); Busy = true; ResumeWake = false; _acceptTranscript = false;
        Transcript = text.Trim(); Log("Команда текстом: " + Transcript);
        try { await ExecuteAsync(Transcript, session, _operation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task ExecuteAsync(string text, long session, CancellationToken token)
    {
        _followUp = false;
        if (AiRouting.IsStopTask(text)) { StopTask(); return; }
        Phase = AssistantPhase.Executing; Status = "Выполняю команду…";
        _deadline = DateTime.UtcNow.AddSeconds(30);
        ArtifactPath = null;
        // Consume confirmation before AI routing; any other answer cancels this one-use request.
        if (_powerConfirmation.HasPending)
        {
            var confirmed = _powerConfirmation.Consume(text, DateTimeOffset.UtcNow);
            _askingChoice = false;
            if (confirmed is { } intent)
            {
                Reply = "Выполняю " + PowerConfirmation.ActionName(intent) + "."; Log(Reply);
                if (!_voiceInteraction) { await ExecutePowerAsync(intent); return; }
                _powerAfterSpeech = intent;
            }
            else { Reply = "Команда питания отменена."; Log(Reply); }
            if (!_voiceInteraction) { Busy = false; Phase = AssistantPhase.Idle; _deadline = DateTime.MaxValue; return; }
            Phase = AssistantPhase.Synthesizing; Speak(session, Reply, Settings); return;
        }
        if (await TryBrowserCommandAsync(text, session, token)) return;
        var powerRequest = CommandRouter.Resolve(text);
        if (PowerConfirmation.IsPower(powerRequest.Intent))
        {
            _conversation.Clear(); _lastVerified = false;
            Reply = _powerConfirmation.Begin(powerRequest.Intent, DateTimeOffset.UtcNow); Log(Reply);
            if (!_voiceInteraction)
            {
                Busy = false; Phase = AssistantPhase.Idle; _deadline = DateTime.MaxValue;
                Status = "Жду подтверждение через меню «Команда текстом».";
                ClarificationReady?.Invoke(Reply); return;
            }
            _askingChoice = true; Phase = AssistantPhase.Synthesizing;
            Speak(session, Reply, Settings); return;
        }
        var explicitTask = AiRouting.ExplicitTask(text);
        if (explicitTask != null && !_conversation.HasPending)
        {
            _conversation.Clear(); _askingChoice = false;
            await AnswerWithAiAsync(explicitTask, session, token, localTask: true);
            return;
        }
        var explicitQuestion = AiRouting.ExplicitQuestion(text);
        if (explicitQuestion != null)
        {
            _conversation.Clear(); _askingChoice = false;
            await AnswerWithAiAsync(explicitQuestion, session, token);
            return;
        }
        var wasChoosing = _conversation.HasPending;
        var preliminary = CommandRouter.Resolve(text, CommandTargets, musicTargetId: Settings.MusicTargetId);
        if (preliminary.Intent == Intent.ExitAssistant)
        {
            _conversation.Clear(); _askingChoice = false; ResumeWake = false; _followUp = false;
            StopMicrophone(); _acceptTranscript = false;
            Reply = "Завершаю работу. До встречи."; Log(Reply);
            if (!_voiceInteraction) { ExitRequested?.Invoke(); return; }
            _exitAfterSpeech = true; Phase = AssistantPhase.Synthesizing; Status = "Завершаю работу JARVIS…";
            _deadline = DateTime.UtcNow.AddSeconds(15); Speak(session, Reply, Settings); return;
        }
        if (preliminary.Intent is Intent.AssistantPresent or Intent.HowAreYou) _conversation.Clear();
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
            await ResumeWakeAsync(session, token, 200, allowFollowUp: true); return;
        }
        Phase = AssistantPhase.Synthesizing; Status = "Готовлю ответ…";
        _deadline = DateTime.UtcNow.AddSeconds(30); Speak(session, Reply, Settings);
    }

    private async Task AnswerWithAiAsync(string question, long session, CancellationToken token, bool localTask = false)
    {
        Status = "Жду ответ нейросети…";
        _deadline = DateTime.UtcNow.AddSeconds(120);
        var revision = Ai.ConnectionRevision;
        SkillResult result;
        _aiPending = true;
        OnPropertyChanged(nameof(CanEnterText));
        _aiResponseSession = session;
        TaskVoiceInterrupt? interrupt = null;
        try
        {
            if (localTask)
            {
                interrupt = Ai.Configuration.AllowLocalActions ? BeginTaskVoiceInterrupt(session) : null;
                bool Current() => !_disposed && session == _session && revision == Ai.ConnectionRevision && !token.IsCancellationRequested && Ai.Configuration.AllowLocalActions;
                var executor = new LocalActionExecutor(() => Targets, _skills, _actionWindows, Current);
                result = await Ai.RunTaskAsync(question, executor, step =>
                {
                    if (!Current()) return;
                    var name = Targets.FirstOrDefault(t => t.Id == step.Action.TargetId)?.Name ?? "";
                    Status = Ui.Format("Шаг {0} из 3 · {1}", step.Step, name + " · " + Ui.Text(step.Message));
                    if (step.Verified != null) Reply = Ui.Text(step.Message);
                }, token);
            }
            else result = await Ai.AskAsync(question, token);
        }
        finally
        {
            if (interrupt != null) EndTaskVoiceInterrupt(interrupt);
            if (session == _session) { _aiPending = false; OnPropertyChanged(nameof(CanEnterText)); }
        }
        if (_disposed || session != _session || revision != Ai.ConnectionRevision || token.IsCancellationRequested) return;
        _lastVerified = result.Verified;
        Reply = localTask ? string.Join("\n", result.Message.Split('\n').Select(Ui.Text)) : result.Message;
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
        if (session != _session) return;
        _speechParts.Clear();
        foreach (var part in SpeechChunks.Split(text))
            if (!string.IsNullOrWhiteSpace(part)) _speechParts.Enqueue(part);
        _speechSettings = settings;
        _replyVolume = (float)settings.Volume;
        if (!SendNextSpeechPart(session)) RecoverOperation("Нет текста для озвучивания.");
    }

    private bool SendNextSpeechPart(long session)
    {
        if (session != _session || _speechSettings is not { } settings) return false;
        if (!_speechParts.TryDequeue(out var text)) { _speechSettings = null; return false; }
        Phase = AssistantPhase.Synthesizing; Status = "Готовлю ответ…";
        _deadline = DateTime.UtcNow.AddSeconds(90);
        Send(new { type = "synthesize", session, text, voice = settings.Voice,
            style = settings.VoiceStyle, amount = settings.RobotAmount, rate = settings.SpeechRate, pitch = settings.VoicePitch });
        return true;
    }

    private void ListenForChoice()
    {
        _followUp = false;
        if (!AwaitingChoice || SelectedMicrophone == null)
        { _conversation.Clear(); _powerConfirmation.Clear(); StopVideoChoice(); Busy = false; Status = "Для голосового выбора нужен микрофон. Повтори команду."; return; }
        var session = NewSession(); _acceptTranscript = true; Busy = true;
        Phase = AssistantPhase.Listening; Status = _powerConfirmation.HasPending
            ? "Скажи «Подтверждаю» или «Отмена». Жду 10 секунд."
            : "Назови часть или вариант. Повторять «Джарвис» не нужно. Жду 10 секунд.";
        _deadline = DateTime.UtcNow.AddSeconds(24);
        try
        {
            Send(new { type = "start", session, mode = "command", wait_seconds = 10, vocabulary = Vocabulary(), choosing = !_powerConfirmation.HasPending });
            StartMicrophone(session); Log("Слушаю уточнение");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private async Task ResumeWakeAsync(long session, CancellationToken token, int delay, bool allowFollowUp = false)
    {
        if (!_resumeWake || _disposed) return;
        await Task.Delay(delay, token);
        if (session != _session) return;
        if (allowFollowUp && _voiceInteraction && Settings.FollowUpListening && SelectedMicrophone != null)
        {
            var next = NewSession(); _followUp = true; _acceptTranscript = true; Busy = true;
            Phase = AssistantPhase.Listening; Status = "Слушаю следующую команду 5 секунд — без имени «Джарвис».";
            _deadline = DateTime.UtcNow.AddSeconds(19);
            Send(new { type = "start", session = next, mode = "command", wait_seconds = 5, vocabulary = Vocabulary() });
            StartMicrophone(next);
        }
        else Listen();
    }

    private async Task ExecutePowerAsync(Intent intent)
    {
        // Stop mic, wake loop and watchdog before suspension; no stale operation resumes after wake.
        Stop();
        var session = _session;
        var token = _operation.Token;
        Busy = true; Phase = AssistantPhase.Executing;
        Status = "Передаю команду питания Windows…";
        try
        {
            await _power.RequestAsync(intent, token);
            if (session != _session) return;
            Reply = "Запрос питания передан Windows.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (session != _session) return; Reply = ex.Message; }
        if (session != _session) return;
        Log(Reply); Busy = false; Phase = AssistantPhase.Idle;
        Status = "Прослушивание остановлено. Нажмите кнопку с микрофоном, чтобы продолжить.";
    }

    public void PreviewMiniSettings(string view, double scale)
    {
        var settings = _settings with
        {
            MiniView = view is "robot" or "panel" ? view : "robot",
            MiniRobotScale = double.IsFinite(scale) ? Math.Clamp(scale, 0.6, 1.6) : 1.0
        };
        if (settings == _settings) return;
        _settings = settings;
        OnPropertyChanged(nameof(Settings));
    }

    public void PersistMiniSettings() => _settingsStore.Save(_settings);

    public void ApplySettings(AssistantSettings settings)
    {
        settings = settings.Normalize();
        _settingsStore.Save(settings);
        _settings = settings;
        Ui.Apply(settings.InterfaceLanguage);
        ThemeManager.Apply(settings.Appearance, settings.ColorStyle);
        if (!Busy) RefreshDevices();
        foreach (var name in new[] { nameof(Settings), nameof(WakeMode), nameof(ModeLabel), nameof(VoiceLabel), nameof(ReduceMotion), nameof(ShowLog) }) OnPropertyChanged(name);
    }

    public void ClearHistory() { _rawHistory.Clear(); History.Clear(); }

    private void RefreshLanguage()
    {
        foreach (var name in new[] { nameof(Status), nameof(Transcript), nameof(Reply), nameof(ModeLabel), nameof(VoiceLabel), nameof(MicrophoneLabel), nameof(ListeningActionLabel), nameof(PhaseTitle), nameof(BrowserStatus) }) OnPropertyChanged(name);
        History.Clear();
        foreach (var entry in _rawHistory) History.Add(Ui.Text(entry));
    }

    private void Send(object message)
    { if (_worker?.Send(message) != true) throw new IOException("Голосовой модуль не принимает команды."); }

    private void Fail(string text)
    {
        if (_exitAfterSpeech) { Stop(); ExitRequested?.Invoke(); return; }
        Stop(); Ready = false; Phase = AssistantPhase.Fault; Status = text; Log("Ошибка: " + text); CycleCompleted?.Invoke(false);
    }

    private void RecoverOperation(string error)
    {
        // An "error" response leaves the worker alive; only "fatal"/transport failures
        // invalidate readiness. Keep the full reply and allow the next command.
        var speech = Phase is AssistantPhase.Synthesizing or AssistantPhase.Speaking;
        Stop(); Phase = AssistantPhase.Fault;
        Status = speech ? "Не удалось озвучить ответ. Текст сохранён. Можно повторить команду."
            : "Не удалось выполнить голосовую операцию. Можно повторить команду.";
        Log("Ошибка: " + error); CycleCompleted?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Ui.Changed -= RefreshLanguage;
        Ai.ConnectionChanged -= OnConnectionChanged;
        // Invalidate the UI/capture owner before canceling service continuations.
        _watchdog.Stop(); Stop(); Ai.Dispose(); _audio.Dispose();
        await _videoBrowser.DisposeAsync();
        if (_worker != null) await _worker.DisposeAsync().ConfigureAwait(false);
        _operation.Dispose();
    }
}
