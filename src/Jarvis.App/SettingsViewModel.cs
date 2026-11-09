using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jarvis.App.Services;
using Jarvis.Core;

namespace Jarvis.App;

public record Choice(string Id, string Label);

public sealed class SettingsViewModel : ObservableObject
{
    public MainViewModel Runtime { get; }
    public ObservableCollection<Microphone> Microphones => Runtime.Microphones;
    public IReadOnlyList<Choice> Voices { get; } = [new("denis", Ui.Text("Денис · спокойный мужской")), new("dmitri", Ui.Text("Дмитрий · исходный голос"))];
    public IReadOnlyList<Choice> Styles { get; } = [new("robot", Ui.Text("Синтез · роботизированный")), new("natural", Ui.Text("Естественный · без эффекта")), new("radio", Ui.Text("Радиоканал"))];
    public string? MicrophoneId { get; set; }
    public string Voice { get; set; }
    private string _voiceStyle;
    public string VoiceStyle { get => _voiceStyle; set { SetProperty(ref _voiceStyle, value); OnPropertyChanged(nameof(EffectEnabled)); } }
    public bool EffectEnabled => VoiceStyle != "natural";
    public double RobotAmount { get; set; }
    public double SpeechRate { get; set; }
    public double VoicePitch { get; set; }
    public double Volume { get; set; }
    public bool WakeMode { get; set; }
    public bool WakeSound { get; set; }
    public bool FollowUpListening { get; set; }
    public double CommandWaitSeconds { get; set; }
    public bool ShowLog { get; set; }
    public bool ReduceMotion { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ListenOnStartup { get; set; }
    public bool StartInTray { get; set; }
    public string InterfaceLanguage { get; set; }
    public string Appearance { get; set; }
    public string ColorStyle { get; set; }
    public IReadOnlyList<Choice> ColorStyles { get; } = [new("graphite", Ui.Text("Графит")), new("sapphire", Ui.Text("Сапфир")),
        new("emerald", Ui.Text("Изумруд")), new("ruby", Ui.Text("Рубин")), new("amethyst", Ui.Text("Аметист"))];
    public IReadOnlyList<Choice> Appearances { get; } = [new("system", Ui.Text("Системное")), new("light", Ui.Text("Светлое")), new("dark", Ui.Text("Тёмное"))];
    public IReadOnlyList<Choice> Languages { get; } = [new("ru", "Русский"), new("en", "English")];

    public SettingsViewModel(MainViewModel runtime)
    {
        Runtime = runtime;
        StartWithWindows = StartupRegistration.ReadCommand() != null;
        var s = runtime.Settings;
        InterfaceLanguage = s.InterfaceLanguage;
        Appearance = s.Appearance;
        ColorStyle = s.ColorStyle;
        MicrophoneId = runtime.SelectedMicrophone?.Id ?? s.MicrophoneId;
        Voice = s.Voice; _voiceStyle = s.VoiceStyle; RobotAmount = s.RobotAmount;
        SpeechRate = s.SpeechRate; VoicePitch = s.VoicePitch; Volume = s.Volume; WakeMode = s.WakeMode;
        WakeSound = s.WakeSound; FollowUpListening = s.FollowUpListening;
        CommandWaitSeconds = s.CommandWaitSeconds; ShowLog = s.ShowLog; ReduceMotion = s.ReduceMotion;
        ListenOnStartup = s.ListenOnStartup; StartInTray = s.StartInTray;
    }

    public AssistantSettings Capture() => new AssistantSettings
    {
        MicrophoneId = MicrophoneId, Voice = Voice, VoiceStyle = VoiceStyle,
        MusicTargetId = Runtime.Settings.MusicTargetId,
        RobotAmount = Math.Round(RobotAmount, 2), SpeechRate = Math.Round(SpeechRate, 2), VoicePitch = VoicePitch,
        Volume = Math.Round(Volume, 2), WakeMode = WakeMode, CommandWaitSeconds = (int)CommandWaitSeconds,
        WakeSound = WakeSound, FollowUpListening = FollowUpListening,
        ShowLog = ShowLog, ReduceMotion = ReduceMotion,
        ListenOnStartup = ListenOnStartup, StartInTray = StartInTray, InterfaceLanguage = InterfaceLanguage,
        Appearance = Appearance, ColorStyle = ColorStyle,
        MiniView = Runtime.Settings.MiniView, MiniRobotSize = Runtime.Settings.MiniRobotSize,
        MiniRobotScale = Runtime.Settings.MiniRobotScale
    }.Normalize();
}
