using Jarvis.Core;
using Xunit;
using static Jarvis.Core.Tests.NameVariantsTests;

namespace Jarvis.Core.Tests;

public class DesktopCommandsTests
{
    private readonly LaunchTarget[] _apps = [Game("Subnautica", "sub"), Game("Forza Horizon 5", "five"), Game("Forza Horizon 6", "six"), Game("Telegram", "tg")];
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    [Theory]
    [InlineData("Открой папку с Сабнавтикой", "sub")]
    [InlineData("Открой папку с игрой Subnautica", "sub")]
    [InlineData("Открой каталог Сабнавтики", "sub")]
    [InlineData("Открой папку программы Телеграм", "tg")]
    [InlineData("Открой папку Телеграма", "tg")]
    [InlineData("Открой папку с Телеграмом", "tg")]
    public void FoldersUseInstalledCatalogAndInflections(string phrase, string id)
    {
        var request = CommandRouter.Resolve(phrase, _apps);
        Assert.Equal(Intent.OpenTargetFolder, request.Intent); Assert.Equal(id, request.TargetId);
    }
    [Theory]
    [InlineData("пауза", Intent.MediaPause)]
    [InlineData("поставь на паузу", Intent.MediaPause)]
    [InlineData("продолжи", Intent.MediaPlay)]
    [InlineData("следующий трек", Intent.MediaNext)]
    [InlineData("предыдущий трек", Intent.MediaPrevious)]
    [InlineData("сверни окно", Intent.MinimizeWindow)]
    [InlineData("закрой текущее приложение", Intent.CloseWindow)]
    public void DesktopPhrases(string phrase, Intent intent) => Assert.Equal(intent, CommandRouter.Resolve(phrase).Intent);
    [Theory]
    [InlineData("Закрой телеграм", Intent.CloseTarget)]
    [InlineData("Выключи телеграм", Intent.CloseTarget)]
    [InlineData("Переключись на телеграм", Intent.FocusTarget)]
    public void OnlyRunningTargetsCanBeClosedOrFocused(string phrase, Intent intent)
    {
        Assert.Equal(Intent.Unknown, CommandRouter.Resolve(phrase, _apps).Intent);
        var window = _apps[3] with { Id = "window:tg" };
        var request = CommandRouter.Resolve(phrase, _apps, [window]);
        Assert.Equal(intent, request.Intent); Assert.Equal(window.Id, request.TargetId);
    }
    [Theory]
    [InlineData("Закрой форзу", Intent.CloseTarget)]
    [InlineData("Переключись на форзу", Intent.FocusTarget)]
    [InlineData("Открой папку Форзы", Intent.OpenTargetFolder)]
    public void ClarificationPreservesRequestedAction(string phrase, Intent intent)
    {
        var dialog = new LaunchConversation();
        var first = dialog.Resolve(phrase, _apps, _now, _apps);
        Assert.True(first.NeedsAnswer); Assert.Null(first.Command);
        var selected = dialog.Resolve("шестую", _apps, _now.AddSeconds(1), _apps);
        Assert.Equal(intent, selected.Command?.Intent); Assert.Equal("six", selected.Command?.TargetId);
    }
    [Fact] public void ClosingNeverSwitchesToAnotherWindowAfterOriginalDisappears()
    {
        var dialog = new LaunchConversation(); dialog.Resolve("Закрой форзу", _apps, _now, _apps);
        var result = dialog.Resolve("вариант два", _apps, _now, [_apps[2]]);
        Assert.Equal(Intent.Unknown, result.Command?.Intent); Assert.False(dialog.HasPending);
    }
    [Theory]
    [InlineData("не закрывай телеграм")]
    [InlineData("закрой телеграм и браузер")]
    [InlineData("закрой телеграм?")]
    [InlineData("открой папку C:\\Windows")]
    [InlineData("принудительно закрой телеграм")]
    public void NoBroadOrComposedActions(string phrase) => Assert.Equal(Intent.Unknown, CommandRouter.Resolve(phrase, _apps, _apps).Intent);
    [Fact] public void UnknownNewCommandDiscardsOldCloseQuestion()
    {
        var dialog = new LaunchConversation(); dialog.Resolve("Закрой форзу", _apps, _now, _apps);
        dialog.Resolve("Запусти неизвестную программу", _apps, _now, _apps);
        Assert.False(dialog.HasPending);
        Assert.Equal(Intent.Unknown, dialog.Resolve("шестую", _apps, _now, _apps).Command?.Intent);
    }
}
