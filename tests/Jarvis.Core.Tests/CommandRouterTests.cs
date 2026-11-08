using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class CommandRouterTests
{
    [Theory]
    [InlineData("Джарвис, открой браузер.")]
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
    [InlineData("")]
    public void RejectsAmbiguousOrUnsupportedRequests(string text) => Assert.Equal(Intent.Unknown, CommandRouter.Parse(text));

    [Theory]
    [InlineData("Джарвис, отмена")]
    [InlineData("отмени")]
    public void RecognizesCancellation(string text) => Assert.Equal(Intent.Cancel, CommandRouter.Parse(text));
}
