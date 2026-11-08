using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class CommandRouterTests
{
    [Theory]
    [InlineData("закрой себя", Intent.ExitAssistant)]
    [InlineData("Джарвис, закрой Джарвиса", Intent.ExitAssistant)]
    [InlineData("закрой Jarvis", Intent.ExitAssistant)]
    [InlineData("открой Джарвис", Intent.AssistantPresent)]
    [InlineData("запусти Jarvis", Intent.AssistantPresent)]
    [InlineData("как дела?", Intent.HowAreYou)]
    [InlineData("Джарвис, как у тебя дела?", Intent.HowAreYou)]
    public void RecognizesAssistantCommands(string text, Intent intent) => Assert.Equal(intent, CommandRouter.Parse(text));

    [Theory]
    [InlineData("не закрывай себя")]
    [InlineData("закрой себя?")]
    [InlineData("как закрыть Джарвис")]
    [InlineData("закрой себя и выключи компьютер")]
    public void DoesNotExitForNegatedQuestionsOrCompoundRequests(string text) => Assert.NotEqual(Intent.ExitAssistant, CommandRouter.Parse(text));
    [Theory]
    [InlineData("Джарвис, открой браузер.")]
    [InlineData("Джервис, открой браузер.")]
    [InlineData("Джарвиз, открой браузер.")]
    [InlineData("Чарвис, открой браузер.")]
    [InlineData("Запусти браузер")]
    [InlineData("пожалуйста открой мне браузер")]
    [InlineData("Jarvis, включи интернет браузер!")]
    public void RecognizesSupportedRequests(string text) => Assert.Equal(Intent.OpenBrowser, CommandRouter.Parse(text));

    [Theory]
    [InlineData("не открывай браузер")]
    [InlineData("не надо, открой браузер")]
    [InlineData("как открыть браузер")]
    [InlineData("открой браузер?")]
    [InlineData("открой браузер и выключи компьютер")]
    [InlineData("я сказал ему открой браузер")]
    [InlineData("сервис открой браузер")]
    [InlineData("чарльз открой браузер")]
    [InlineData("")]
    public void RejectsAmbiguousOrUnsupportedRequests(string text) => Assert.Equal(Intent.Unknown, CommandRouter.Parse(text));

    [Theory]
    [InlineData("Джарвис, отмена")]
    [InlineData("отмени")]
    public void RecognizesCancellation(string text) => Assert.Equal(Intent.Cancel, CommandRouter.Parse(text));
}
