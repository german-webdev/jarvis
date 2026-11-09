using System.Text.Json.Serialization;

namespace Jarvis.Core;

public sealed record AiMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public static class AiRequest
{
    public static void ValidateInput(IReadOnlyList<AiMessage> messages)
    {
        if (messages.Count != 1 || messages[0].Role != "user")
            throw new InvalidDataException("Запрос должен содержать одно сообщение пользователя.");
        if (string.IsNullOrWhiteSpace(messages[0].Content) || messages[0].Content.Length > 4000)
            throw new InvalidDataException("Вопрос должен содержать от 1 до 4000 символов.");
    }
}
