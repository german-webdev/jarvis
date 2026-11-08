using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jarvis.Core;

public sealed class HttpAiClient(HttpClient client)
{
    public const string SystemPrompt = "Ты JARVIS, голосовой помощник. Отвечай по-русски, кратко, обычным текстом, не более 6 предложений. У тебя нет доступа к компьютеру пользователя и нет инструментов. Не утверждай, что выполнил действие на компьютере. Если тебя просят что-то открыть или изменить, объясни, что для этого нужны встроенные команды JARVIS.";

    public async Task<string[]> ModelsAsync(AiConnection connection, string key, CancellationToken token)
    {
        using var document = await SendAsync(connection, key, connection.Provider == "ollama" ? "api/tags" : "models", null, token);
        var property = connection.Provider == "ollama" ? "models" : "data";
        if (!document.RootElement.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Сервис вернул неизвестный формат списка моделей.");
        return entries.EnumerateArray().Select(e => e.TryGetProperty(connection.Provider == "ollama" ? "name" : "id", out var name) ? name.GetString() : null)
            .OfType<string>().Where(n => n.Length is > 0 and <= 200 && !n.Any(char.IsControl)).Distinct().Order().Take(500).ToArray();
    }

    public async Task<string> AskAsync(AiConnection connection, string key, string question, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(connection.Model)) throw new InvalidDataException("Выбери модель в настройках нейросети.");
        if (question.Length is 0 or > 4000) throw new InvalidDataException("Вопрос должен содержать от 1 до 4000 символов.");
        var messages = new[] { new { role = "system", content = SystemPrompt }, new { role = "user", content = question } };
        object body = connection.Provider == "ollama"
            ? new { model = connection.Model, messages, stream = false, options = new { num_predict = 512 } }
            : new { model = connection.Model, messages, stream = false, max_tokens = 512 };
        using var document = await SendAsync(connection, key, connection.Provider == "ollama" ? "api/chat" : "chat/completions", body, token);
        string? answer;
        try
        {
            answer = connection.Provider == "ollama"
                ? document.RootElement.GetProperty("message").GetProperty("content").GetString()
                : document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new InvalidDataException("Сервис не вернул текстовый ответ."); }
        return CleanAnswer(answer);
    }

    public static string CleanAnswer(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("Нейросеть вернула пустой ответ.");
        answer = answer.Trim();
        return answer.Length <= 1800 ? answer : answer[..1800] + "…";
    }

    private async Task<JsonDocument> SendAsync(AiConnection connection, string key, string path, object? body, CancellationToken token)
    {
        connection.Validate();
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(connection.BaseAddress(), path));
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"Сервис нейросети вернул HTTP {(int)response.StatusCode}. Проверь адрес, доступ и выбранную модель.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + count > 2_000_000) throw new InvalidDataException("Ответ сервиса слишком большой.");
            buffer.Write(block, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
}
