using System.Net;
using System.Text.Json;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class AiRequestTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData("api")]
    [InlineData("ollama")]
    [InlineData("chatgpt")]
    public async Task AllHttpAdaptersSendOneQuestionWithoutToolsOrServerConversation(string provider)
    {
        var input = new AiMessage[] { new("user", "question") };
        using var http = new HttpClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = json.RootElement;
            var messages = body.GetProperty(provider == "chatgpt" ? "input" : "messages").EnumerateArray().ToArray();
            if (provider != "chatgpt") messages = messages.Skip(1).ToArray();
            Assert.Equal(input.Select(m => m.Role), messages.Select(m => m.GetProperty("role").GetString()));
            Assert.Equal(input.Select(m => m.Content), messages.Select(m => m.GetProperty("content").GetString()));
            Assert.False(body.TryGetProperty("tools", out _)); Assert.False(body.TryGetProperty("previous_response_id", out _));
            if (provider == "chatgpt") Assert.False(body.GetProperty("store").GetBoolean());
            var response = provider switch
            {
                "chatgpt" => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\"}\n\n",
                "ollama" => "{\"message\":{\"content\":\"ok\"}}",
                _ => "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }));
        if (provider == "chatgpt") Assert.Equal("ok", await new ChatGptPlanClient(http).AskAsync(new(), "fixture", input, None));
        else Assert.Equal("ok", await new HttpAiClient(http).AskAsync(new() { Name = "fixture", Provider = provider, Model = "fixture", Endpoint = "http://localhost:11434" }, "", input, None));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
}
