using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public class AiLocalActionServiceTests
{
    internal static readonly LaunchTarget FixtureTarget = new("file:C:\\private\\fixture.exe", "Fixture", "exe", @"C:\private\fixture.exe", "fixture", [], true);
    internal sealed class Windows : ILocalActionWindows
    {
        public Task<ObservedWindow?> ObserveAsync(LaunchTarget target, CancellationToken token) => Task.FromResult<ObservedWindow?>(new("window-fixture"));
        public bool IsCurrent(LaunchTarget target, ObservedWindow window) => true;
    }
    internal static AiConnection Configure(AiService ai, string root)
    {
        var profile = new AiConnection { Name = "Fixture", Provider = "chatgpt", Model = "fixture" };
        ai.Save(new() { Enabled = true, AllowLocalActions = true, ActiveConnectionId = profile.Id, Connections = [profile] });
        var directory = Path.Combine(root, "data", "chatgpt-accounts"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, profile.Id + ".protected"), AiService.Protect(JsonSerializer.Serialize(new ChatGptAccount
        { ClientId = "fixture-client", Subject = "fixture-subject", AccessToken = "fixture", Scope = "chatgpt.tokens.use.direct", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) })));
        return profile;
    }
    internal static HttpResponseMessage Tool(int step = 1) => new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new
    {
        type = "response.completed", response = new { status = "completed", output = new[] { new { type = "function_call", @namespace = "jarvis_local",
            name = step == 1 ? "launch_application" : "focus_application", call_id = step.ToString(), arguments = "{\"target_id\":\"app_1\"}", status = "completed" } } }
    }) + "\n\n") };
    internal static HttpResponseMessage TextAnswer() => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}]}}\n\n") };

    [Theory]
    [InlineData("stop")]
    [InlineData("toggle")]
    [InlineData("model")]
    [InlineData("account")]
    [InlineData("aba")]
    public async Task InvalidationCancelsWholeTaskAndRejectsLateToolCall(string change)
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-action-" + Guid.NewGuid());
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> bodies = []; var callCount = 0; var actions = 0;
        using var http = new HttpClient(new Handler(async request =>
        { bodies.Add(await request.Content!.ReadAsStringAsync()); return ++callCount == 2 ? await late.Task : TextAnswer(); }));
        using var ai = new AiService(root, http); using var stop = new CancellationTokenSource();
        try
        {
            var p = Configure(ai, root);
            await ai.AskAsync("previous", CancellationToken.None);
            var executor = new LocalActionExecutor(() => [FixtureTarget], new([new DelegateSkill([Intent.LaunchTarget, Intent.FocusTarget], (_, _) =>
            { actions++; return Task.FromResult(new SkillResult(true, "fixture")); })]), new Windows(), () => ai.Configuration.AllowLocalActions);
            var pending = ai.RunTaskAsync("obsolete task", executor, null, stop.Token);
            Assert.Equal(2, callCount);
            switch (change)
            {
                case "stop": stop.Cancel(); break;
                case "toggle": ai.Save(ai.Configuration with { AllowLocalActions = false }); break;
                case "model": ai.Save(ai.Configuration with { Connections = [p with { Model = "other" }] }); break;
                case "account": await ai.ChatGpt.SignOutAsync(p, CancellationToken.None); break;
                case "aba":
                    ai.Save(ai.Configuration with { Connections = [p with { Model = "other" }] });
                    ai.Save(ai.Configuration with { Connections = [p] }); break;
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            late.SetResult(Tool()); await Task.Yield();
            Assert.Equal(0, actions);
            Assert.DoesNotContain("obsolete task", File.ReadAllText(Path.Combine(root, "data", "ai-connections.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SuccessfulTaskDoesNotLeakIntoNextQuestion()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-action-" + Guid.NewGuid()); List<string> bodies = []; var actions = 0;
        using var http = new HttpClient(new Handler(async request =>
        { bodies.Add(await request.Content!.ReadAsStringAsync()); return bodies.Count < 3 ? Tool(bodies.Count) : TextAnswer(); }));
        using var ai = new AiService(root, http);
        try
        {
            Configure(ai, root);
            var executor = new LocalActionExecutor(() => [FixtureTarget], new([new DelegateSkill([Intent.LaunchTarget, Intent.FocusTarget], (request, _) =>
            { actions++; return Task.FromResult(new SkillResult(request.Intent == Intent.FocusTarget, "fixture")); })]), new Windows(), () => true);
            var result = await ai.RunTaskAsync("open fixture", executor, null, CancellationToken.None);
            Assert.True(result.Verified); Assert.Equal(2, actions);
            await ai.AskAsync("what happened", CancellationToken.None);
            using var json = JsonDocument.Parse(bodies[^1]);
            Assert.Equal(1, json.RootElement.GetProperty("input").GetArrayLength());
            Assert.DoesNotContain("open fixture", bodies[^1]);
            Assert.False(json.RootElement.TryGetProperty("tools", out _));
            Assert.DoesNotContain("app_1", bodies[^1]); Assert.DoesNotContain("function_call", bodies[^1]);
            Assert.All(bodies, body => Assert.DoesNotContain("private", body));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("api", true)]
    [InlineData("ollama", true)]
    [InlineData("claude", true)]
    [InlineData("chatgpt", false)]
    public async Task UnsupportedOrDisabledActionsNeverReadAccountOrSendRequest(string provider, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-action-" + Guid.NewGuid()); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(TextAnswer()); }));
        using var ai = new AiService(root, http);
        try
        {
            var p = new AiConnection { Name = "fixture", Provider = provider, Endpoint = "https://example.test/v1", Model = "fixture" };
            ai.Save(new() { Enabled = true, AllowLocalActions = allowed, ActiveConnectionId = p.Id, Connections = [p] });
            var executor = new LocalActionExecutor(() => [FixtureTarget], new([]), new Windows(), () => allowed);
            Assert.False((await ai.RunTaskAsync("open fixture", executor, null, CancellationToken.None)).Verified);
            Assert.Equal(0, calls);
            Assert.False(Directory.Exists(Path.Combine(root, "data", "chatgpt-accounts")));
        }
        finally { Directory.Delete(root, true); }
    }
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
