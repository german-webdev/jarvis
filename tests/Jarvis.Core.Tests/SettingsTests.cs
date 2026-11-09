using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("robot", "small", 0.6)]
    [InlineData("robot", "medium", 0.93)]
    [InlineData("robot", "large", 1.17)]
    [InlineData("panel", "small", 1.6)]
    [InlineData("panel", "medium", 1.0)]
    [InlineData("panel", "large", 1.3)]
    public void MiniPreferencesSurviveRestartWithoutChangingOtherSettings(string view, string size, double scale)
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-mini-settings-" + Guid.NewGuid());
        var file = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new AssistantSettings { MiniView = view, MiniRobotSize = size, MiniRobotScale = scale,
                Appearance = "light", ColorStyle = "sapphire", InterfaceLanguage = "en",
                WakeMode = false, Volume = 0.4, MusicTargetId = "fixture-player" };
            new SettingsStore(file).Save(expected);
            Assert.Equal(expected, new SettingsStore(file).Load().Settings);
        }
        finally { if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Fact]
    public void LegacySettingsUseTheMediumRobotWithoutChangingExistingPreferences()
    {
        Assert.Null(new AssistantSettings().MiniRobotScale);
        var settings = System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>(
            "{\"Appearance\":\"light\",\"ColorStyle\":\"ruby\",\"WakeMode\":false,\"Volume\":0.4}")!.Normalize();
        Assert.Equal("robot", settings.MiniView);
        Assert.Equal("medium", settings.MiniRobotSize);
        Assert.Equal(1d, settings.MiniRobotScale);
        Assert.Equal("light", settings.Appearance);
        Assert.Equal("ruby", settings.ColorStyle);
        Assert.False(settings.WakeMode);
        Assert.Equal(0.4, settings.Volume);
    }

    [Theory]
    [InlineData("small", 0.8)]
    [InlineData("medium", 1.0)]
    [InlineData("large", 1.3)]
    [InlineData("invalid", 1.0)]
    public void MissingScaleMigratesLegacySizesWithoutWritingOnLoad(string legacySize, double expectedScale)
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-mini-migration-" + Guid.NewGuid());
        var file = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        var json = $$"""{"MiniView":"panel","MiniRobotSize":"{{legacySize}}","Volume":0.4,"WakeMode":false}""";
        try
        {
            File.WriteAllText(file, json);
            var loaded = new SettingsStore(file).Load();
            Assert.Null(loaded.Warning);
            Assert.Equal(expectedScale, loaded.Settings.MiniRobotScale);
            Assert.Equal("panel", loaded.Settings.MiniView);
            Assert.Equal(0.4, loaded.Settings.Volume);
            Assert.False(loaded.Settings.WakeMode);
            Assert.Equal(json, File.ReadAllText(file));
            Assert.Equal(loaded.Settings, loaded.Settings.Normalize());
        }
        finally { File.Delete(file); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(0.1, 0.6)]
    [InlineData(0.6, 0.6)]
    [InlineData(0.93, 0.93)]
    [InlineData(1.17, 1.17)]
    [InlineData(1.6, 1.6)]
    [InlineData(9.0, 1.6)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(double.NegativeInfinity, 1.0)]
    public void ExplicitScaleTakesPrecedenceAndNormalizesToTheSupportedRange(double scale, double expected)
    {
        var settings = new AssistantSettings { MiniView = "panel", MiniRobotSize = "large", MiniRobotScale = scale }.Normalize();
        Assert.Equal(expected, settings.MiniRobotScale);
        Assert.Equal("large", settings.MiniRobotSize);
        Assert.Equal("panel", settings.MiniView);
        Assert.Equal(settings, settings.Normalize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../invalid")]
    public void InvalidMiniPreferencesFallBackIndependently(string? invalid)
    {
        var invalidView = new AssistantSettings { MiniView = invalid!, MiniRobotSize = "large" }.Normalize();
        Assert.Equal("robot", invalidView.MiniView);
        Assert.Equal("large", invalidView.MiniRobotSize);
        Assert.Equal(1.3, invalidView.MiniRobotScale);
        var invalidSize = new AssistantSettings { MiniView = "panel", MiniRobotSize = invalid! }.Normalize();
        Assert.Equal("panel", invalidSize.MiniView);
        Assert.Equal("medium", invalidSize.MiniRobotSize);
        Assert.Equal(1d, invalidSize.MiniRobotScale);
    }

    [Theory]
    [InlineData("graphite")]
    [InlineData("sapphire")]
    [InlineData("emerald")]
    [InlineData("ruby")]
    [InlineData("amethyst")]
    public void ColorStylePersistsIndependentlyOfAppearance(string style)
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-color-test-" + Guid.NewGuid());
        var file = Path.Combine(directory, "settings.json");
        try
        {
            foreach (var appearance in new[] { "system", "light", "dark" })
            {
                var settings = new AssistantSettings { Appearance = appearance, ColorStyle = style };
                new SettingsStore(file).Save(settings);
                Assert.Equal(settings.Normalize(), new SettingsStore(file).Load().Settings);
            }
        }
        finally { if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Fact]
    public void LegacyOrInvalidColorStyleFallsBackWithoutChangingAppearance()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>("{\"Appearance\":\"light\"}")!.Normalize();
        Assert.Equal("graphite", legacy.ColorStyle);
        Assert.Equal("light", legacy.Appearance);
        Assert.Equal("graphite", new AssistantSettings { ColorStyle = "../../invalid" }.Normalize().ColorStyle);
        Assert.Equal("graphite", new AssistantSettings { ColorStyle = null! }.Normalize().ColorStyle);
    }
    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public void AppearanceSurvivesSettingsSerialization(string appearance)
    {
        var original = new AssistantSettings { Appearance = appearance, MusicTargetId = "music-player", InterfaceLanguage = "en" };
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        Assert.Equal(original.Normalize(), System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>(json)!.Normalize());
    }

    [Fact]
    public void OlderAndUnknownAppearanceSettingsKeepTheDefaultDarkStyle()
    {
        Assert.Equal("dark", System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>("{}")!.Normalize().Appearance);
        Assert.Equal("dark", new AssistantSettings { Appearance = "unknown" }.Normalize().Appearance);
    }

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
    public void LegacySettingsKeepNeutralVoicePitch()
    {
        Assert.Equal(0d, new AssistantSettings().VoicePitch);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>("{\"SpeechRate\":1.1,\"Volume\":0.4}")!.Normalize();
        Assert.Equal(0d, legacy.VoicePitch);
        Assert.Equal(1.1, legacy.SpeechRate);
        Assert.Equal(0.4, legacy.Volume);
    }

    [Fact]
    public void NonfiniteVoicePitchFallsBackToNeutralWithoutChangingSpeechRate()
    {
        foreach (var pitch in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            var normalized = new AssistantSettings { VoicePitch = pitch, SpeechRate = 1.1 }.Normalize();
            Assert.Equal(0d, normalized.VoicePitch);
            Assert.Equal(1.1, normalized.SpeechRate);
        }
    }

    [Fact]
    public void VoicePitchClampsAndPersistsIntermediateValues()
    {
        Assert.Equal(-6d, new AssistantSettings { VoicePitch = -9 }.Normalize().VoicePitch);
        Assert.Equal(6d, new AssistantSettings { VoicePitch = 9 }.Normalize().VoicePitch);
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-voice-pitch-" + Guid.NewGuid());
        var file = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new AssistantSettings { VoicePitch = 2.5, SpeechRate = 1.1, Volume = 0.4 }.Normalize();
            new SettingsStore(file).Save(expected);
            Assert.Equal(expected, new SettingsStore(file).Load().Settings);
        }
        finally { if (File.Exists(file)) File.Delete(file); if (Directory.Exists(directory)) Directory.Delete(directory); }
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
            Assert.Equal(expected.Normalize(), new SettingsStore(file).Load().Settings);
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
        Assert.True(new AssistantSettings().WakeMode);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>("{}")!.Normalize().WakeMode);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AssistantSettings>("{\"WakeMode\":false}")!.Normalize().WakeMode);
        Assert.False(settings.ListenOnStartup);
        Assert.True(settings.StartInTray);
    }
}
