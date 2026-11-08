using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class PowerCommandsTests
{
    public static IEnumerable<object[]> Phrases()
    {
        foreach (var name in new[] { "компьютер", "Компухтер", "компудахтер", "Компутер" })
        {
            yield return [$"выключи {name}", Intent.ShutdownComputer];
            yield return [$"Джарвис, перезагрузи {name}, пожалуйста", Intent.RestartComputer];
            yield return [$"переведи {name} в режим сна", Intent.SleepComputer];
            yield return [$"переведи {name} в гибернацию", Intent.HibernateComputer];
        }
    }
    [Theory, MemberData(nameof(Phrases))]
    public void ExactPowerCommandsAcceptComputerAliases(string text, Intent expected) => Assert.Equal(expected, CommandRouter.Parse(text));

    [Theory]
    [InlineData("режим сна", Intent.SleepComputer)]
    [InlineData("гибернация", Intent.HibernateComputer)]
    [InlineData("включи гибернацию", Intent.HibernateComputer)]
    [InlineData("переведи компьютер в режим гибернации", Intent.HibernateComputer)]
    [InlineData("выключи себя", Intent.ExitAssistant)]
    [InlineData("выключи музыку", Intent.MediaPause)]
    public void DistinctActions(string text, Intent expected) => Assert.Equal(expected, CommandRouter.Parse(text));

    [Theory]
    [InlineData("не выключи компухтер")]
    [InlineData("выключи компьютер?")]
    [InlineData("выключи компутер и перезагрузи компьютер")]
    [InlineData("как перезагрузить компьютер")]
    [InlineData("открой режим сна")]
    [InlineData("скажи выключи компьютер")]
    public void NeverInferPowerFromOtherPhrases(string text) => Assert.False(PowerConfirmation.IsPower(CommandRouter.Parse(text)));

    [Theory]
    [InlineData("да")]
    [InlineData("отмена")]
    [InlineData("стоп")]
    [InlineData("останови")]
    [InlineData("нет")]
    [InlineData("не подтверждаю")]
    [InlineData("да подтверждаю?")]
    [InlineData("да подтверждаю и открой браузер")]
    [InlineData("спроси нейросеть подтверди")]
    public void OtherAnswersCancelAndCannotBeReplayed(string answer)
    {
        var gate = new PowerConfirmation(); var now = DateTimeOffset.UtcNow;
        gate.Begin(Intent.ShutdownComputer, now);
        Assert.Null(gate.Consume(answer, now));
        Assert.False(gate.HasPending);
        Assert.Null(gate.Consume("да подтверждаю", now));
    }
    [Theory]
    [InlineData(Intent.ShutdownComputer)]
    [InlineData(Intent.RestartComputer)]
    [InlineData(Intent.SleepComputer)]
    [InlineData(Intent.HibernateComputer)]
    public void ConfirmationIsOneShotAndExpires(Intent intent)
    {
        var gate = new PowerConfirmation(); var now = DateTimeOffset.UtcNow;
        Assert.Null(gate.Consume("да подтверждаю", now));
        Assert.Contains(PowerConfirmation.ActionName(intent), gate.Begin(intent, now));
        Assert.Equal(intent, gate.Consume("Да, подтверждаю!", now.AddSeconds(1)));
        Assert.Null(gate.Consume("да подтверждаю", now.AddSeconds(2)));
        gate.Begin(intent, now);
        Assert.Null(gate.Consume("да подтверждаю", now.AddSeconds(30)));
        gate.Begin(intent, now); gate.Clear();
        Assert.Null(gate.Consume("да подтверждаю", now));
        gate.Begin(intent, now); gate.Renew(now.AddSeconds(20));
        Assert.Equal(intent, gate.Consume("подтверждаю", now.AddSeconds(35)));
    }
}
