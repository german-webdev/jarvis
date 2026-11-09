using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class LocalActionsTests
{
    private static LaunchTarget Target(string id = "file:C:\\private\\fixture.exe") => new(id, "Fixture", "exe", @"C:\private\fixture.exe", "fixture", [], true);
    private sealed class Windows : ILocalActionWindows
    {
        public bool Exists = true;
        public int Observations;
        public Task<ObservedWindow?> ObserveAsync(LaunchTarget target, CancellationToken token) { Observations++; return Task.FromResult(Exists ? new ObservedWindow("observed-window") : null); }
        public bool IsCurrent(LaunchTarget target, ObservedWindow window) => Exists;
    }
    private static SkillDispatcher Skills(List<CommandRequest> commands, Action<CommandRequest>? callback = null) => new([
        new DelegateSkill([Intent.LaunchTarget, Intent.FocusTarget], (command, _) =>
        { commands.Add(command); callback?.Invoke(command); return Task.FromResult(new SkillResult(command.Intent == Intent.FocusTarget, "submitted")); })]);
    private static AiActionTurn Call(string id = "one", string name = "launch_application", string args = "{\"target_id\":\"app_1\"}") =>
        new([JsonSerializer.SerializeToElement(new { type = "function_call", @namespace = "jarvis_local", call_id = id, name, arguments = args })], [new(id, name, args)], "");
    private static readonly AiMessage[] Conversation = [new("user", "Open Fixture then focus it")];

    [Fact]
    public async Task LaunchRequiresObservedWindowAndFocusUsesItsIdentity()
    {
        List<CommandRequest> commands = []; var windows = new Windows(); var target = Target();
        var executor = new LocalActionExecutor(() => [target], Skills(commands), windows, () => true);
        List<AiActionRequest> requests = [];
        var answer = await AiLocalActions.RunAsync(Conversation, executor, (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(requests.Count switch { 1 => Call(), 2 => Call("two", "focus_application"), _ => new AiActionTurn([], [], "All done") });
        }, null, CancellationToken.None);
        Assert.Equal([Intent.LaunchTarget, Intent.FocusTarget], commands.Select(c => c.Intent));
        Assert.Equal(target.Id, commands[0].TargetId); Assert.Equal("observed-window", commands[1].TargetId);
        Assert.Equal(1, windows.Observations); Assert.Contains("Переключение", answer);
        Assert.Equal("app_1", requests[0].Targets[0].Id);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(requests));
        Assert.Contains("function_call_output", JsonSerializer.Serialize(requests[1].Continuation));
        Assert.Contains("verified", JsonSerializer.Serialize(requests[1].Continuation));
    }

    [Theory]
    [InlineData("shell", "{}")]
    [InlineData("launch_application", "{\"target_id\":\"unknown\"}")]
    [InlineData("launch_application", "{\"target_id\":\"app_1\",\"exe\":\"evil\"}")]
    [InlineData("launch_application", "{\"target_id\":\"app_1\",\"target_id\":\"app_1\"}")]
    [InlineData("launch_application", "{\"target_id\":3}")]
    [InlineData("launch_application", "[]")]
    [InlineData("focus_application", "{\"target_id\":\"app_1\"}")]
    public async Task InvalidCallsNeverReachSkills(string name, string args)
    {
        List<CommandRequest> commands = [];
        var executor = new LocalActionExecutor(() => [Target()], Skills(commands), new Windows(), () => true);
        await Assert.ThrowsAnyAsync<Exception>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) => Task.FromResult(Call(name: name, args: args)), null, CancellationToken.None));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task UnobservedLaunchStopsBeforeModelOrFocus()
    {
        List<CommandRequest> commands = []; var count = 0;
        var executor = new LocalActionExecutor(() => [Target()], Skills(commands), new Windows { Exists = false }, () => true);
        await Assert.ThrowsAsync<InvalidDataException>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) =>
        { count++; return Task.FromResult(Call()); }, null, CancellationToken.None));
        Assert.Single(commands); Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayCannotRepeatExternalAction(bool changedCallId)
    {
        List<CommandRequest> commands = []; var count = 0; var windows = new Windows();
        var executor = new LocalActionExecutor(() => [Target()], Skills(commands), windows, () => true);
        await Assert.ThrowsAsync<InvalidDataException>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) =>
        { count++; if (count == 2) windows.Exists = false; return Task.FromResult(Call(changedCallId ? count.ToString() : "one")); }, null, CancellationToken.None));
        Assert.Single(commands);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("model")]
    [InlineData("progress")]
    [InlineData("after-launch")]
    [InlineData("between")]
    public async Task StopAtEveryBoundaryPreventsFollowingAction(string boundary)
    {
        using var stop = new CancellationTokenSource(); List<CommandRequest> commands = []; int count = 0;
        var executor = new LocalActionExecutor(() => [Target()], Skills(commands, _ => { if (boundary == "after-launch") stop.Cancel(); }), new Windows(), () => true);
        if (boundary == "before") stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) =>
        {
            count++;
            if (boundary == "model" || boundary == "between" && count == 2) stop.Cancel();
            return Task.FromResult(Call(count.ToString(), count == 1 ? "launch_application" : "focus_application"));
        }, _ => { if (boundary == "progress") stop.Cancel(); }, stop.Token));
        Assert.Equal(boundary is "after-launch" or "between" ? 1 : 0, commands.Count);
    }

    [Fact]
    public async Task CancellationDoesNotWaitForUncooperativeTransportOrExecuteItsLateCall()
    {
        using var stop = new CancellationTokenSource(); List<CommandRequest> commands = [];
        var late = new TaskCompletionSource<AiActionTurn>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new LocalActionExecutor(() => [Target()], Skills(commands), new Windows(), () => true);
        var pending = AiLocalActions.RunAsync(Conversation, executor, (_, _) => late.Task, null, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult(Call()); await Task.Yield(); Assert.Empty(commands);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("removed")]
    [InlineData("changed")]
    [InlineData("permission")]
    public async Task CatalogAndPermissionAreRecheckedAfterPlanning(string change)
    {
        List<CommandRequest> commands = []; var target = Target(); IReadOnlyList<LaunchTarget> catalog = [target]; bool allowed = true;
        var executor = new LocalActionExecutor(() => catalog, Skills(commands), new Windows(), () => allowed);
        await Assert.ThrowsAnyAsync<Exception>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) =>
        {
            if (change == "permission") allowed = false;
            else catalog = change switch { "removed" => [], "disabled" => [target with { Enabled = false }], _ => [target with { Target = @"C:\other.exe" }] };
            return Task.FromResult(Call());
        }, null, CancellationToken.None));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task StaleWindowStopsFocusAndFourthActionCannotExecute()
    {
        List<CommandRequest> commands = []; var target = Target(); var windows = new Windows();
        var executor = new LocalActionExecutor(() => [target], Skills(commands), windows, () => true);
        Assert.True((await executor.ExecuteAsync(new(LocalActionKind.LaunchApplication, target.Id), null, CancellationToken.None)).Verified);
        windows.Exists = false;
        Assert.False((await executor.ExecuteAsync(new(LocalActionKind.FocusApplication, target.Id), null, CancellationToken.None)).Verified);
        Assert.Single(commands);
        commands.Clear(); windows.Exists = true;
        var targets = Enumerable.Range(1, 4).Select(i => Target(i.ToString()) with { Name = "Fixture " + i }).ToArray();
        executor = new(() => targets, Skills(commands), windows, () => true);
        foreach (var t in targets) await executor.ExecuteAsync(new(LocalActionKind.LaunchApplication, t.Id), null, CancellationToken.None);
        Assert.Equal(3, commands.Count);
    }

    [Fact]
    public async Task MultipleCallsAreRejectedBeforeAnyExecution()
    {
        List<CommandRequest> commands = []; var executor = new LocalActionExecutor(() => [Target()], Skills(commands), new Windows(), () => true);
        var turn = Call(); turn = turn with { Calls = [turn.Calls[0], turn.Calls[0] with { Id = "two" }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) => Task.FromResult(turn), null, CancellationToken.None));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task AmbiguousNamesAndOrdinaryTextNeverExecuteActions()
    {
        List<CommandRequest> commands = []; var targets = new[] { Target("a"), Target("b") };
        var executor = new LocalActionExecutor(() => targets, Skills(commands), new Windows(), () => true);
        Assert.False((await executor.ExecuteAsync(new(LocalActionKind.LaunchApplication, "a"), null, CancellationToken.None)).Verified);
        executor = new(() => [Target()], Skills(commands), new Windows(), () => true);
        await Assert.ThrowsAsync<InvalidDataException>(() => AiLocalActions.RunAsync(Conversation, executor,
            (_, _) => Task.FromResult(new AiActionTurn([], [], "launch_application({target_id: app_1})")), null, CancellationToken.None));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task FourModelRequestsAndThreeActionsAreHardLimits()
    {
        List<CommandRequest> commands = []; var calls = 0;
        var targets = Enumerable.Range(1, 4).Select(i => Target(i.ToString()) with { Name = "Fixture " + i }).ToArray();
        var executor = new LocalActionExecutor(() => targets, Skills(commands), new Windows(), () => true);
        await Assert.ThrowsAsync<InvalidDataException>(() => AiLocalActions.RunAsync(Conversation, executor, (_, _) =>
        { calls++; return Task.FromResult(Call(calls.ToString(), args: "{\"target_id\":\"app_" + calls + "\"}")); }, null, CancellationToken.None));
        Assert.Equal(4, calls); Assert.Equal(3, commands.Count);
    }

    [Fact]
    public async Task ResponsesContractCarriesStrictToolsOpaqueIdsAndCompletedOutputs()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(ChatGptPlanClient.Resource + "/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture", request.Headers.Authorization!.Parameter);
            var body = await request.Content!.ReadAsStringAsync(); using var json = JsonDocument.Parse(body); var root = json.RootElement;
            Assert.False(root.GetProperty("parallel_tool_calls").GetBoolean()); Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Single(root.GetProperty("tools").EnumerateArray());
            Assert.Equal("namespace", root.GetProperty("tools")[0].GetProperty("type").GetString());
            Assert.Equal("jarvis_local", root.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal(2, root.GetProperty("tools")[0].GetProperty("tools").GetArrayLength());
            var tool = root.GetProperty("tools")[0].GetProperty("tools")[0]; Assert.True(tool.GetProperty("strict").GetBoolean());
            Assert.False(tool.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("app_1", tool.GetProperty("parameters").GetProperty("properties").GetProperty("target_id").GetProperty("enum")[0].GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent(Completed(Call().Output)) };
        }));
        var result = await new ChatGptPlanClient(http).ActionTurnAsync(new() { AccessToken = "fixture" }, "fixture", new(Conversation, [new("app_1", "Fixture")], []), CancellationToken.None);
        Assert.Single(result.Calls); Assert.Equal("one", result.Calls[0].Id);
    }

    [Theory]
    [InlineData("data: {\"type\":\"response.function_call_arguments.done\",\"arguments\":\"{}\"}\n\n")]
    [InlineData("data: {\"type\":\"response.incomplete\"}\n\n")]
    [InlineData("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"incomplete\",\"output\":[]}}\n\n")]
    public async Task IncompleteStreamNeverProducesExecutableCalls(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await Assert.ThrowsAsync<InvalidDataException>(() => ChatGptPlanClient.ReadActionTurnAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void ExplicitTaskAndStopAreSeparateFromConversationAndMedia()
    {
        Assert.Null(AiRouting.ExplicitTask("открой приложение")); Assert.Null(AiRouting.ExplicitTask("спроси нейросеть открой приложение"));
        Assert.Equal("открой приложение", AiRouting.ExplicitTask("Задача ИИ: открой приложение"));
        Assert.Equal("open app", AiRouting.ExplicitTask("AI task: open app"));
        Assert.True(AiRouting.IsStopTask("Джарвис, останови задачу")); Assert.True(AiRouting.IsStopTask("stop task"));
        Assert.False(AiRouting.IsStopTask("стоп")); Assert.False(new AiConfiguration().AllowLocalActions);
    }
    public static string Completed(IReadOnlyList<JsonElement> output) => "data: " + JsonSerializer.Serialize(new { type = "response.completed", response = new { status = "completed", output } }) + "\n\n";
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
