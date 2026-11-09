using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public class AiRequestServiceTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("profile")]
    [InlineData("credential")]
    [InlineData("disable")]
    [InlineData("late-error")]
    [InlineData("stop")]
    public async Task ChangingConnectionOrCancelingDiscardsLateResponse(string change)
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-context-" + Guid.NewGuid());
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new List<string>();
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return ++calls == 2 ? await late.Task : Answer();
        }));
        using var ai = new AiService(root, http);
        using var cancel = new CancellationTokenSource();
        var profile = new AiConnection { Name = "Fixture", Model = "fixture", Endpoint = "https://example.test/v1" };
        AiConfiguration Settings(AiConnection p) => new() { Enabled = true, ActiveConnectionId = p.Id, Connections = [p] };
        try
        {
            ai.Save(Settings(profile));
            Assert.True((await ai.AskAsync("first", CancellationToken.None)).Verified);
            var pending = ai.AskAsync("old pending", cancel.Token);
            Assert.Equal(2, calls);
            var replacement = change switch
            {
                "model" => profile with { Model = "other" },
                "endpoint" => profile with { Endpoint = "https://other.test/v1" },
                "profile" => profile with { Id = Guid.NewGuid().ToString("N") },
                "credential" => profile with { ProtectedApiKey = AiService.Protect("fixture-key") },
                _ => profile
            };
            if (change == "late-error") ai.Save(ai.Configuration with { Enabled = false });
            else if (change == "stop") cancel.Cancel();
            else ai.Save(Settings(replacement) with { Enabled = change != "disable" });
            if (change == "late-error") late.SetException(new IOException("late failure"));
            else late.SetResult(Answer());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (change is "disable" or "late-error") ai.Save(Settings(profile));
            Assert.True((await ai.AskAsync("next", CancellationToken.None)).Verified);
            using var json = JsonDocument.Parse(bodies[^1]);
            var messages = json.RootElement.GetProperty("messages");
            // Every request is isolated, including after cancellation.
            Assert.Equal(2, messages.GetArrayLength());
            Assert.DoesNotContain("first", bodies[^1]);
            Assert.DoesNotContain("old pending", bodies[^1]);
            Assert.DoesNotContain("first", File.ReadAllText(Path.Combine(root, "data", "ai-connections.json")));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "data")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EveryRequestSendsOnlyItsOwnQuestion()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-context-" + Guid.NewGuid());
        var lengths = new List<int>();
        HttpClient Client() => new(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            lengths.Add(json.RootElement.GetProperty("messages").GetArrayLength());
            return Answer();
        }));
        try
        {
            using (var ai = new AiService(root, Client()))
            {
                var p = new AiConnection { Name = "Fixture", Model = "fixture", Endpoint = "https://example.test/v1" };
                ai.Save(new() { Enabled = true, ActiveConnectionId = p.Id, Connections = [p] });
                await ai.AskAsync("first", CancellationToken.None);
                var revision = ai.ConnectionRevision;
                // Empty isolated account fixture: no browser, revoke request or real account read.
                await ai.ChatGpt.SignOutAsync(new() { Name = "Inactive", Provider = "chatgpt" }, CancellationToken.None);
                Assert.Equal(revision, ai.ConnectionRevision);
                ai.Save(ai.Configuration with { UseForUnknownCommands = true, Connections = [p with { Name = "Renamed" }] });
                Assert.Equal(revision, ai.ConnectionRevision);
                await ai.AskAsync("next", CancellationToken.None);
            }
            using (var ai = new AiService(root, Client())) await ai.AskAsync("fresh", CancellationToken.None);
            Assert.Equal(new[] { 2, 2, 2 }, lengths);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedAccountScopeCannotReceiveRequest(bool sameScope)
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-context-" + Guid.NewGuid());
        var responseCalls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "fixture-new", token_type = "Bearer", expires_in = 3600, scope = sameScope ? "resource.invoke chatgpt.tokens.use.direct" : "chatgpt.tokens.use.direct" })) });
            responseCalls++;
            if (responseCalls == 2)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(1, body.RootElement.GetProperty("input").GetArrayLength());
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}\n\ndata: {\"type\":\"response.completed\"}\n\n") });
        }));
        using var ai = new AiService(root, http);
        var profile = new AiConnection { Name = "Fixture", Provider = "chatgpt", Model = "fixture" };
        var fixture = new ChatGptAccount { ClientId = "fixture-client", Subject = "fixture-subject", AccessToken = "fixture", RefreshToken = "fixture-refresh", Scope = "chatgpt.tokens.use.direct resource.invoke", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        try
        {
            ai.Save(new() { Enabled = true, ActiveConnectionId = profile.Id, Connections = [profile] });
            var directory = Path.Combine(root, "data", "chatgpt-accounts");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, profile.Id + ".protected");
            File.WriteAllText(path, AiService.Protect(JsonSerializer.Serialize(fixture)));
            Assert.True((await ai.AskAsync("first", CancellationToken.None)).Verified);
            File.WriteAllText(path, AiService.Protect(JsonSerializer.Serialize(fixture with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) })));
            if (sameScope)
            {
                Assert.True((await ai.AskAsync("next", CancellationToken.None)).Verified);
                Assert.Equal(2, responseCalls);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ai.AskAsync("next", CancellationToken.None));
                Assert.Equal(1, responseCalls);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ClaudeReceivesOnlyCurrentQuestionWithoutSessionPersistence()
    {
        Assert.Equal("question", SubscriptionClient.RequestInput([new("user", "question")]));
        Assert.Contains("--no-session-persistence", SubscriptionClient.AnswerArguments(new() { Provider = "claude" }));
        Assert.DoesNotContain("--resume", SubscriptionClient.AnswerArguments(new() { Provider = "claude" }));
        Assert.Throws<InvalidDataException>(() => SubscriptionClient.RequestInput([new("user", new string('q', 4001))]));
        Assert.Throws<InvalidDataException>(() => SubscriptionClient.RequestInput([new("user", "first"), new("assistant", "old"), new("user", "next")]));
    }

    private static HttpResponseMessage Answer() => new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
}
