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
    public IReadOnlyList<Choice> Voices { get; } = [new("denis", "Денис · спокойный мужской"), new("dmitri", "Дмитрий · исходный голос")];
    public IReadOnlyList<Choice> Styles { get; } = [new("robot", "Синтез · роботизированный"), new("natural", "Естественный · без эффекта"), new("radio", "Радиоканал")];
    public string? MicrophoneId { get; set; }
    public string Voice { get; set; }
    private string _voiceStyle;
    public string VoiceStyle { get => _voiceStyle; set { SetProperty(ref _voiceStyle, value); OnPropertyChanged(nameof(EffectEnabled)); } }
    public bool EffectEnabled => VoiceStyle != "natural";
    public double RobotAmount { get; set; }
    public double SpeechRate { get; set; }
    public double Volume { get; set; }
    public bool WakeMode { get; set; }
    public double CommandWaitSeconds { get; set; }
    public bool ShowLog { get; set; }
    public bool ReduceMotion { get; set; }

    public SettingsViewModel(MainViewModel runtime)
    {
        Runtime = runtime;
        var s = runtime.Settings;
        MicrophoneId = runtime.SelectedMicrophone?.Id ?? s.MicrophoneId;
        Voice = s.Voice; _voiceStyle = s.VoiceStyle; RobotAmount = s.RobotAmount;
        SpeechRate = s.SpeechRate; Volume = s.Volume; WakeMode = s.WakeMode;
        CommandWaitSeconds = s.CommandWaitSeconds; ShowLog = s.ShowLog; ReduceMotion = s.ReduceMotion;
    }

    public AssistantSettings Capture() => new AssistantSettings
    {
        MicrophoneId = MicrophoneId, Voice = Voice, VoiceStyle = VoiceStyle,
        RobotAmount = Math.Round(RobotAmount, 2), SpeechRate = Math.Round(SpeechRate, 2),
        Volume = Math.Round(Volume, 2), WakeMode = WakeMode, CommandWaitSeconds = (int)CommandWaitSeconds,
        ShowLog = ShowLog, ReduceMotion = ReduceMotion
    }.Normalize();
}
