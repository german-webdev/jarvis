using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Jarvis.Core;

namespace Jarvis.App.Services;

public sealed class AiService : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(100) };
    public AiConnectionStore Store { get; }
    public SubscriptionClient Subscriptions { get; }
    public AiConfiguration Configuration { get; private set; }
    public string? LoadWarning { get; }

    public AiService(string root)
    {
        Store = new(Path.Combine(root, "data", "ai-connections.json"));
        (Configuration, LoadWarning) = Store.Load();
        Subscriptions = new(Path.Combine(root, "data", "ai-sessions"));
    }

    public void Save(AiConfiguration settings) { Store.Save(settings); Configuration = settings; }
    public Task<string[]> ModelsAsync(AiConnection connection, CancellationToken token) =>
        new HttpAiClient(_http).ModelsAsync(connection, Unprotect(connection.ProtectedApiKey), token);

    public async Task<SkillResult> AskAsync(string question, CancellationToken token)
    {
        var selected = Configuration.Active;
        if (selected == null) return new(false, "Нейросеть выключена. Добавь и включи подключение в меню «JARVIS → Нейросети».");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(110));
            var answer = selected.IsSubscription
                ? await Subscriptions.AskAsync(selected, question, timeout.Token)
                : await new HttpAiClient(_http).AskAsync(selected, Unprotect(selected.ProtectedApiKey), question, timeout.Token);
            return new(true, answer);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, "Нейросеть не ответила вовремя. Попробуй позже или выбери другую модель."); }
        catch (InvalidDataException error) { return new(false, error.Message); }
        catch (Exception) { return new(false, "Подключение к нейросети не удалось. Проверь его в меню «JARVIS → Нейросети»."); }
    }

    public static string Protect(string key) => string.IsNullOrEmpty(key) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string encrypted) => string.IsNullOrEmpty(encrypted) ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser));
    public void Dispose() => _http.Dispose();
}
