using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core;

namespace Jarvis.App.Services;

/// <summary>Official CLI bridges. Each saved profile owns its own login directory.</summary>
public sealed class SubscriptionClient(string sessionRoot)
{
    public static string? FindExecutable(string provider)
    {
        var name = provider == "chatgpt" ? "codex.exe" : "claude.exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var path = Path.Combine(directory.Trim('"'), name); if (File.Exists(path)) return Path.GetFullPath(path); }
            catch (ArgumentException) { }
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name);
        return File.Exists(local) ? local : null;
    }

    public ProcessStartInfo CreateStart(AiConnection connection, params string[] arguments)
    {
        connection.Validate();
        var exe = string.IsNullOrWhiteSpace(connection.CliPath) ? FindExecutable(connection.Provider) : connection.CliPath;
        if (exe == null || !File.Exists(exe)) throw new InvalidDataException("Компонент входа Claude Code не установлен. Установи его с официального сайта Claude; JARVIS найдёт его автоматически.");
        var home = Path.GetFullPath(Path.Combine(sessionRoot, connection.Id));
        if (!home.StartsWith(Path.GetFullPath(sessionRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Неверный каталог подключения.");
        var workspace = Path.Combine(home, "workspace");
        Directory.CreateDirectory(workspace);
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)
            || k is "CODEX_API_KEY" or "CODEX_ACCESS_TOKEN").ToArray()) start.Environment.Remove(key);
        // Only the child process sees this isolated configuration directory.
        start.Environment[connection.Provider == "chatgpt" ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR"] = home;
        if (connection.Provider == "chatgpt")
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
        }
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return start;
    }

    public async Task<bool> IsSignedInAsync(AiConnection connection, CancellationToken token)
    {
        var result = await RunAsync(CreateStart(connection, connection.Provider == "chatgpt" ? ["login", "status"] : ["auth", "status", "--json"]), null, token);
        if (result.Code != 0) return false;
        if (connection.Provider == "chatgpt") return (result.Output + result.Error).Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var data = JsonDocument.Parse(result.Output);
            return data.RootElement.TryGetProperty("authMethod", out var method) && method.GetString() == "claude.ai"
                && (!data.RootElement.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }

    public async Task SignInAsync(AiConnection connection, CancellationToken token, Action<Uri>? browserLink = null)
    {
        var result = await RunAsync(CreateStart(connection, connection.Provider == "chatgpt" ? ["login"] : ["auth", "login"]), null, token, browserLink);
        if (result.Code != 0 || !await IsSignedInAsync(connection, token))
            throw new InvalidDataException("Вход по подписке не завершён. Проверь вход в браузере и доступность официального клиента.");
    }

    public async Task SignOutAsync(AiConnection connection, CancellationToken token)
    {
        var result = await RunAsync(CreateStart(connection, connection.Provider == "chatgpt" ? ["logout"] : ["auth", "logout"]), null, token);
        if (result.Code != 0) throw new InvalidDataException("Не удалось выйти из аккаунта этого подключения.");
    }

    public static string[] AnswerArguments(AiConnection connection)
    {
        List<string> args;
        if (connection.Provider == "chatgpt")
        {
            args = ["exec", "--ignore-user-config", "--ignore-rules", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only", "--json",
                "-c", "approval_policy=\"never\"", "-c", "web_search=\"disabled\"", "-c", "developer_instructions=" + JsonSerializer.Serialize(HttpAiClient.SystemPrompt)];
            foreach (var feature in new[] { "shell_tool", "unified_exec", "js_repl", "code_mode", "apps", "multi_agent", "view_image", "apply_patch_freeform", "tool_search", "tool_suggest", "request_permissions" })
            { args.Add("-c"); args.Add("features." + feature + "=false"); }
            args.Add("-c"); args.Add("features.skip_host_skill_discovery=true");
        }
        else
            args = ["-p", "--output-format", "json", "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--setting-sources", "",
                "--no-session-persistence", "--no-chrome", "--disable-slash-commands", "--system-prompt", HttpAiClient.SystemPrompt];
        if (!string.IsNullOrWhiteSpace(connection.Model)) { args.Add("--model"); args.Add(connection.Model); }
        if (connection.Provider == "chatgpt") args.Add("-");
        return args.ToArray();
    }

    public async Task<string> AskAsync(AiConnection connection, string question, CancellationToken token)
    {
        if (question.Length is 0 or > 4000) throw new InvalidDataException("Вопрос должен содержать от 1 до 4000 символов.");
        if (!await IsSignedInAsync(connection, token)) throw new InvalidDataException("Сначала выполни вход по подписке в настройках этого подключения.");
        var result = await RunAsync(CreateStart(connection, AnswerArguments(connection)), question, token);
        if (result.Code != 0) throw new InvalidDataException("Официальный клиент не вернул ответ. Проверь вход, лимиты подписки и выбранную модель.");
        return ReadAnswer(connection.Provider, result.Output);
    }

    public static string ReadAnswer(string provider, string output)
    {
        if (provider == "claude")
        {
            using var data = JsonDocument.Parse(output);
            if (data.RootElement.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
                throw new InvalidDataException("Официальный клиент сообщил об ошибке запроса.");
            return HttpAiClient.CleanAnswer(data.RootElement.GetProperty("result").GetString());
        }
        string? answer = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var data = JsonDocument.Parse(line);
            var root = data.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "item.completed"
                && root.TryGetProperty("item", out var item) && item.GetProperty("type").GetString() == "agent_message")
                answer = item.GetProperty("text").GetString();
        }
        return HttpAiClient.CleanAnswer(answer);
    }

    public static Uri? AuthorizationLink(string line)
    {
        var clean = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        foreach (Match match in Regex.Matches(clean, @"https://[^\s<>""']+"))
            if (Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) && uri.IsDefaultPort && uri.UserInfo.Length == 0
                && uri.Host is "claude.ai" or "console.anthropic.com" or "platform.claude.com" or "auth.claude.com"
                && uri.AbsolutePath.Contains("authorize", StringComparison.Ordinal)) return uri;
        return null;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(ProcessStartInfo start, string? input, CancellationToken token, Action<Uri>? browserLink = null)
    {
        using var process = Process.Start(start) ?? throw new InvalidDataException("Не удалось запустить официальный клиент.");
        using var registration = token.Register(() => StopProcess(process));
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, token, browserLink);
            var error = ReadBoundedAsync(process.StandardError, token, browserLink);
            if (input != null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
            if (browserLink == null) process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await output, await error);
        }
        finally { StopProcess(process); }
    }

    private static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token, Action<Uri>? browserLink)
    {
        var result = new StringBuilder();
        var block = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(block, token)) > 0)
        {
            // Drain the pipe even if a verbose client exceeds the retained output limit.
            if (result.Length + count <= 2_000_000) result.Append(block, 0, count);
            if (browserLink != null)
                foreach (var c in block.Take(count))
                {
                    if (c == '\n')
                    {
                        var uri = AuthorizationLink(line.ToString()); line.Clear();
                        if (uri != null) browserLink(uri);
                    }
                    else if (line.Length < 32768) line.Append(c);
                }
        }
        return result.ToString();
    }
}
