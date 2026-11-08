using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void InvalidValuesCannotReachAudioEngine()
    {
        var settings = new AssistantSettings { Volume = 4, SpeechRate = double.NaN, RobotAmount = -1,
            Voice = "../../other", VoiceStyle = "shell", CommandWaitSeconds = 100, InterfaceLanguage = "unsupported" }.Normalize();
        Assert.Equal(1, settings.Volume);
        Assert.Equal(1, settings.SpeechRate);
        Assert.Equal(0, settings.RobotAmount);
        Assert.Equal("denis", settings.Voice);
        Assert.Equal("robot", settings.VoiceStyle);
        Assert.Equal(15, settings.CommandWaitSeconds);
        Assert.Equal("ru", settings.InterfaceLanguage);
    }

    [Fact]
    public void SettingsSurviveRestartAndCorruptFileIsNotOverwrittenByLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new AssistantSettings { WakeMode = true, Voice = "dmitri", Volume = 0.4, CommandWaitSeconds = 12,
                ListenOnStartup = true, StartInTray = false, InterfaceLanguage = "en" };
            var store = new SettingsStore(file);
            store.Save(expected);
            Assert.Equal(expected, new SettingsStore(file).Load().Settings);
            File.WriteAllText(file, "broken");
            Assert.NotNull(store.Load().Warning);
            Assert.Equal("broken", File.ReadAllText(file));
        }
        finally { File.Delete(file); Directory.Delete(directory); }
    }

    [Fact]
    public void OlderSettingsDoNotEnableTheMicrophoneOnStartup()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>("{\"WakeMode\":true}")!.Normalize();
        Assert.True(settings.WakeMode);
        Assert.False(settings.ListenOnStartup);
        Assert.True(settings.StartInTray);
    }
}
