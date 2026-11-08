using System.Text.Json;

namespace Jarvis.Core;

public sealed record AssistantSettings
{
    public int Version { get; init; } = 1;
    public string? MicrophoneId { get; init; }
    public bool WakeMode { get; init; }
    public string Voice { get; init; } = "denis";
    public string VoiceStyle { get; init; } = "robot";
    public double RobotAmount { get; init; } = 0.45;
    public double SpeechRate { get; init; } = 1.0;
    public double Volume { get; init; } = 0.8;
    public int CommandWaitSeconds { get; init; } = 8;
    public bool ShowLog { get; init; } = true;
    public bool ReduceMotion { get; init; }

    public AssistantSettings Normalize() => this with
    {
        Version = 1,
        Voice = Voice is "denis" or "dmitri" ? Voice : "denis",
        VoiceStyle = VoiceStyle is "natural" or "robot" or "radio" ? VoiceStyle : "robot",
        RobotAmount = Bound(RobotAmount, 0, 1, 0.45),
        SpeechRate = Bound(SpeechRate, 0.85, 1.2, 1),
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
