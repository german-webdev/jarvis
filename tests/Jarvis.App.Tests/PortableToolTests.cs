using System.Net;
using System.Net.Http;
using System.Text.Json;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public sealed class PortableToolTests
{
    private sealed class TestProfile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-portable-" + Guid.NewGuid());
        public void Dispose() { if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, true); }
    }
    [Theory]
    [InlineData("api")]
    [InlineData("ollama")]
    public async Task SelectedProviderReceivesToolsAndVerifiedObservations(string provider)
    {
        using var profile = new TestProfile();
        var requests = 0;
        using var http = new HttpClient(new AiLocalActionServiceTests.Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = body.RootElement.GetProperty("messages");
            Assert.Contains("JSON", messages[0].GetProperty("content").GetString());
            Assert.Contains("browser_navigate", messages[1].GetProperty("content").GetString());
            if (++requests == 2) Assert.Contains("fixture observation", messages[1].GetProperty("content").GetString());
            var output = requests == 1 ? "{\"name\":\"browser_navigate\",\"arguments\":{}}" : "{\"name\":\"browser_show\",\"arguments\":{}}";
            object response = provider == "ollama" ? new { message = new { content = output } }
                : new { choices = new[] { new { message = new { content = output } } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }));
        using var ai = new AiService(profile.Path, http);
        var connection = new AiConnection { Name = "Fixture", Provider = provider, Endpoint = "https://example.test/v1", Model = "fixture" };
        ai.Save(new() { Enabled = true, UseForUnknownCommands = true, Connections = [connection], ActiveConnectionId = connection.Id });
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(new Browser());
        ai.OpenBrowserPage = (_, _) => throw new Exception("Already-open page should be retained");
        var result = await ai.RunBrowserRequestAsync("Открой example.com", true, null, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal("Выполнено.", result.Message);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"name\":\"browser_show\",\"arguments\":[],\"text\":\"done\"}")]
    [InlineData("{\"text\":null}")]
    public void InvalidPlansCannotExecute(string text) => Assert.Throws<System.IO.InvalidDataException>(() => PortableToolProtocol.Parse(text));

    private sealed class Browser : IAiToolSession
    {
        public IReadOnlyList<AiToolDefinition> Tools { get; } = new[] { "browser_navigate", "browser_show" }
            .Select(name => new AiToolDefinition(name, "fixture", JsonSerializer.SerializeToElement(new { }))).ToArray();
        public Task<AiToolObservation> InvokeAsync(string name, JsonElement args, CancellationToken token) => Task.FromResult(
            new AiToolObservation(true, "fixture observation", "Выполнено.", "https://example.com/", Complete: name == "browser_show", AlreadyOpened: true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
