using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Jarvis.App.Services;

public sealed class WorkerClient : IAsyncDisposable
{
    private Process? _process;
    private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private Task? _writer;
    private Task? _reader;
    private Task? _errors;
    public event Action<JsonElement>? Message;
    public event Action<string>? Failed;
    private bool _disposed;

    public void Start(string root)
    {
        var start = new ProcessStartInfo(Path.Combine(root, ".venv", "Scripts", "python.exe"))
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add("-m"); start.ArgumentList.Add("jarvis_speech.worker");
        start.Environment["PYTHONPATH"] = Path.Combine(root, "speech");
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["OMP_NUM_THREADS"] = "4";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить голосовой модуль.");
        _writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in _outgoing.Reader.ReadAllAsync(_stop.Token))
                { await _process.StandardInput.WriteLineAsync(line); await _process.StandardInput.FlushAsync(); }
            }
            catch (Exception ex) { if (!_disposed) Failed?.Invoke(ex.Message); }
        });
        _reader = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.Length > 4_000_000) throw new InvalidDataException("Слишком большой ответ голосового модуля.");
                    using var doc = JsonDocument.Parse(line);
                    Message?.Invoke(doc.RootElement.Clone());
                }
                if (!_disposed) Failed?.Invoke("Голосовой модуль завершился. Нажми «Перезапустить модуль».");
            }
            catch (Exception ex) { if (!_disposed) Failed?.Invoke(ex.Message); }
        });
        // Drain stderr even during model initialization. Never log microphone audio or transcripts to disk.
        _errors = Task.Run(async () =>
        {
            try { while (await _process.StandardError.ReadLineAsync(_stop.Token) is not null) { } }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public bool Send(object message) => !_disposed && _outgoing.Writer.TryWrite(JsonSerializer.Serialize(message));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _outgoing.Writer.TryComplete();
        _stop.Cancel();
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        try { await Task.WhenAll(new[] { _reader, _writer, _errors }.OfType<Task>()).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        _process?.Dispose();
        _stop.Dispose();
    }
}
