using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public sealed class AiBrowserTaskTests
{
    private const string Prompt = "Открой YouTube и найди видео про котиков";
    private const string Url = "https://www.youtube.com/results?search_query=cats";
    private const string Watch = "https://www.youtube.com/watch?v=abcdefghijk";
    private const string Channel = "https://www.youtube.com/@FixtureChannel";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelReceivesTheObservationAndCannotOverrideIt(bool verified)
    {
        using var root = new TestProfile();
        var browser = new Browser(verified);
        var requests = 0;
        using var ai = new AiService(root.Path, new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var input = body.RootElement.GetProperty("input");
            Assert.Equal(Prompt, input[0].GetProperty("content").GetString());
            Assert.Equal("jarvis_tools", body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
            if (++requests == 1) { Assert.Equal(1, input.GetArrayLength()); return Call(); }
            Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
            Assert.Equal("call_fixture", input[2].GetProperty("call_id").GetString());
            using var output = JsonDocument.Parse(input[2].GetProperty("output").GetString()!);
            Assert.Equal(verified, output.RootElement.GetProperty("verified").GetBoolean());
            Assert.Equal("observed page data", output.RootElement.GetProperty("observation").GetString());
            return verified ? Show() : AiLocalActionServiceTests.TextAnswer();
        })));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        List<string> opened = [];
        ai.OpenBrowserPage = (address, _) =>
        {
            Assert.True(browser.Disposed);
            Assert.Equal(2, requests);
            opened.Add(address.AbsoluteUri);
        };
        var result = await ai.RunBrowserRequestAsync(Prompt, true, null, CancellationToken.None);
        Assert.Equal(verified, result.Verified);
        Assert.Equal(verified ? Url : null, result.SourceUrl);
        Assert.Equal(verified ? "Выполнено." : "connection error", result.Message);
        Assert.Equal(verified ? new[] { Url } : Array.Empty<string>(), opened);
        Assert.True(browser.Disposed);
        Assert.Equal(1, browser.Calls);
    }

    [Fact]
    public async Task OnlyTheFinalVerifiedWatchPageOpensForAVideoRequest()
    {
        using var root = new TestProfile();
        var browser = new Browser(true);
        var requests = 0;
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ => Task.FromResult(++requests switch
        {
            1 => Call(),
            2 => Call(Watch, "watch_fixture"),
            _ => Show()
        }))));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        List<string> opened = [];
        ai.OpenBrowserPage = (address, _) => opened.Add(address.AbsoluteUri);
        var result = await ai.RunBrowserRequestAsync("Запусти видео про кошек на YouTube", true, null, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal("Выполнено.", result.Message);
        Assert.Equal(new[] { Watch }, opened);
        Assert.Equal(2, browser.Calls);
    }

    [Fact]
    public async Task FailedDefaultBrowserHandoffDoesNotClaimCompletion()
    {
        using var root = new TestProfile();
        var requests = 0;
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ =>
            Task.FromResult(++requests == 1 ? Call() : Show()))));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(new Browser(true));
        ai.OpenBrowserPage = (_, _) => throw new System.ComponentModel.Win32Exception();
        var result = await ai.RunBrowserRequestAsync(Prompt, true, null, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Contains("браузере по умолчанию", result.Message);
    }

    [Fact]
    public async Task StoppingAfterObservationRejectsTheLateBrowserHandoff()
    {
        using var root = new TestProfile();
        using var stop = new CancellationTokenSource();
        var pendingAnswer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser(true);
        var requests = 0;
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ => ++requests == 1 ? Task.FromResult(Call()) : pendingAnswer.Task)));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        var opens = 0;
        ai.OpenBrowserPage = (_, _) => opens++;
        var pending = ai.RunBrowserRequestAsync(Prompt, true, null, stop.Token);
        Assert.Equal(1, browser.Calls);
        stop.Cancel();
        pendingAnswer.SetResult(Show());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        await browser.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task ResearchAnswerDoesNotOpenATab()
    {
        using var root = new TestProfile();
        var requests = 0;
        var browser = new Browser(true);
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ =>
            Task.FromResult(++requests == 1 ? Call() : AiLocalActionServiceTests.TextAnswer()))));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        ai.OpenBrowserPage = (_, _) => throw new Xunit.Sdk.XunitException("Research must not open the browser.");
        var result = await ai.RunBrowserRequestAsync("Research this topic", false, null, CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal("done", result.Message);
        Assert.Null(result.SourceUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelRequestOnlyHandsOffAChannelPage(bool channelFound)
    {
        using var root = new TestProfile();
        var browser = new Browser(true);
        var requests = 0;
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ => Task.FromResult(++requests switch
        {
            1 => Call(),
            2 when channelFound => Call(Channel, "channel_fixture"),
            _ => Show()
        }))));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        List<string> opened = [];
        ai.OpenBrowserPage = (address, _) => opened.Add(address.AbsoluteUri);
        var result = await ai.RunBrowserRequestAsync("Открой канал Фикстура", true, null, CancellationToken.None);
        Assert.Equal(channelFound, result.Verified);
        Assert.Equal(channelFound ? new[] { Channel } : Array.Empty<string>(), opened);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("disable")]
    public async Task LateModelOutputCannotStartBrowserAction(string reason)
    {
        using var root = new TestProfile();
        using var stop = new CancellationTokenSource();
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new Browser(true);
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ => late.Task)));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        var pending = ai.RunBrowserRequestAsync(Prompt, true, null, stop.Token);
        if (reason == "disable") ai.Save(ai.Configuration with { UseForUnknownCommands = false });
        else stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult(Call());
        await browser.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, browser.Calls);
    }

    [Theory]
    [InlineData("ollama", true, true)]
    [InlineData("chatgpt", false, true)]
    [InlineData("chatgpt", true, false)]
    public async Task DisabledOrUnsupportedBrowserRouteStartsNoProcessOrRequest(string provider, bool enabled, bool fallback)
    {
        using var root = new TestProfile();
        var requests = 0;
        var opens = 0;
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ => { requests++; return Task.FromResult(Call()); })));
        var connection = new AiConnection { Provider = provider, Name = "Fixture", Model = "fixture", Endpoint = "http://localhost:11434" };
        ai.Save(new() { Enabled = enabled, UseForUnknownCommands = fallback, ActiveConnectionId = connection.Id, Connections = [connection] });
        ai.OpenBrowserTools = (_, _) => { opens++; return Task.FromResult<IAiToolSession>(new Browser(true)); };
        Assert.False((await ai.RunBrowserRequestAsync(Prompt, true, null, CancellationToken.None)).Verified);
        Assert.Equal(0, opens);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ModelTextWithoutAToolCannotConfirmBrowserWork()
    {
        using var root = new TestProfile();
        var browser = new Browser(true);
        using var ai = new AiService(root.Path, new HttpClient(new Handler(_ => Task.FromResult(AiLocalActionServiceTests.TextAnswer()))));
        Configure(ai, root.Path);
        ai.OpenBrowserTools = (_, _) => Task.FromResult<IAiToolSession>(browser);
        var result = await ai.RunBrowserRequestAsync(Prompt, true, null, CancellationToken.None);
        Assert.False(result.Verified);
        Assert.Null(result.SourceUrl);
        Assert.Equal(0, browser.Calls);
    }

    [Fact]
    public async Task StoppingAStalledMcpCallClosesTheProcess()
    {
        const string server = """
            while ($line = [Console]::ReadLine()) {
                $msg = ConvertFrom-Json $line
                if ($msg.method -eq 'initialize') {
                    $result = @{ protocolVersion = '2025-06-18'; capabilities = @{ tools = @{} } }
                } elseif ($msg.method -eq 'tools/list') {
                    $result = @{ tools = @(@{ name = 'fixture'; processId = $PID }) }
                } elseif ($msg.method -eq 'tools/call') {
                    Start-Sleep -Seconds 20
                    break
                } else { continue }
                [Console]::WriteLine((@{ jsonrpc = '2.0'; id = $msg.id; result = $result } | ConvertTo-Json -Depth 6 -Compress))
            }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(server)) })
            start.ArgumentList.Add(argument);
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = await McpStdioClient.StartAsync(start, testDeadline.Token);
        var tools = await client.ListToolsAsync(testDeadline.Token);
        using var serverProcess = Process.GetProcessById(Assert.Single(tools).GetProperty("processId").GetInt32());
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var pending = client.CallAsync("fixture", JsonSerializer.SerializeToElement(new { }), stop.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
        await serverProcess.WaitForExitAsync(testDeadline.Token);
    }

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("http://127.0.0.1")]
    [InlineData("https://foo.localhost/results")]
    [InlineData("https://service.local/results")]
    [InlineData("https://www.youtube.com:444/results?search_query=cats")]
    [InlineData("https://secret@example.org/results")]
    public void BrowserAddressScopeExcludesPrivateAddressesAndCredentials(string url) =>
        Assert.False(PlaywrightBrowserTools.TryPublicPage(url, out _));

    [Theory]
    [InlineData("https://example.net/articles/current?format=compact")]
    [InlineData("https://www.weather.gov/")]
    [InlineData("http://example.com/")]
    public void PublicBrowserHasNoSiteAllowlist(string url) => Assert.True(PlaywrightBrowserTools.TryPublicPage(url, out _));

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("10.0.0.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("fc00::1", false)]
    public void PublicBrowserRejectsPrivateDnsDestinations(string ip, bool expected) =>
        Assert.Equal(expected, PlaywrightBrowserTools.IsPublicIp(IPAddress.Parse(ip)));

    [Theory]
    [InlineData(Url, "/watch?v=abcdefghijk", true)]
    [InlineData(Url, "/feed/subscriptions", false)]
    [InlineData("https://www.youtube.com/results?search_query=dogs", "/watch?v=abcdefghijk", false)]
    [InlineData("https://consent.youtube.com/", "/watch?v=abcdefghijk", false)]
    public void YoutubeSearchRequiresTheExpectedPageAndObservedVideoLinks(string actual, string link, bool expected)
    {
        var result = PlaywrightBrowserTools.Observe(new Uri(Url),
            $"### Page\n- Page URL: {actual}\n- Page Title: cats - YouTube\n### Snapshot\n```yaml\n- link \"Cat video\" [ref=e3]:\n  - /url: {link}\n```");
        Assert.Equal(expected, result.Verified);
        Assert.Equal(expected ? actual : null, result.SourceUrl);
    }

    [Fact]
    public void QuotedVideoTitlesArePreserved()
    {
        var result = PlaywrightBrowserTools.Observe(new Uri(Url), """
            ### Page
            - Page URL: https://www.youtube.com/results?search_query=cats
            - Page Title: cats - YouTube
            ### Snapshot
            - link "The \"Cat\" video" [ref=e3]:
              - /url: /watch?v=abcdefghijk
            """);
        Assert.True(result.Verified);
        using var data = JsonDocument.Parse(result.Data);
        Assert.Equal("The \"Cat\" video", data.RootElement.GetProperty("videos")[0].GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(Watch, "Cat video", true)]
    [InlineData(Watch, "Video unavailable", false)]
    [InlineData("https://www.youtube.com/watch?v=ABCDEFGHIJK", "Cat video", false)]
    public void WatchPageRequiresTheRequestedIdAndVideoHeading(string address, string heading, bool verified)
    {
        var result = PlaywrightBrowserTools.Observe(new Uri(Watch),
            $"### Page\n- Page URL: {address}\n- Page Title: Cat video - YouTube\n### Snapshot\n- heading \"{heading}\" [level=1] [ref=e1]");
        Assert.Equal(verified, result.Verified);
    }

    [Fact]
    public void YoutubeResultsReturnObservedChannelLinksWithoutRequiringVideos()
    {
        var result = PlaywrightBrowserTools.Observe(new Uri(Url), $"""
            ### Page
            - Page URL: {Url}
            - Page Title: cats - YouTube
            ### Snapshot
            - link "Fixture channel @FixtureChannel" [ref=e3]:
              - /url: /@FixtureChannel
            """);
        Assert.True(result.Verified);
        using var data = JsonDocument.Parse(result.Data);
        Assert.Equal(Channel, data.RootElement.GetProperty("channels")[0].GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(Channel, "@FixtureChannel", true)]
    [InlineData(Channel, "@wrong", false)]
    [InlineData("https://www.youtube.com/@wrong", "@wrong", false)]
    public void ChannelRequiresTheExpectedAddressAndObservedHeader(string address, string handle, bool verified)
    {
        var result = PlaywrightBrowserTools.Observe(new Uri(Channel), $"""
            ### Page
            - Page URL: {address}
            - Page Title: Fixture - YouTube
            ### Snapshot
            - heading "Fixture, verified" [level=1] [ref=e3]:
              - generic [ref=e4]: Fixture
              - generic [ref=e5]: "{handle}"
            """);
        Assert.Equal(verified, result.Verified);
    }

    [Fact]
    public void OptionalCookieRejectionOnlyMatchesTheExpectedChannel()
    {
        var redirect = Uri.EscapeDataString(Channel + "?cbrd=1");
        var snapshot = $"### Page\n- Page URL: https://consent.youtube.com/m?continue={redirect}\n### Snapshot\n- button \"Reject all\" [ref=e14]";
        Assert.True(PlaywrightBrowserTools.TryGetConsentReject(new Uri(Channel), snapshot, out var target));
        Assert.Equal("e14", target);
        Assert.False(PlaywrightBrowserTools.TryGetConsentReject(new Uri(Channel), snapshot.Replace("Reject all", "Accept all"), out _));
        Assert.False(PlaywrightBrowserTools.TryGetConsentReject(new Uri(Channel), snapshot.Replace("consent.youtube.com", "example.org"), out _));
        Assert.False(PlaywrightBrowserTools.TryGetConsentReject(new Uri("https://www.youtube.com/@different"), snapshot, out _));
    }

    private static void Configure(AiService ai, string root)
    {
        AiLocalActionServiceTests.Configure(ai, root);
        ai.Save(ai.Configuration with { AllowLocalActions = false, UseForUnknownCommands = true });
    }

    private static HttpResponseMessage Call(string url = Url, string id = "call_fixture") => new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new
    {
        type = "response.completed", response = new { status = "completed", output = new[] { new { type = "function_call", @namespace = "jarvis_tools",
            name = "browser_navigate", call_id = id, arguments = JsonSerializer.Serialize(new { url }), status = "completed" } } }
    }) + "\n\n") };

    private static HttpResponseMessage Show() => new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new
    {
        type = "response.completed", response = new { status = "completed", output = new[] { new { type = "function_call", @namespace = "jarvis_tools",
            name = "browser_show", call_id = "show_fixture", arguments = "{}", status = "completed" } } }
    }) + "\n\n") };

    private sealed class TestProfile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("jarvis-browser-test-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }

    private sealed class Browser(bool verified) : IAiToolSession
    {
        private string? _current;
        public int Calls { get; private set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed => Closed.Task.IsCompleted;
        public IReadOnlyList<AiToolDefinition> Tools { get; } = [new("browser_navigate", "Read a page", JsonSerializer.SerializeToElement(new { type = "object" })),
            new("browser_show", "Show a page", JsonSerializer.SerializeToElement(new { type = "object" }))];
        public Task<AiToolObservation> InvokeAsync(string name, JsonElement arguments, CancellationToken token)
        {
            if (name == "browser_show") return Task.FromResult(new AiToolObservation(true, "", "Выполнено.", _current, Complete: true));
            Assert.Equal("browser_navigate", name);
            var address = arguments.GetProperty("url").GetString();
            Assert.True(address is Url or Watch or Channel);
            _current = verified ? address : null;
            Calls++;
            return Task.FromResult(new AiToolObservation(verified, "observed page data", verified ? "observed title" : "connection error", verified ? address : null));
        }
        public ValueTask DisposeAsync() { Closed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
