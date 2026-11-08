using Xunit;
using Jarvis.Core;

namespace Jarvis.Core.Tests;

public class MusicSelectionTests
{
    private static LaunchTarget App(string name, string id) => new(id, name, "app", id, "Windows", [], true);
    private readonly LaunchTarget[] _apps = [App("Яндекс Музыка", "yandex"), App("Spotify", "spotify")];
    [Theory]
    [InlineData("дальше", Intent.MediaNext)] [InlineData("следующий", Intent.MediaNext)]
    [InlineData("назад", Intent.MediaPrevious)] [InlineData("предыдущий", Intent.MediaPrevious)]
    [InlineData("выключи музыку", Intent.MediaPause)] [InlineData("останови музыку", Intent.MediaPause)]
    [InlineData("отключи музыку", Intent.MediaPause)]
    [InlineData("стоп", Intent.MediaPause)] [InlineData("останови", Intent.MediaPause)]
    public void ShortMediaCommands(string phrase, Intent expected) => Assert.Equal(expected, CommandRouter.Resolve(phrase).Intent);
    [Theory][InlineData("стоп")][InlineData("останови")][InlineData("Джарвис, стоп, пожалуйста")]
    public void ShortStopCancelsPendingChoiceButOtherwisePausesMedia(string phrase)
    {
        var dialog = new LaunchConversation(); var now = DateTimeOffset.UtcNow;
        Assert.Equal(Intent.MediaPause, dialog.Resolve(phrase, _apps, now).Command?.Intent);
        Assert.True(dialog.Resolve("Включи музыку", _apps, now).NeedsAnswer);
        Assert.Equal(Intent.Cancel, dialog.Resolve(phrase, _apps, now).Command?.Intent);
        Assert.False(dialog.HasPending);
        Assert.Equal(Intent.MediaPause, dialog.Resolve(phrase, _apps, now).Command?.Intent);
    }
    [Fact] public void TurnMusicOffPausesInsteadOfClosingMatchingApplication()
    {
        var music = App("Музыка", "music");
        var command = CommandRouter.Resolve("Джарвис, выключи музыку, пожалуйста", [music], [music]);
        Assert.Equal(Intent.MediaPause, command.Intent);
        Assert.Null(command.TargetId);
    }
    [Fact] public void PausedMusicWinsOverBrowserEvenWhenBrowserIsPlaying()
    {
        var choice = MusicSelection.Choose([new("Chrome", null, true), new("ru.yandex.desktop.music", "yandex", false)], _apps, null);
        Assert.Equal("ru.yandex.desktop.music", choice.SourceId);
    }
    [Fact] public void PreferredPlayerDisappearingNeverStartsBrowser()
    {
        Assert.Null(MusicSelection.Choose([new("Chrome", null, false)], _apps, "yandex").SourceId);
        Assert.Null(MusicSelection.Choose([new("a", "yandex", false), new("b", "yandex", true)], _apps, "yandex").SourceId);
    }
    [Fact] public void MultipleMusicAppsOrVideosNeedExplicitChoice()
    {
        Assert.Null(MusicSelection.Choose([new("a", "yandex", false), new("b", "spotify", false)], _apps, null).SourceId);
        Assert.Null(MusicSelection.Choose([new("Chrome", null, false), new("Edge", null, false)], _apps, null).SourceId);
        Assert.Equal("Chrome", MusicSelection.Choose([new("Chrome", null, false)], _apps, null).SourceId);
    }
    [Fact] public void StartMusicSelectsAndKeepsPlayActionAfterClarification()
    {
        var now = DateTimeOffset.UtcNow; var dialog = new LaunchConversation();
        Assert.True(dialog.Resolve("Включи музыку", _apps, now).NeedsAnswer);
        var selected = dialog.Resolve("Яндекс Музыку", _apps, now).Command;
        Assert.Equal(Intent.StartMusic, selected?.Intent); Assert.Equal("yandex", selected?.TargetId);
        Assert.Equal("spotify", CommandRouter.Resolve("Включи музыку", _apps, musicTargetId: "spotify").TargetId);
        Assert.Equal("yandex", CommandRouter.Resolve("Включи музыку", [_apps[0]]).TargetId);
    }
    [Theory]
    [InlineData("Яндекс Музыка", "Яндекс Музыку")]
    [InlineData("Яндекс Музыка", "Яндекс Музыки")]
    [InlineData("Яндекс Музыка", "Яндекс Музыкой")]
    [InlineData("Альфа лаборатория", "Альфа лабораторию")]
    [InlineData("Kaspersky Password Manager", "касперский пасворд менеджер")]
    [InlineData("Kaspersky Password Manager", "касперский пассворд менеджер")]
    public void NamesWorkWithoutPerApplicationCustomAliases(string name, string spoken)
    {
        var app = App(name, "one");
        Assert.Equal("one", CommandRouter.Resolve("Запусти " + spoken, [app]).TargetId);
        Assert.Equal("one", CommandRouter.Resolve("Закрой " + spoken, running: [app]).TargetId);
    }
    [Fact] public void CustomNamesSurviveRunningWindowIdentityMapping()
    {
        var installed = App("Kaspersky Password Manager", "one") with { Aliases = ["Касперский Password Manager", "мой менеджер"] };
        var running = installed with { Id = "window:123", Kind = "window", Target = "123" };
        Assert.Equal("window:123", CommandRouter.Resolve("Закрой Касперский Password Manager", [installed], [running]).TargetId);
    }
    [Fact] public void ExistingCommaSeparatedAliasesWorkWithoutResaving()
    {
        var app = App("Some Tool", "one") with { Aliases = ["мой инструмент, моя утилита; тестовая программа\nдругая фраза"] };
        foreach (var name in new[] { "мой инструмент", "моя утилита", "тестовая программа", "другая фраза" })
            Assert.Equal("one", CommandRouter.Resolve("Запусти " + name, [app]).TargetId);
    }
    [Theory]
    [InlineData("Word", "word")][InlineData("ward", "word")][InlineData("wardo", "word")]
    [InlineData("ворд", "word")][InlineData("WARDOGS", "game")][InlineData("вардогс", "game")]
    public void UniqueWordAndWardogsNamesLaunchWithoutClarification(string name, string expected)
    {
        var apps = new[] { App("Word", "word"), App("WARDOGS", "game") };
        var dialog = new LaunchConversation(); var now = DateTimeOffset.UtcNow;
        var result = dialog.Resolve("Запусти " + name, apps, now);
        Assert.False(result.NeedsAnswer);
        Assert.Equal(Intent.LaunchTarget, result.Command?.Intent);
        Assert.Equal(expected, result.Command?.TargetId);
    }
    [Fact]
    public void SharedCustomAliasStillRequiresClarification()
    {
        var apps = new[] { App("Word", "word"), App("WARDOGS", "game") with { Aliases = ["ворд"] } };
        var dialog = new LaunchConversation(); var now = DateTimeOffset.UtcNow;
        Assert.True(dialog.Resolve("Запусти ворд", apps, now).NeedsAnswer);
        Assert.Equal("word", dialog.Resolve("вариант один", apps, now).Command?.TargetId);
    }
    [Theory][InlineData("эксель")][InlineData("экссель")]
    public void ExcelPronunciations(string name) => Assert.Equal("excel", CommandRouter.Resolve("Запусти " + name, [App("Excel", "excel")]).TargetId);
}
