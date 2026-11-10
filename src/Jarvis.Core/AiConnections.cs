using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Core;

public sealed record AiConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Provider { get; init; } = "api";
    public string Endpoint { get; init; } = "";
    public string Model { get; init; } = "";
    public string CliPath { get; init; } = "";
    public string ProtectedApiKey { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSubscription => Provider is "chatgpt" or "claude";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsBrowserTools => Provider is "chatgpt" or "claude" or "api" or "ollama";

    public void Validate()
    {
        if (!Regex.IsMatch(Id, "^[a-f0-9]{32}$") || string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
            throw new InvalidDataException("Проверь название подключения.");
        if (Provider is not ("api" or "ollama" or "chatgpt" or "claude"))
            throw new InvalidDataException("Этот способ подключения пока не поддерживается.");
        if (Model.Length > 200 || Model.Any(char.IsControl)) throw new InvalidDataException("Проверь название модели.");
        if (!IsSubscription) _ = BaseAddress();
        if (IsSubscription && CliPath.Length > 0 && (!Path.IsPathFullyQualified(CliPath) || !CliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Укажи полный путь к исполняемому файлу .exe.");
    }

    public Uri BaseAddress()
    {
        if (!Uri.TryCreate(Endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("Нужен адрес HTTPS; HTTP разрешён только для локального сервера. Не вставляй ключ в адрес.");
        return uri;
    }
}

public sealed record AiConfiguration
{
    public bool Enabled { get; init; }
    public bool UseForUnknownCommands { get; init; }
    public bool AllowLocalActions { get; init; }
    public string ProtectedChromeToken { get; init; } = "";
    public string? ActiveConnectionId { get; init; }
    public List<AiConnection> Connections { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public AiConnection? Active => Enabled ? Connections.FirstOrDefault(c => c.Id == ActiveConnectionId) : null;

    public void Validate()
    {
        if (Connections.Count > 30 || Connections.Select(c => c.Id).Distinct().Count() != Connections.Count)
            throw new InvalidDataException("Проверь список подключений: не более 30, без повторов.");
        foreach (var connection in Connections) connection.Validate();
        if (ProtectedChromeToken.Length > 2048 || ProtectedChromeToken.Any(char.IsWhiteSpace))
            throw new InvalidDataException("Проверь код подключения браузера.");
        if (Enabled && Active == null) throw new InvalidDataException("Выбери подключение для ответов нейросети.");
    }
}

public sealed class AiConnectionStore(string path)
{
    public (AiConfiguration Configuration, string? Warning) Load()
    {
        if (!File.Exists(path)) return (new(), null);
        try
        {
            var value = JsonSerializer.Deserialize<AiConfiguration>(File.ReadAllText(path)) ?? new();
            value.Validate();
            return (value, null);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or NullReferenceException)
        { return (new(), "Настройки нейросетей не прочитаны. Подключения выключены; исходный файл сохранён."); }
    }

    public void Save(AiConfiguration configuration)
    {
        configuration.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public static class AiRouting
{
    public static string? ExplicitTask(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(?:(?:джарвис|джервис|джарвиз|чарвис|jarvis)[,\s]+)?(?:задача (?:ии|нейросети)|ai task)[,\s:]+(.+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    public static bool IsStopTask(string text) => Regex.IsMatch(text.Trim(),
        @"^(?:(?:джарвис|джервис|джарвиз|чарвис|jarvis)[,\s]+)?(?:останови задачу|отмени задачу|stop task|cancel task)[.!?]*$", RegexOptions.IgnoreCase);
    public static string? ExplicitQuestion(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(?:(?:джарвис|джервис|джарвиз|чарвис|jarvis)[,\s]+)?(?:спроси (?:нейросеть|ии)|вопрос (?:нейросети|к нейросети))[,\s:]+(.+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static bool CanFallback(AiConfiguration settings, CommandRequest command, bool wasChoosing) =>
        settings.Active != null && settings.UseForUnknownCommands && !wasChoosing && command.Intent == Intent.Unknown
        && command.CandidateIds == null && (command.Explanation == null || command.CandidateIntent == Intent.LaunchTarget);

    // This only chooses an availability/verification message. The model receives the entire request and chooses the action.
    public static bool NeedsBrowser(string text)
    {
        var phrase = CommandRouter.Normalize(text);
        return RequestsChannelPage(text) || Regex.IsMatch(phrase, @"\b(?:найди|поищи|отыщи|подбери|найти|покажи|открой|открыть|прочитай|посмотри|запусти|запустить|включи|включить|find|search|open|read|play|watch)\b")
            && Regex.IsMatch(phrase, @"\b(?:youtube|ютуб\w*|википеди\w*|wikipedia|интернет\w*|онлайн|сайт\w*|браузер\w*|web|website|browser|видео|ролик\w*|videos?)\b");
    }

    public static bool RequestsChannelPage(string text) => Regex.IsMatch(CommandRouter.Normalize(text),
        @"\b(?:открой|открыть|покажи|найди|поищи|перейди на|зайди на|open|find|go to) (?:(?:мне|пожалуйста|(?:на )?(?:ютуб\w*|youtube)) )*(?:канал|channel)\b");

    public static bool RequestsChannelVideo(string text) => Regex.IsMatch(CommandRouter.Normalize(text),
        @"\b(?:канал|channel)\b.+?\b(?:и|затем|потом|and|then) (?:запусти|включи|открой|посмотри|play|open|watch)\b.*\b(?:видео|ролик\w*|video)\b");
}
