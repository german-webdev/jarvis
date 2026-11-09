using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class SpeechChunksTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(400)] [InlineData(500)] [InlineData(501)] [InlineData(12000)]
    public void BoundsEveryRequestWithoutDroppingText(int length)
    {
        var text = new string('я', length);
        var parts = SpeechChunks.Split(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 400));
    }

    [Fact]
    public void KeepsParagraphsAndUnicodeAndPrefersSentenceBoundaries()
    {
        var text = string.Concat(Enumerable.Repeat("Джарвис 🙂 отвечает спокойно. Следующий абзац:\nМузыка, фильмы и голос.\n\n", 90));
        var parts = SpeechChunks.Split(text);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, part => { Assert.InRange(part.Length, 1, 400); Assert.False(char.IsHighSurrogate(part[^1])); });
        Assert.EndsWith(".", SpeechChunks.Split(new string('a', 290) + ". " + new string('b', 300))[0]);
        var emojiText = new string('a', 399) + "🙂" + new string('b', 500);
        Assert.Equal(emojiText, string.Concat(SpeechChunks.Split(emojiText)));
        Assert.All(SpeechChunks.Split(emojiText), part => Assert.False(char.IsHighSurrogate(part[^1])));
    }
}
