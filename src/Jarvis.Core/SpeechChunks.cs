namespace Jarvis.Core;

/// <summary>Bound each speech request while retaining every character of the displayed reply.</summary>
public static class SpeechChunks
{
    public static IReadOnlyList<string> Split(string text, int maximumCharacters = 400)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumCharacters is < 2 or > 500) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var parts = new List<string>();
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(maximumCharacters, text.Length - offset);
            if (offset + length < text.Length)
            {
                var sentence = -1; var whitespace = -1;
                for (var index = 0; index < length; index++)
                {
                    var character = text[offset + index];
                    if (char.IsWhiteSpace(character)) whitespace = index + 1;
                    if (character is '.' or '!' or '?' or ';' or '\n') sentence = index + 1;
                }
                if (sentence >= length / 2) length = sentence;
                else if (whitespace > 0) length = whitespace;
                if (char.IsHighSurrogate(text[offset + length - 1]) && char.IsLowSurrogate(text[offset + length])) length--;
            }
            parts.Add(text.Substring(offset, length));
            offset += length;
        }
        return parts;
    }
}
