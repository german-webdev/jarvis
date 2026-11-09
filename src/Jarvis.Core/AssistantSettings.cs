using System.Text.Json;

namespace Jarvis.Core;

public sealed record AssistantSettings
{
    public int Version { get; init; } = 1;
    public string? MicrophoneId { get; init; }
    public string? MusicTargetId { get; init; }
    public bool WakeMode { get; init; } = true;
    public bool WakeSound { get; init; } = true;
    public bool FollowUpListening { get; init; } = true;
    public string Voice { get; init; } = "denis";
    public string VoiceStyle { get; init; } = "robot";
    public double RobotAmount { get; init; } = 0.45;
    public double SpeechRate { get; init; } = 1.0;
    public double VoicePitch { get; init; }
    public double Volume { get; init; } = 0.8;
    public int CommandWaitSeconds { get; init; } = 8;
    public bool ShowLog { get; init; } = true;
    public bool ReduceMotion { get; init; }
    public bool ListenOnStartup { get; init; }
    public bool StartInTray { get; init; } = true;
    public string InterfaceLanguage { get; init; } = "ru";
    public string Appearance { get; init; } = "dark";
    public string ColorStyle { get; init; } = "graphite";
    public string MiniView { get; init; } = "robot";
    public string MiniRobotSize { get; init; } = "medium";
    public double? MiniRobotScale { get; init; }

    public AssistantSettings Normalize() => this with
    {
        Version = 1,
        InterfaceLanguage = InterfaceLanguage is "ru" or "en" ? InterfaceLanguage : "ru",
        Appearance = Appearance is "system" or "light" or "dark" ? Appearance : "dark",
        ColorStyle = ColorStyle is "graphite" or "sapphire" or "emerald" or "ruby" or "amethyst" ? ColorStyle : "graphite",
        MiniView = MiniView is "robot" or "panel" ? MiniView : "robot",
        MiniRobotSize = MiniRobotSize is "small" or "medium" or "large" ? MiniRobotSize : "medium",
        MiniRobotScale = Bound(MiniRobotScale ?? (MiniRobotSize switch { "small" => 0.8, "large" => 1.3, _ => 1.0 }), 0.6, 1.6, 1.0),
        MusicTargetId = string.IsNullOrWhiteSpace(MusicTargetId) || MusicTargetId.Length > 512 ? null : MusicTargetId,
        Voice = Voice is "denis" or "dmitri" ? Voice : "denis",
        VoiceStyle = VoiceStyle is "natural" or "robot" or "radio" ? VoiceStyle : "robot",
        RobotAmount = Bound(RobotAmount, 0, 1, 0.45),
        SpeechRate = Bound(SpeechRate, 0.85, 1.2, 1),
        VoicePitch = Bound(VoicePitch, -6, 6, 0),
        Volume = Bound(Volume, 0, 1, 0.8),
        CommandWaitSeconds = Math.Clamp(CommandWaitSeconds, 4, 15)
    };

    private static double Bound(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed class SettingsStore(string path)
{
    public (AssistantSettings Settings, string? Warning) Load()
    {
        if (!File.Exists(path)) return (new(), null);
        try
        {
            var settings = JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(path));
            return (settings?.Normalize() ?? new(), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new(), "Настройки не прочитаны. Используются значения по умолчанию.");
        }
    }

    public void Save(AssistantSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

public enum AssistantPhase { Loading, Idle, WaitingForName, Listening, Recognizing, Executing, Synthesizing, Speaking, Fault }
