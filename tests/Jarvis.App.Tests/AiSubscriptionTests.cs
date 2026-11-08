using System.IO;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public class AiSubscriptionTests
{
    [Fact]
    public void ApiKeyIsStoredAsCiphertextAndCanBeRecoveredByCurrentUser()
    {
        const string fixture = "not-a-real-api-key";
        var ciphertext = AiService.Protect(fixture);
        Assert.DoesNotContain(fixture, ciphertext);
        Assert.Equal(fixture, AiService.Unprotect(ciphertext));
    }

    [Fact]
    public void OfficialClientArgumentsDoNotBypassPermissions()
    {
        var codex = SubscriptionClient.AnswerArguments(new() { Provider = "chatgpt" });
        Assert.Contains("read-only", codex);
        Assert.Contains("features.shell_tool=false", codex);
        Assert.Contains("features.unified_exec=false", codex);
        Assert.Contains("features.js_repl=false", codex);
        Assert.DoesNotContain(codex, x => x.Contains("bypass", StringComparison.Ordinal));
        var claude = SubscriptionClient.AnswerArguments(new() { Provider = "claude" });
        Assert.Equal("", claude[Array.IndexOf(claude, "--tools") + 1]);
        Assert.Contains("--strict-mcp-config", claude);
        Assert.Contains("--no-session-persistence", claude);
    }

    [Fact]
    public void OnlyFinalAssistantMessagesAreReturned()
    {
        Assert.Equal("answer", SubscriptionClient.ReadAnswer("chatgpt", "{\"type\":\"thread.started\"}\n{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"answer\"}}"));
        Assert.Equal("answer", SubscriptionClient.ReadAnswer("claude", "{\"is_error\":false,\"result\":\"answer\"}"));
        Assert.Throws<InvalidDataException>(() => SubscriptionClient.ReadAnswer("claude", "{\"is_error\":true,\"result\":\"private error details\"}"));
    }

    [Fact]
    public void SubscriptionProcessHasItsOwnHomeAndDoesNotUseGlobalApiCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-ai-process-" + Guid.NewGuid());
        var profile = new AiConnection { Name = "test", Provider = "chatgpt", CliPath = Environment.ProcessPath! };
        try
        {
            var start = new SubscriptionClient(root).CreateStart(profile, "login", "status");
            Assert.Equal(Path.Combine(root, profile.Id), start.Environment["CODEX_HOME"]);
            Assert.False(start.Environment.ContainsKey("OPENAI_API_KEY"));
            Assert.False(start.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
            Assert.Equal(new[] { "-c", "cli_auth_credentials_store=\"file\"", "login", "status" }, start.ArgumentList);
        }
        finally
        {
            Directory.Delete(Path.Combine(root, profile.Id, "workspace"));
            Directory.Delete(Path.Combine(root, profile.Id));
            Directory.Delete(root);
        }
    }
}
