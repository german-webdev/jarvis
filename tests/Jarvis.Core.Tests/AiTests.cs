using System.Net;
using System.Text.Json;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class AiTests
{
    [Fact]
    public void NewAndMissingConfigurationNeverEnableAi()
    {
        var settings = new AiConnectionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")).Load().Configuration;
        Assert.False(settings.Enabled);
        Assert.False(settings.UseForUnknownCommands);
        Assert.Empty(settings.Connections);
        Assert.Null(settings.Active);
    }

    [Fact]
    public void ProfilesRoundTripWithoutActivatingAConnection()
    {
        var path = Path.Combine(Path.GetTempPath(), "jarvis-ai-" + Guid.NewGuid() + ".json");
        try
        {
            var profile = new AiConnection { Name = "Local", Provider = "ollama", Endpoint = "http://localhost:11434", Model = "example" };
            var store = new AiConnectionStore(path);
            store.Save(new() { Connections = [profile], ActiveConnectionId = profile.Id });
            var result = store.Load();
            Assert.Null(result.Warning); Assert.False(result.Configuration.Enabled); Assert.Null(result.Configuration.Active);
            Assert.Equal(profile, Assert.Single(result.Configuration.Connections));
            File.WriteAllText(path, "invalid");
            Assert.NotNull(store.Load().Warning); Assert.Equal("invalid", File.ReadAllText(path));
        }
        finally { File.Delete(path); File.Delete(path + ".tmp"); }
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("https://example.com/v1", true)]
    [InlineData("http://example.com/v1", false)]
    [InlineData("https://user:secret@example.com/v1", false)]
    [InlineData("https://example.com/v1?key=secret", false)]
    [InlineData("file:///C:/test", false)]
    public void EndpointsDoNotAllowPlaintextRemoteKeysOrEmbeddedCredentials(string endpoint, bool valid)
    {
        var connection = new AiConnection { Name = "test", Endpoint = endpoint };
        if (valid) connection.Validate(); else Assert.Throws<InvalidDataException>(connection.Validate);
    }

    [Theory]
    [InlineData("Спроси нейросеть почему небо голубое", "почему небо голубое")]
    [InlineData("Джарвис, спроси нейросеть: что такое CPU?", "что такое CPU?")]
    [InlineData("Вопрос к нейросети: объясни OAuth", "объясни OAuth")]
    [InlineData("открой браузер", null)]
    public void ExplicitQuestionsPreserveTheActualQuestion(string phrase, string? expected) => Assert.Equal(expected, AiRouting.ExplicitQuestion(phrase));

    [Fact]
    public void AiFallbackCannotTakeOverLocalCommandsOrClarification()
    {
        var profile = new AiConnection { Name = "test", Provider = "claude" };
        var config = new AiConfiguration { Enabled = true, UseForUnknownCommands = true, ActiveConnectionId = profile.Id, Connections = [profile] };
        Assert.True(AiRouting.CanFallback(config, new(Intent.Unknown), false));
        Assert.False(AiRouting.CanFallback(config with { Enabled = false }, new(Intent.Unknown), false));
        Assert.False(AiRouting.CanFallback(config with { UseForUnknownCommands = false }, new(Intent.Unknown), false));
        Assert.False(AiRouting.CanFallback(config, new(Intent.Unknown), true));
        Assert.False(AiRouting.CanFallback(config, new(Intent.Unknown, CandidateIntent: Intent.LaunchTarget), false));
        Assert.False(AiRouting.CanFallback(config, new(Intent.Unknown, Explanation: "choice expired"), false));
        Assert.False(AiRouting.CanFallback(config, CommandRouter.Resolve("открой браузер"), false));
        Assert.False(AiRouting.CanFallback(config, CommandRouter.Resolve("стоп"), false));
    }

    [Theory]
    [InlineData("api", "/v1/chat/completions", "{\"choices\":[{\"message\":{\"content\":\"Ответ\"}}]}")]
    [InlineData("ollama", "/api/chat", "{\"message\":{\"content\":\"Ответ\"}}")]
    public async Task ChatUsesExpectedProtocolWithoutSendingTools(string provider, string path, string response)
    {
        var handler = new FixtureHandler(async request =>
        {
            Assert.Equal(path, request.RequestUri!.AbsolutePath);
            Assert.Equal("fixture-key", request.Headers.Authorization!.Parameter);
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(json.RootElement.TryGetProperty("tools", out _));
            Assert.Equal("Вопрос", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.DoesNotContain("fixture-key", body);
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        });
        using var http = new HttpClient(handler);
        var profile = new AiConnection { Name = "test", Provider = provider, Endpoint = provider == "api" ? "https://example.test/v1" : "http://localhost:11434", Model = "fixture" };
        Assert.Equal("Ответ", await new HttpAiClient(http).AskAsync(profile, "fixture-key", "Вопрос", CancellationToken.None));
    }

    [Fact]
    public async Task FailureDoesNotEchoServerBodyOrCredentials()
    {
        using var http = new HttpClient(new FixtureHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-body") })));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new HttpAiClient(http).ModelsAsync(new() { Name = "test", Endpoint = "https://example.test/v1" }, "fixture-key", CancellationToken.None));
        Assert.Contains("401", error.Message); Assert.DoesNotContain("secret-body", error.Message); Assert.DoesNotContain("fixture-key", error.Message);
    }

    [Fact]
    public async Task ModelsAndCancellationAreHandledWithoutGeneratingAnswers()
    {
        using var http = new HttpClient(new FixtureHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"name\":\"local-model\"}]}") });
        }));
        var client = new HttpAiClient(http);
        var profile = new AiConnection { Name = "test", Provider = "ollama", Endpoint = "http://localhost:11434" };
        Assert.Equal(["local-model"], await client.ModelsAsync(profile, "", CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ModelsAsync(profile, "", cancellation.Token));
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return response(request); }
    }
}
