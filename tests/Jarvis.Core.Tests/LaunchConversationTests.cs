using Jarvis.Core;
using Xunit;
using static Jarvis.Core.Tests.NameVariantsTests;

namespace Jarvis.Core.Tests;

public class LaunchConversationTests
{
    private readonly LaunchTarget[] _games = [Game("Forza Horizon 5", "5"), Game("Forza Horizon 6", "6")];
    private readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-11-08T00:00:00Z");
    [Theory]
    [InlineData("шестую", "6")]
    [InlineData("шесть", "6")]
    [InlineData("6", "6")]
    [InlineData("пятую", "5")]
    [InlineData("Джарвис, шестую, пожалуйста", "6")]
    [InlineData("форзу шесть", "6")]
    [InlineData("Forza Horizon 6", "6")]
    [InlineData("вариант два", "6")]
    [InlineData("первый вариант", "5")]
    public void AmbiguityAsksBeforeLaunching(string answer, string id)
    {
        var dialog = new LaunchConversation();
        var first = dialog.Resolve("Запусти форзу", _games, _now);
        Assert.True(first.NeedsAnswer); Assert.Null(first.Command); Assert.True(dialog.HasPending);
        Assert.Contains("Forza Horizon 5", first.Question); Assert.Contains("Forza Horizon 6", first.Question);
        var next = dialog.Resolve(answer, _games, _now.AddSeconds(5));
        Assert.Equal(id, next.Command?.TargetId); Assert.False(dialog.HasPending);
    }
    [Fact] public void SingleGameNeedsNoQuestion()
    {
        var turn = new LaunchConversation().Resolve("Запусти форзу", [_games[1]], _now);
        Assert.False(turn.NeedsAnswer); Assert.Equal("6", turn.Command?.TargetId);
    }
    [Theory]
    [InlineData("не шестую")]
    [InlineData("шестую и выключи компьютер")]
    [InlineData("да")]
    [InlineData("седьмую")]
    [InlineData("-6")]
    [InlineData("первую")]
    public void UnclearAnswerNeverSelectsFirstGame(string answer)
    {
        var dialog = new LaunchConversation(); dialog.Resolve("Запусти форзу", _games, _now);
        Assert.True(dialog.Resolve(answer, _games, _now.AddSeconds(1)).NeedsAnswer);
        var last = dialog.Resolve(answer, _games, _now.AddSeconds(2));
        Assert.Equal(Intent.Unknown, last.Command?.Intent); Assert.False(dialog.HasPending);
    }
    [Fact] public void CancelStopExpiryAndNewCommandsDiscardPendingChoice()
    {
        var dialog = new LaunchConversation();
        dialog.Resolve("Запусти форзу", _games, _now);
        Assert.Equal(Intent.Cancel, dialog.Resolve("отмена", _games, _now).Command?.Intent);
        Assert.Equal(Intent.Unknown, dialog.Resolve("шестую", _games, _now).Command?.Intent);
        dialog.Resolve("Запусти форзу", _games, _now); dialog.Clear();
        Assert.Equal(Intent.Unknown, dialog.Resolve("шестую", _games, _now).Command?.Intent);
        dialog.Resolve("Запусти форзу", _games, _now);
        Assert.Equal(Intent.Unknown, dialog.Resolve("шестую", _games, _now.AddSeconds(31)).Command?.Intent);
        Assert.False(dialog.HasPending);
        dialog.Resolve("Запусти форзу", _games, _now);
        Assert.Equal(Intent.OpenBrowser, dialog.Resolve("Открой браузер", _games, _now).Command?.Intent);
        Assert.False(dialog.HasPending);
    }
    [Fact] public void ChangedCatalogAndDuplicatePartNumbersCannotSelectWrongGame()
    {
        var dialog = new LaunchConversation(); dialog.Resolve("Запусти форзу", _games, _now);
        var changed = new[] { _games[0] with { Enabled = false }, _games[1] };
        Assert.Equal(Intent.Unknown, dialog.Resolve("вариант один", changed, _now).Command?.Intent);
        Assert.False(dialog.HasPending);
        LaunchTarget[] samePart = [Game("Forza Horizon 6", "1"), Game("Forza Motorsport 6", "2")];
        dialog.Resolve("Запусти форзу", samePart, _now);
        Assert.True(dialog.Resolve("шестую", samePart, _now).NeedsAnswer);
        Assert.Equal("2", dialog.Resolve("вариант два", samePart, _now).Command?.TargetId);
    }
    [Fact] public void IdenticalTitlesMentionSourceAndCanBeChosenByPosition()
    {
        var games = new[] { Game("Forza Horizon 6", "steam"), Game("Forza Horizon 6", "xbox") with { Source = "Xbox" } };
        var dialog = new LaunchConversation();
        var first = dialog.Resolve("Запусти форзу", games, _now);
        Assert.Contains("(Steam)", first.Question); Assert.Contains("(Xbox)", first.Question);
        Assert.True(dialog.Resolve("шестую", games, _now).NeedsAnswer);
        Assert.Equal("xbox", dialog.Resolve("вариант два", games, _now).Command?.TargetId);
    }
    [Fact] public void SpokenQuestionFitsSpeechProtocolAndDoesNotDropCandidates()
    {
        var games = Enumerable.Range(1, 6).Select(i => Game("Forza Horizon " + i + " " + new string('x', 100), i.ToString())).ToArray();
        var dialog = new LaunchConversation();
        Assert.InRange(dialog.Resolve("Запусти форзу", games, _now).Question!.Length, 1, 500);
        Assert.Equal(6, dialog.CandidateIds.Count);
        Assert.InRange(dialog.Resolve("не знаю", games, _now).Question!.Length, 1, 500);
    }
}
