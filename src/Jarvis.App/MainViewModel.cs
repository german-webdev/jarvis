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
    private readonly AudioService _audio = new();
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
    private string _reply = "Открой браузер · Запусти браузер";
    private bool _ready;
    private bool _busy;
    private bool _wakeMode;
    private double _level;
    private Microphone? _selected;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Transcript { get => _transcript; private set => SetProperty(ref _transcript, value); }
    public string Reply { get => _reply; private set => SetProperty(ref _reply, value); }
    public bool Ready { get => _ready; private set { SetProperty(ref _ready, value); Changed(); } }
    public bool Busy { get => _busy; private set { SetProperty(ref _busy, value); Changed(); } }
    public bool WakeMode { get => _wakeMode; set => SetProperty(ref _wakeMode, value); }
    public double Level { get => _level; private set => SetProperty(ref _level, value); }
    public Microphone? SelectedMicrophone { get => _selected; set { SetProperty(ref _selected, value); Changed(); } }
    public bool CanListen => Ready && !Busy && SelectedMicrophone != null;
    public bool CanSpeak => Ready && !Busy;
    public bool CanConfigure => !Busy;
    public ObservableCollection<Microphone> Microphones { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public event Action? Initialized;
    public event Action<bool>? CycleCompleted;

    public MainViewModel(string root)
    {
        _root = root;
        _audio.Audio += data =>
        {
            if (_worker?.Send(new { type = "audio", session = _session, pcm = Convert.ToBase64String(data) }) != true)
                Dispatch(() => Fail("Очередь звука переполнена. Перезапусти модуль."));
        };
        _audio.Level += value => Dispatch(() => Level = Math.Min(100, value * 200));
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
        History.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (History.Count > 30) History.RemoveAt(History.Count - 1);
    }

    public void RefreshDevices()
    {
        if (Busy) return;
        try
        {
            var previous = SelectedMicrophone?.Id ?? AudioService.DefaultDeviceId();
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
        Status = "Загружаю модели — при первом запуске это займёт немного времени…";
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
        var session = NewSession();
        _acceptTranscript = true;
        _resumeWake = WakeMode;
        Busy = true;
        Transcript = "…";
        Status = WakeMode ? "Жду «Джарвис» — затем скажи команду" : "Слушаю. Скажи: «Открой браузер»";
        _deadline = WakeMode ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(18);
        try
        {
            Send(new { type = "start", session, mode = WakeMode ? "wake" : "command" });
            _audio.Start(SelectedMicrophone!.Id);
            Log(WakeMode ? "Ожидание имени включено" : "Микрофон включён");
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    public void Stop()
    {
        _acceptTranscript = false;
        _resumeWake = false;
        _audio.StopCapture();
        _audio.StopPlayback();
        var session = NewSession();
        _worker?.Send(new { type = "cancel", session });
        _deadline = DateTime.MaxValue;
        Busy = false;
        Status = "Микрофон выключен";
    }

    public async Task TestVoiceAsync()
    {
        if (!Ready || Busy) return;
        var session = NewSession();
        Busy = true;
        _lastVerified = false;
        _resumeWake = false;
        Reply = "Я Джарвис. Готов открыть браузер по твоей команде.";
        Status = "Готовлю голос…";
        _deadline = DateTime.UtcNow.AddSeconds(30);
        Send(new { type = "synthesize", session, text = Reply });
        await Task.CompletedTask;
    }

    public void ImportTestAudio(string path)
    {
        if (!Ready || Busy) return;
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
        Send(new { type = "transcribe", session, pcm = Convert.ToBase64String(buffer.ToArray()) });
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
            Status = "Готов. Выбери микрофон и нажми «Слушать»";
            Log("Модели готовы · локально · CPU"); Initialized?.Invoke(); return;
        }
        var session = message.GetProperty("session").GetInt64();
        if (session != _session) return;
        var token = _operation.Token;
        switch (type)
        {
            case "activated":
                Status = "Имя услышано. Слушаю команду…";
                _deadline = DateTime.UtcNow.AddSeconds(18); Log("Услышал имя"); break;
            case "processing":
                _audio.StopCapture(); Status = "Распознаю фразу…";
                _deadline = DateTime.UtcNow.AddSeconds(45); break;
            case "empty":
                _audio.StopCapture(); _acceptTranscript = false;
                Status = "Речь не обнаружена. Попробуй ещё раз.";
                Busy = false; _deadline = DateTime.MaxValue; CycleCompleted?.Invoke(false); break;
            case "transcript":
                if (!_acceptTranscript) return;
                _acceptTranscript = false;
                _audio.StopCapture(); Transcript = Text();
                var elapsed = message.GetProperty("elapsed_ms").GetInt32();
                Log($"Распознавание: {elapsed} мс · {Transcript}");
                var intent = CommandRouter.Parse(Transcript);
                _lastVerified = false;
                if (intent == Intent.OpenBrowser)
                {
                    Status = "Открываю браузер…";
                    var result = await BrowserLauncher.OpenAsync(token);
                    if (session != _session) return;
                    _lastVerified = result.Verified; Reply = result.Message;
                }
                else Reply = intent == Intent.Cancel ? "Отменено." : "Пока я умею открывать браузер. Скажи: открой браузер.";
                Log(Reply); Status = "Готовлю ответ…";
                _deadline = DateTime.UtcNow.AddSeconds(30);
                Send(new { type = "synthesize", session, text = Reply }); break;
            case "speech":
                Status = "Отвечаю… Микрофон выключен";
                await _audio.PlayAsync(Convert.FromBase64String(message.GetProperty("wav").GetString()!), token);
                if (session != _session) return;
                Busy = false; _deadline = DateTime.MaxValue;
                Status = "Готов. Микрофон выключен";
                CycleCompleted?.Invoke(_lastVerified);
                if (_resumeWake && !_disposed)
                {
                    await Task.Delay(700, token);
                    if (session == _session) Listen();
                }
                break;
        }
    }

    private void Send(object message)
    { if (_worker?.Send(message) != true) throw new IOException("Голосовой модуль не принимает команды."); }

    private void Fail(string text)
    {
        Stop(); Ready = false; Status = text; Log("Ошибка: " + text); CycleCompleted?.Invoke(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _watchdog.Stop(); Stop(); _audio.Dispose();
        if (_worker != null) await _worker.DisposeAsync();
        _operation.Dispose();
    }
}
