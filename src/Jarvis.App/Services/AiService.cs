using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Core;

namespace Jarvis.App.Services;

public sealed class AiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private long _revision;
    public long ConnectionRevision => Interlocked.Read(ref _revision);
    public event Action? ConnectionChanged;
    public AiConnectionStore Store { get; }
    public SubscriptionClient Subscriptions { get; }
    public ChatGptPlanClient ChatGptClient { get; }
    public ChatGptSignIn ChatGpt { get; }
    public AiConfiguration Configuration { get; private set; }
    public string? LoadWarning { get; }

    public AiService(string root, HttpClient? http = null)
    {
        _http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(100) };
        Store = new(Path.Combine(root, "data", "ai-connections.json"));
        (Configuration, LoadWarning) = Store.Load();
        Subscriptions = new(Path.Combine(root, "data", "ai-sessions"));
        ChatGptClient = new(_http);
        ChatGpt = new(Path.Combine(root, "data", "chatgpt-accounts"), ChatGptClient);
        ChatGpt.AccountChanging += OnAccountChanging;
        Subscriptions.AccountChanging += OnAccountChanging;
    }

    private void OnAccountChanging(AiConnection connection)
    {
        lock (_gate)
            if (Configuration.Active is { } active && active.Id == connection.Id && active.Provider == connection.Provider)
                InvalidateConnection();
    }

    private static string AccountScope(ChatGptAccount? account) => account == null ? "" :
        System.Text.Json.JsonSerializer.Serialize(new { account.Subject, account.ClientId,
            Scope = account.Scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });

    private static string ConnectionScope(AiConnection? connection) => connection == null ? "disabled" :
        System.Text.Json.JsonSerializer.Serialize(new { connection.Id, connection.Provider, connection.Endpoint,
            connection.Model, connection.CliPath, connection.ProtectedApiKey });

    public void Save(AiConfiguration settings)
    {
        lock (_gate)
        {
            var changed = ConnectionScope(Configuration.Active) != ConnectionScope(settings.Active)
                || Configuration.AllowLocalActions != settings.AllowLocalActions;
            Store.Save(settings); Configuration = settings;
            if (changed) InvalidateConnection();
        }
    }

    private void InvalidateConnection()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _revision);
            _pending?.Cancel();
            ConnectionChanged?.Invoke();
        }
    }
    public async Task<string[]> ModelsAsync(AiConnection connection, CancellationToken token) => connection.Provider == "chatgpt"
        ? await ChatGptClient.ModelsAsync(await ChatGpt.ReadyAsync(connection, token), token)
        : await new HttpAiClient(_http).ModelsAsync(connection, Unprotect(connection.ProtectedApiKey), token);

    public Task<SkillResult> AskAsync(string question, CancellationToken token) => AskCoreAsync(question, null, null, token);

    public Task<SkillResult> RunTaskAsync(string question, LocalActionExecutor executor, Action<LocalActionProgress>? progress, CancellationToken token) =>
        AskCoreAsync(question, executor, progress, token);

    private async Task<SkillResult> AskCoreAsync(string question, LocalActionExecutor? executor, Action<LocalActionProgress>? progress, CancellationToken token)
    {
        var revisionAtStart = ConnectionRevision;
        Task<string> request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(executor == null ? TimeSpan.FromSeconds(110) : AiLocalActions.TimeLimit);
        try
        {
            lock (_gate)
            {
                revisionAtStart = ConnectionRevision;
                var selected = Configuration.Active;
                if (selected == null) return new(false, "Нейросеть выключена. Добавь и включи подключение в меню «JARVIS → Нейросети».");
                if (executor != null && !Configuration.AllowLocalActions) return new(false, "Действия ИИ выключены. Разреши их в настройках подключений ИИ.");
                if (executor != null && selected.Provider != "chatgpt") return new(false, "Это подключение поддерживает разговор, но не локальные действия. Для задач выбери ChatGPT.");
                var account = selected.Provider == "chatgpt" ? ChatGpt.Read(selected) : null;
                AiMessage[] messages = [new("user", question)];
                AiRequest.ValidateInput(messages);
                _pending?.Cancel();
                _pending = timeout;
                request = SendAsync();
                async Task<string> SendAsync()
                {
                    var cancellation = timeout.Token;
                    if (selected.Provider == "chatgpt")
                    {
                        var ready = await ChatGpt.ReadyAsync(selected, cancellation);
                        lock (_gate)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (revisionAtStart != ConnectionRevision) throw new OperationCanceledException(cancellation);
                            if (AccountScope(account) != AccountScope(ready))
                            {
                                // Reject a refresh that changed the authorized account scope.
                                InvalidateConnection();
                                throw new OperationCanceledException(cancellation);
                            }
                        }
                        if (executor != null)
                            return await AiLocalActions.RunAsync(messages, executor,
                                (turn, stop) => ChatGptClient.ActionTurnAsync(ready, selected.Model, turn, stop), progress, cancellation);
                        return await ChatGptClient.AskAsync(ready, selected.Model, messages, cancellation);
                    }
                    return selected.IsSubscription
                        ? await Subscriptions.AskAsync(selected, messages, cancellation)
                        : await new HttpAiClient(_http).AskAsync(selected, Unprotect(selected.ProtectedApiKey), messages, cancellation);
                }
            }
            var answer = HttpAiClient.CleanAnswer(await request.WaitAsync(timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
            if (revisionAtStart != ConnectionRevision) throw new OperationCanceledException();
            return new(true, answer);
        }
        catch (Exception) when (token.IsCancellationRequested || revisionAtStart != ConnectionRevision)
        { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) { return new(false, "Нейросеть не ответила вовремя. Попробуй позже или выбери другую модель."); }
        catch (InvalidDataException error) { return new(false, error.Message); }
        catch (Exception) { return new(false, "Подключение к нейросети не удалось. Проверь его в меню «JARVIS → Нейросети»."); }
        finally { lock (_gate) { if (_pending == timeout) _pending = null; } }
    }

    public static string Protect(string key) => string.IsNullOrEmpty(key) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string encrypted) => string.IsNullOrEmpty(encrypted) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
    public void Dispose() { lock (_gate) { _pending?.Cancel(); } _http.Dispose(); }
}
