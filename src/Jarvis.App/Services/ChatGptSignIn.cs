using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App.Services;

public sealed class ChatGptSignIn(string root, ChatGptPlanClient client)
{
    private readonly SemaphoreSlim _gate = new(1);
    private string AccountPath(AiConnection connection)
    {
        connection.Validate();
        return Path.Combine(root, connection.Id + ".protected");
    }
    public ChatGptAccount? Read(AiConnection connection)
    {
        var path = AccountPath(connection);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ChatGptAccount>(AiService.Unprotect(File.ReadAllText(path))); }
        catch { throw new InvalidDataException("Не удалось прочитать вход ChatGPT. Выполни вход заново."); }
    }
    private void Save(AiConnection connection, ChatGptAccount account)
    {
        Directory.CreateDirectory(root);
        var path = AccountPath(connection);
        File.WriteAllText(path + ".tmp", AiService.Protect(JsonSerializer.Serialize(account)));
        File.Move(path + ".tmp", path, overwrite: true);
    }
    private string HostId()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "host-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (existing.StartsWith("urn:uuid:") && Guid.TryParse(existing[9..], out _)) return existing;
            throw new InvalidDataException("Не удалось прочитать идентификатор установки JARVIS.");
        }
        var id = "urn:uuid:" + Guid.NewGuid();
        File.WriteAllText(path + ".tmp", id); File.Move(path + ".tmp", path);
        return id;
    }
    public static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    public async Task SignInAsync(AiConnection connection, Action<Uri> openBrowser, IProgress<string>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var previous = Read(connection);
            using var callback = new OAuthLoopback();
            var attempt = OAuthAttempt.Create(callback.RedirectUri, previous?.ClientId);
            // The listener is already bound before the browser opens.
            var uri = ChatGptPlanClient.AuthorizeUri(attempt, HostId());
            progress?.Report("Открываю браузер для входа в ChatGPT…");
            openBrowser(uri);
            progress?.Report("Подтверди вход в браузере. JARVIS ждёт возвращения…");
            var query = await callback.ReceiveAsync(attempt.State, token);
            progress?.Report("Проверяю подтверждение аккаунта ChatGPT…");
            var account = await client.ExchangeAsync(attempt, query, previous, token);
            Save(connection, account);
        }
        finally { _gate.Release(); }
    }
    public async Task<ChatGptAccount> ReadyAsync(AiConnection connection, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var account = Read(connection);
            if (account == null || !account.SignedIn) throw new InvalidDataException("Сначала войди в ChatGPT через браузер.");
            if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                if (account.RefreshToken.Length == 0) throw new InvalidDataException("Вход ChatGPT истёк. Выполни вход заново.");
                account = await client.RefreshAsync(account, token); Save(connection, account);
            }
            return account;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> SignOutAsync(AiConnection connection, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var account = Read(connection); if (account == null) return true;
            var revoked = false;
            try { await client.RevokeAsync(account, token); revoked = true; }
            catch (Exception) { /* Clear locally even if remote revocation cannot be confirmed. */ }
            Save(connection, account with { AccessToken = "", RefreshToken = "", IdToken = "", Scope = "", ExpiresAt = default });
            return revoked;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Short-lived IPv4 loopback callback. Never binds a public interface or requires URL ACLs.</summary>
public sealed class OAuthLoopback : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    public string RedirectUri { get; }
    public OAuthLoopback()
    {
        _listener.Start();
        RedirectUri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/auth/callback";
    }
    public async Task<string> ReceiveAsync(string expectedState, CancellationToken token)
    {
        while (true)
        {
            using var socket = await _listener.AcceptTcpClientAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = socket.GetStream();
            try
            {
                var request = new List<byte>(); var block = new byte[1024];
                while (request.Count < 16384)
                {
                    var count = await stream.ReadAsync(block, timeout.Token); if (count == 0) break;
                    request.AddRange(block.Take(count));
                    if (Encoding.ASCII.GetString(request.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var lines = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n");
                var parts = lines[0].Split(' ');
                var host = new Uri(RedirectUri).Authority;
                Uri? uri = null;
                var valid = request.Count < 16384 && parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/auth/callback?")
                    && lines.Any(l => l.Equals("Host: " + host, StringComparison.OrdinalIgnoreCase))
                    && Uri.TryCreate("http://" + host + parts[1], UriKind.Absolute, out uri);
                if (valid)
                {
                    var values = ChatGptPlanClient.ParseQuery(uri!.Query);
                    valid = values.GetValueOrDefault("state") == expectedState;
                }
                var body = valid ? "JARVIS received the sign-in response. Return to JARVIS to see the result. You may close this tab." : "This callback does not belong to the pending JARVIS sign-in.";
                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {bytes.Length}\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token); await stream.WriteAsync(bytes, timeout.Token);
                if (valid) return uri!.Query;
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is IOException or OperationCanceledException or InvalidDataException or UriFormatException) { }
        }
    }
    public void Dispose() => _listener.Stop();
}
