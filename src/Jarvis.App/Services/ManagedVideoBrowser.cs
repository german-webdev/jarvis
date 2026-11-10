using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Jarvis.Core;

namespace Jarvis.App.Services;

/// <summary>Own process, fresh profile, random loopback CDP endpoint. Never enumerates user browser tabs.</summary>
internal sealed class ManagedVideoBrowser : IVideoBrowserSession
{
    private readonly Process _process;
    private readonly string _profile;
    private readonly string _ownedRoot;
    private readonly string _ownedParent;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1);
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Func<string, string?>? _fixture;
    private Task? _reader;
    private int _nextId;
    internal Func<string, Task>? BeforeSendAsync { get; set; }
    private string? _session;
    private string? _target;
    private string? _mainFrame;
    private string? _expected;
    private volatile string? _problem;
    private readonly object _navigationGate = new();
    private CancellationTokenSource _navigation = new();
    private Task _stopping = Task.CompletedTask;
    private bool _disposed;
    public bool Alive => !_disposed && !_process.HasExited && _socket.State == WebSocketState.Open;
    public string? Problem => _problem;
    private ManagedVideoBrowser(Process process, string root, string parent, string profile, Func<string, string?>? fixture)
    { _process = process; _ownedRoot = root; _ownedParent = parent; _profile = profile; _fixture = fixture; }

    internal static string? FindBrowser() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
    }.FirstOrDefault(File.Exists);

    public static Task<IVideoBrowserSession> LaunchAsync(string root, CancellationToken token) => LaunchAsync(root, null, token);
    // Test-only dependency: fulfill document requests with local HTML. No public endpoint/profile/port injection.
    internal static async Task<IVideoBrowserSession> LaunchAsync(string root, Func<string, string?>? fixture, CancellationToken token)
    {
        var exe = FindBrowser() ?? throw new InvalidDataException("Нужен установленный Microsoft Edge или Google Chrome. Установите браузер самостоятельно.");
        root = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(root, "work", "browser-sessions"));
        var profile = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        ValidateOwnedProfile(root, parent, profile);
        Directory.CreateDirectory(profile);
        ValidateOwnedProfile(root, parent, profile);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = fixture != null, WindowStyle = fixture != null ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
        foreach (var arg in new[] { "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "--user-data-dir=" + profile,
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-extensions", "--disable-background-networking",
            "--autoplay-policy=user-gesture-required", "--disable-component-update" }) start.ArgumentList.Add(arg);
        if (fixture != null)
        { start.ArgumentList.Add("--headless=new"); start.ArgumentList.Add("--host-resolver-rules=MAP * ~NOTFOUND"); start.ArgumentList.Add("--disable-gpu"); start.ArgumentList.Add("--disable-popup-blocking"); }
        start.ArgumentList.Add("about:blank");
        token.ThrowIfCancellationRequested();
        var process = Process.Start(start) ?? throw new IOException("Browser did not start.");
        var browser = new ManagedVideoBrowser(process, root, parent, profile, fixture);
        try
        {
            var marker = Path.Combine(profile, "DevToolsActivePort");
            string[] lines = [];
            while (lines.Length < 2)
            {
                token.ThrowIfCancellationRequested(); if (process.HasExited) throw new IOException("Browser closed.");
                if (File.Exists(marker)) { try { lines = await File.ReadAllLinesAsync(marker, token); } catch (IOException) { } }
                if (lines.Length < 2) await Task.Delay(100, token);
            }
            if (!int.TryParse(lines[0], out var port) || port is < 1 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(lines[1], @"^/devtools/browser/[A-Za-z0-9-]+$")) throw new IOException("Invalid browser endpoint.");
            // Endpoint must belong to the process we just created, and must not listen on external interfaces.
            if (!OwnsLoopbackPort(process.Id, port)) throw new IOException("Browser port ownership could not be verified.");
            await browser._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), token);
            browser._reader = browser.ReadLoopAsync();
            var targets = await browser.SendAsync("Target.getTargets", null, null, token);
            var pages = targets.GetProperty("targetInfos").EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page").ToArray();
            if (pages.Length != 1 || pages[0].GetProperty("url").GetString() != "about:blank") throw new IOException("Unexpected browser target.");
            browser._target = pages[0].GetProperty("targetId").GetString()!;
            var attach = await browser.SendAsync("Target.attachToTarget", new { targetId = browser._target, flatten = true }, null, token);
            browser._session = attach.GetProperty("sessionId").GetString()!;
            await browser.SendAsync("Browser.setDownloadBehavior", new { behavior = "deny" }, null, token);
            await browser.SendAsync("Target.setDiscoverTargets", new { discover = true }, null, token);
            await browser.CallAsync("Page.enable", null, token);
            var frameTree = await browser.CallAsync("Page.getFrameTree", null, token);
            browser._mainFrame = frameTree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            await browser.CallAsync("Fetch.enable", new { patterns = fixture == null ? new object[] { new { resourceType = "Document", requestStage = "Request" } } : [new { urlPattern = "*", requestStage = "Request" }] }, token);
            token.ThrowIfCancellationRequested(); return browser;
        }
        catch { await browser.DisposeAsync(); throw; }
    }

    public void ExpectNavigation(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "www.youtube.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !(uri.AbsolutePath == "/results" || BrowserVideoCommands.TryVideoUrl(url, out _))) throw new InvalidDataException("Недопустимый адрес видео.");
        lock (_navigationGate)
        {
            _navigation.Cancel(); _navigation = new();
            _expected = url;
        }
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken token)
    {
        if (method == "Page.navigate")
        {
            Task stopping; CancellationToken navigation;
            lock (_navigationGate) { stopping = _stopping; navigation = _navigation.Token; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, navigation);
            await stopping.WaitAsync(linked.Token);
            return await SendAsync(method, parameters, _session, linked.Token);
        }
        return await SendAsync(method, parameters, _session, token);
    }
    private async Task<JsonElement> SendAsync(string method, object? parameters, string? session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } };
            if (session != null) message["sessionId"] = session;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            if (BeforeSendAsync != null) await BeforeSendAsync(method);
            await _send.WaitAsync(token);
            try { token.ThrowIfCancellationRequested(); await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
            finally { _send.Release(); }
            return await tcs.Task.WaitAsync(token);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task ReadLoopAsync()
    {
        var buffer = new byte[16384];
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                using var data = new MemoryStream(); WebSocketReceiveResult chunk;
                do
                {
                    chunk = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _closed.Token);
                    if (chunk.MessageType == WebSocketMessageType.Close) throw new IOException();
                    data.Write(buffer, 0, chunk.Count);
                    if (data.Length > 2 * 1024 * 1024) throw new IOException("Browser response too large.");
                } while (!chunk.EndOfMessage);
                using var document = JsonDocument.Parse(data.ToArray()); var message = document.RootElement;
                if (message.TryGetProperty("id", out var id) && _pending.TryGetValue(id.GetInt32(), out var pending))
                {
                    if (message.TryGetProperty("error", out _)) pending.TrySetException(new InvalidOperationException("Browser command failed."));
                    else pending.TrySetResult(message.GetProperty("result").Clone());
                }
                else if (message.TryGetProperty("method", out var method))
                    _ = HandleEventAsync(method.GetString()!, message.Clone()); // Handler owns data; reader must keep receiving command replies.
            }
        }
        catch (Exception ex) { foreach (var pending in _pending.Values) pending.TrySetException(ex); }
        finally { _problem ??= "Связь с браузером потеряна. Откройте управляемый браузер заново."; _socket.Abort(); }
    }
    private async Task HandleEventAsync(string method, JsonElement message)
    {
        try
        {
            var data = message.GetProperty("params");
            if ((method == "Target.targetDestroyed" || method == "Target.targetCrashed")
                && data.TryGetProperty("targetId", out var closedTarget) && closedTarget.GetString() == _target)
            { MarkProblem("Связь с браузером потеряна. Откройте управляемый браузер заново."); return; }
            if (method == "Target.detachedFromTarget" && data.TryGetProperty("sessionId", out var detached) && detached.GetString() == _session)
            { MarkProblem("Связь с браузером потеряна. Откройте управляемый браузер заново."); return; }
            if (method == "Target.targetCreated" && _target != null)
            {
                var target = data.GetProperty("targetInfo");
                if (target.GetProperty("type").GetString() == "page" && target.GetProperty("targetId").GetString() != _target)
                    MarkProblem("Открылась другая вкладка. Действие остановлено. Закройте управляемый браузер и подключите его заново.");
                return;
            }
            if (!message.TryGetProperty("sessionId", out var session) || session.GetString() != _session) return;
            if (method == "Page.windowOpen") { MarkProblem("Сайт попытался открыть другую вкладку. Действие остановлено."); return; }
            if (method == "Page.javascriptDialogOpening") { MarkProblem("Браузер показывает диалог. Решите это самостоятельно и повторите поиск."); return; }
            if (method != "Fetch.requestPaused") return;
            var requestId = data.GetProperty("requestId").GetString();
            var url = data.GetProperty("request").GetProperty("url").GetString()!;
            bool document = data.GetProperty("resourceType").GetString() == "Document" && data.GetProperty("frameId").GetString() == _mainFrame;
            bool allowed; CancellationToken navigation;
            lock (_navigationGate)
            { allowed = document && _expected != null && url == _expected && _problem == null; navigation = _navigation.Token; }
            using var allowedLifetime = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token, navigation);
            if (allowed && _fixture != null)
            {
                var html = _fixture(url);
                if (html != null)
                { await CallAsync("Fetch.fulfillRequest", new { requestId, responseCode = 200, responseHeaders = new[] { new { name = "Content-Type", value = "text/html; charset=utf-8" } }, body = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)) }, allowedLifetime.Token); return; }
                allowed = false;
            }
            if (allowed) await CallAsync("Fetch.continueRequest", new { requestId }, allowedLifetime.Token);
            else
            {
                if (document) MarkProblem(url.StartsWith("https://consent.", StringComparison.Ordinal)
                    ? "Сайт требует согласие или вход. Автоматическое действие остановлено. Откройте видео самостоятельно в обычном браузере."
                    : "Переход на другой адрес заблокирован. Повторите поиск в управляемой вкладке.");
                await CallAsync("Fetch.failRequest", new { requestId, errorReason = "BlockedByClient" }, _closed.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { if (!_disposed) _problem ??= "Связь с браузером потеряна. Откройте управляемый браузер заново."; }
    }
    private void MarkProblem(string message) { _problem = message; Stop(); }
    public void Stop()
    {
        lock (_navigationGate)
        {
            _expected = null; _navigation.Cancel();
            if (Alive) _stopping = StopLoadingAsync();
        }
    }
    private async Task StopLoadingAsync()
    { try { using var timeout = new CancellationTokenSource(1500); await CallAsync("Page.stopLoading", null, timeout.Token); } catch { } }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        Stop(); _closed.Cancel(); _socket.Abort();
        if (_reader != null) { try { await _reader; } catch { } }
        try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
        _process.Dispose(); _socket.Dispose();
        // Only this randomly named profile, beneath the exact owned session parent, can be removed.
        try
        {
            ValidateOwnedProfile(_ownedRoot, _ownedParent, _profile);
            // Reject links anywhere inside the temporary profile before a recursive operation.
            var pending = new Stack<string>(); pending.Push(_profile);
            while (pending.TryPop(out var folder))
                foreach (var item in Directory.EnumerateFileSystemEntries(folder))
                {
                    var attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Profile contains a link; cleanup skipped.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
                }
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static void ValidateOwnedProfile(string root, string parent, string profile)
    {
        var full = Path.GetFullPath(profile);
        if (!string.Equals(Path.GetFullPath(parent), Path.Combine(Path.GetFullPath(root), "work", "browser-sessions"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new IOException("Profile is outside the owned browser directory.");
        for (var cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
        {
            if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Browser profile path contains a link.");
            if (string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new IOException("Profile root was not reached.");
    }

    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, uint reserved);
    private static bool OwnsLoopbackPort(int pid, int port)
    {
        int size = 0; GetExtendedTcpTable(0, ref size, false, 2, 3, 0); // IPv4, TCP_TABLE_OWNER_PID_LISTENER
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) return false;
            bool found = false;
            for (int i = 0, count = Marshal.ReadInt32(buffer); i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                var owner = Marshal.ReadInt32(row, 20);
                var actualPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                if (owner != pid || actualPort != port) continue;
                var address = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                if (!IPAddress.IsLoopback(address)) return false;
                found = true;
            }
            if (!found) return false;
            int size6 = 0; GetExtendedTcpTable(0, ref size6, false, 23, 3, 0);
            var buffer6 = Marshal.AllocHGlobal(size6);
            try
            {
                if (GetExtendedTcpTable(buffer6, ref size6, false, 23, 3, 0) != 0) return false;
                for (int i = 0, count = Marshal.ReadInt32(buffer6); i < count; i++)
                {
                    var row = buffer6 + 4 + i * 56;
                    if (Marshal.ReadInt32(row, 52) != pid || (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 20)) != port) continue;
                    var bytes = new byte[16]; Marshal.Copy(row, bytes, 0, 16);
                    if (!IPAddress.IsLoopback(new IPAddress(bytes))) return false;
                }
            }
            finally { Marshal.FreeHGlobal(buffer6); }
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
