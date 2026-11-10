using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jarvis.Core;

public sealed partial class ChatGptPlanClient
{
    public async Task<AiActionTurn> ToolTurnAsync(ChatGptAccount account, string model, IReadOnlyList<AiMessage> messages,
        IReadOnlyList<AiToolDefinition> tools, IReadOnlyList<AiToolExchange> continuation, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidDataException("Выбери модель в настройках нейросети.");
        AiRequest.ValidateInput(messages);
        const string toolNamespace = "jarvis_tools";
        var input = messages.Select(message => JsonSerializer.SerializeToElement(message)).Concat(continuation.SelectMany(step =>
            step.ModelOutput.Append(JsonSerializer.SerializeToElement(new { type = "function_call_output", call_id = step.CallId,
                output = JsonSerializer.Serialize(new { verified = step.Observation.Verified, observation = step.Observation.Data }) })))).ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, Resource + "/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Content = JsonContent.Create(new { model, store = false, stream = true, parallel_tool_calls = false,
            include = new[] { "reasoning.encrypted_content" }, input,
            instructions = BrowserToolPrompt.Instructions,
            tools = new[] { new { type = "namespace", name = toolNamespace, description = "Work with websites in the current browser session.",
                tools = tools.Select(tool => new { type = "function", name = tool.Name, description = tool.Description,
                    strict = false, parameters = tool.Parameters }).ToArray() } } });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            throw new InvalidDataException("ChatGPT отклонил запрос с браузерным инструментом. Проверь поддержку инструментов у выбранной модели.");
        CheckStatus(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await ReadToolTurnAsync(stream, toolNamespace, token);
    }
}
