using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

/// <summary>One private, sequential MCP session over newline-delimited JSON-RPC.</summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    private const string Protocol = "2025-06-18";
    private const int MaxMessageLength = 1_000_000;
    private readonly Process _process;
    private readonly char[] _buffer = new char[4096];
    private int _position, _available, _nextId;

    private McpStdioClient(Process process) => _process = process;

    public static async Task<McpStdioClient> StartAsync(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardInputEncoding = start.StandardOutputEncoding = new UTF8Encoding(false);
        start.CreateNoWindow = true;
        var process = Process.Start(start) ?? throw new IOException("MCP process did not start.");
        var client = new McpStdioClient(process);
        try
        {
            process.BeginErrorReadLine();
            var result = await client.RequestAsync("initialize", new { protocolVersion = Protocol,
                capabilities = new { }, clientInfo = new { name = "JARVIS", version = "1" } }, token).ConfigureAwait(false);
            if (result.GetProperty("protocolVersion").GetString() is not (Protocol or "2025-03-26" or "2024-11-05")
                || !result.GetProperty("capabilities").TryGetProperty("tools", out _))
                throw new InvalidDataException("Unsupported MCP server capabilities.");
            await client.SendAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement[]> ListToolsAsync(CancellationToken token)
    {
        List<JsonElement> tools = [];
        string? cursor = null;
        for (var page = 0; page < 4; page++)
        {
            var response = await RequestAsync("tools/list", cursor == null ? new { } : (object)new { cursor }, token).ConfigureAwait(false);
            tools.AddRange(response.GetProperty("tools").EnumerateArray());
            if (!response.TryGetProperty("nextCursor", out var next) || string.IsNullOrEmpty(cursor = next.GetString()))
                return tools.ToArray();
        }
        throw new InvalidDataException("MCP tool list exceeds the supported size.");
    }

    public Task<JsonElement> CallAsync(string name, JsonElement arguments, CancellationToken token) =>
        RequestAsync("tools/call", new { name, arguments }, token);

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var stop = deadline.Token;
        var id = ++_nextId;
        try
        {
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, stop).ConfigureAwait(false);
            for (var count = 0; count < 64; count++)
            {
                using var message = JsonDocument.Parse(await ReadMessageAsync(stop).ConfigureAwait(false));
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var responseId)) continue; // Notifications carry no ID.
                if (root.TryGetProperty("method", out var serverMethod))
                {
                    if (serverMethod.GetString() == "ping")
                        await SendAsync(new { jsonrpc = "2.0", id = responseId, result = new { } }, stop).ConfigureAwait(false);
                    else
                        await SendAsync(new { jsonrpc = "2.0", id = responseId, error = new { code = -32601, message = "Method not supported" } }, stop).ConfigureAwait(false);
                    continue;
                }
                if (responseId.ValueKind != JsonValueKind.Number || responseId.GetInt32() != id)
                    throw new InvalidDataException("Unexpected MCP response ID.");
                if (root.TryGetProperty("error", out _)) throw new InvalidDataException("MCP request failed.");
                stop.ThrowIfCancellationRequested();
                return root.GetProperty("result").Clone();
            }
            throw new InvalidDataException("Too many MCP messages without a response.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The session is abandoned after cancellation; terminate the browser and MCP as one tree.
            KillTree();
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("MCP request timed out.");
        }
    }

    private async Task SendAsync(object message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task<string> ReadMessageAsync(CancellationToken token)
    {
        var message = new StringBuilder();
        while (true)
        {
            if (_position == _available)
            {
                _available = await _process.StandardOutput.ReadAsync(_buffer, token).ConfigureAwait(false);
                _position = 0;
                if (_available == 0) throw new EndOfStreamException("MCP session closed.");
            }
            var newline = Array.IndexOf(_buffer, '\n', _position, _available - _position);
            var end = newline < 0 ? _available : newline;
            if (message.Length + end - _position > MaxMessageLength) throw new InvalidDataException("MCP response is too large.");
            message.Append(_buffer, _position, end - _position);
            _position = newline < 0 ? end : end + 1;
            if (newline >= 0) return message.ToString();
        }
    }

    private void KillTree()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { KillTree(); }
        finally { _process.Dispose(); }
    }
}
