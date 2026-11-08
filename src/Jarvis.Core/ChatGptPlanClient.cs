using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jarvis.Core;

public sealed record ChatGptAccount
{
    public string ClientId { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Email { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public string IdToken { get; init; } = "";
    public string Scope { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public bool CanUsePlan => Scope.Split(' ').Contains("chatgpt.tokens.use.direct");
    public bool SignedIn => AccessToken.Length > 0 && CanUsePlan;
}

public sealed record OAuthAttempt(string State, string Nonce, string Verifier, string RedirectUri, string ClientId)
{
    public static OAuthAttempt Create(string redirectUri, string? clientId = null) => new(
        ChatGptPlanClient.Encode(RandomNumberGenerator.GetBytes(32)),
        ChatGptPlanClient.Encode(RandomNumberGenerator.GetBytes(32)),
        ChatGptPlanClient.Encode(RandomNumberGenerator.GetBytes(32)), redirectUri, clientId ?? "dynamic_agent_client");
}

/// <summary>Public Sign in with ChatGPT flow. No CLI, browser cookies or API keys.</summary>
public sealed class ChatGptPlanClient(HttpClient http)
{
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    public static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public static Uri AuthorizeUri(OAuthAttempt attempt, string hostId)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = attempt.ClientId, ["ext_agent_host_id"] = hostId, ["response_type"] = "code",
            ["redirect_uri"] = attempt.RedirectUri, ["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",
            ["resource"] = Resource, ["state"] = attempt.State, ["nonce"] = attempt.Nonce,
            ["code_challenge_method"] = "S256", ["code_challenge"] = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier)))
        };
        if (attempt.ClientId == "dynamic_agent_client") query["agent_name_hint"] = "JARVIS";
        // No ID token in a URL; returning users choose their saved account in the browser.
        return new Uri(Issuer + "/api/accounts/authorize?" + Query(query));
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (!result.TryAdd(key, parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : ""))
                throw new InvalidDataException("Некорректный ответ авторизации. Начни вход заново.");
        }
        return result;
    }

    public static (string ClientId, string Code) ValidateCallback(OAuthAttempt attempt, string query)
    {
        var values = ParseQuery(query);
        if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State)))
            throw new InvalidDataException("Ответ браузера не относится к этому входу. Начни вход заново.");
        if (values.ContainsKey("error")) throw new InvalidDataException("Вход отменён или доступ к подписке не разрешён в браузере.");
        var id = values.GetValueOrDefault("client_id", attempt.ClientId);
        if (string.IsNullOrWhiteSpace(id) || id == "dynamic_agent_client" || (attempt.ClientId != "dynamic_agent_client" && id != attempt.ClientId))
            throw new InvalidDataException("Регистрация ChatGPT не завершена. Начни вход заново.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidDataException("Браузер не передал подтверждение входа.");
        return (id, code);
    }

    public async Task<ChatGptAccount> ExchangeAsync(OAuthAttempt attempt, string query, ChatGptAccount? previous, CancellationToken token)
    {
        var (id, code) = ValidateCallback(attempt, query);
        using var data = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = id,
            ["code"] = code, ["code_verifier"] = attempt.Verifier, ["redirect_uri"] = attempt.RedirectUri, ["resource"] = Resource }, token);
        var account = ReadTokens(data.RootElement, new ChatGptAccount { ClientId = id });
        using var keys = await GetJsonAsync(Issuer + "/.well-known/jwks.json", null, token);
        var identity = ValidateIdentity(account.IdToken, keys.RootElement, id, attempt.Nonce, DateTimeOffset.UtcNow);
        if (previous != null && previous.Subject.Length > 0 && previous.Subject != identity.Subject)
            throw new InvalidDataException("Выбран другой аккаунт. Добавь отдельное подключение для него.");
        return account with { Subject = identity.Subject, Email = identity.Email };
    }

    public static (string Subject, string Email) ValidateIdentity(string jwt, JsonElement jwks, string clientId, string nonce, DateTimeOffset now)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) throw new CryptographicException();
            using var header = JsonDocument.Parse(Decode(parts[0]));
            using var payload = JsonDocument.Parse(Decode(parts[1]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw new CryptographicException();
            var kid = header.RootElement.GetProperty("kid").GetString();
            var key = jwks.GetProperty("keys").EnumerateArray().First(k => k.GetProperty("kid").GetString() == kid && k.GetProperty("kty").GetString() == "RSA");
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) });
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
            var p = payload.RootElement;
            var aud = p.GetProperty("aud");
            var audiences = aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(a => a.GetString()).ToArray() : [aud.GetString()];
            if (p.GetProperty("iss").GetString() != Issuer || !audiences.Contains(clientId)
                || (audiences.Length > 1 && (!p.TryGetProperty("azp", out var azp) || azp.GetString() != clientId))
                || p.GetProperty("exp").GetInt64() <= now.ToUnixTimeSeconds()
                || (p.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now.ToUnixTimeSeconds() + 60)
                || p.GetProperty("nonce").GetString() != nonce) throw new CryptographicException();
            var subject = p.GetProperty("sub").GetString();
            if (string.IsNullOrWhiteSpace(subject)) throw new CryptographicException();
            return (subject, p.TryGetProperty("email", out var email) ? email.GetString() ?? "" : "");
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new InvalidDataException("Не удалось проверить подлинность аккаунта ChatGPT. Вход не сохранён."); }
    }

    public async Task<ChatGptAccount> RefreshAsync(ChatGptAccount account, CancellationToken token)
    {
        using var data = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
            ["refresh_token"] = account.RefreshToken, ["resource"] = Resource }, token);
        // Identity remains the one verified during sign-in. Unvalidated replacement ID tokens are not retained.
        return ReadTokens(data.RootElement, account) with { IdToken = account.IdToken };
    }

    private static ChatGptAccount ReadTokens(JsonElement data, ChatGptAccount previous)
    {
        var access = data.GetProperty("access_token").GetString() ?? "";
        var scope = data.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : previous.Scope;
        var expires = data.GetProperty("expires_in").GetInt32();
        if (access.Length == 0 || expires <= 0 || !string.Equals(data.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ChatGPT не выдал действительный доступ. Повтори вход.");
        var result = previous with { AccessToken = access, Scope = scope, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires),
            RefreshToken = data.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : previous.RefreshToken,
            IdToken = data.TryGetProperty("id_token", out var id) ? id.GetString() ?? "" : previous.IdToken };
        if (!result.CanUsePlan) throw new InvalidDataException("В браузере не разрешено использование подписки ChatGPT. Повтори вход и разреши доступ.");
        return result;
    }

    public async Task RevokeAsync(ChatGptAccount account, CancellationToken token)
    {
        if (account.RefreshToken.Length == 0) return;
        using var discovery = await GetJsonAsync(Issuer + "/.well-known/openid-configuration", null, token);
        var uri = new Uri(discovery.RootElement.GetProperty("revocation_endpoint").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "auth.openai.com" || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new InvalidDataException("Не удалось проверить адрес выхода ChatGPT.");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = account.RefreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId });
        using var response = await http.PostAsync(uri, content, token);
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("Не удалось подтвердить выход на стороне ChatGPT.");
    }

    public async Task<string[]> ModelsAsync(ChatGptAccount account, CancellationToken token)
    {
        using var data = await GetJsonAsync(Resource + "/models", account.AccessToken, token);
        return data.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => m.TryGetProperty("visibility", out var v) && v.GetString() == "list")
            .Select(m => m.GetProperty("slug").GetString()).OfType<string>().Take(500).ToArray();
    }

    public async Task<string> AskAsync(ChatGptAccount account, string model, string question, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidDataException("Выбери модель в настройках нейросети.");
        if (question.Length is 0 or > 4000) throw new InvalidDataException("Вопрос должен содержать от 1 до 4000 символов.");
        using var request = new HttpRequestMessage(HttpMethod.Post, Resource + "/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        request.Content = JsonContent.Create(new { model, instructions = HttpAiClient.SystemPrompt,
            input = new[] { new { role = "user", content = question } }, store = false, stream = true });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        CheckStatus(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await ReadAnswerAsync(stream, token);
    }

    public static async Task<string> ReadAnswerAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var answer = new StringBuilder();
        var eventData = new StringBuilder();
        var total = 0;
        // SSE is bounded before allocating unbounded lines or parsing events.
        var line = new StringBuilder(); var ch = new char[1];
        while (await reader.ReadAsync(ch, token) > 0)
        {
            if (++total > 2_000_000) throw new InvalidDataException("Ответ сервиса слишком большой.");
            if (ch[0] != '\n') { if (ch[0] != '\r') line.Append(ch[0]); continue; }
            var text = line.ToString(); line.Clear();
            if (text.StartsWith("data:", StringComparison.Ordinal)) eventData.AppendLine(text[5..].TrimStart());
            if (text.Length != 0 || eventData.Length == 0) continue;
            var data = eventData.ToString(); eventData.Clear();
            if (data.Trim() == "[DONE]") break;
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement; var type = root.GetProperty("type").GetString();
            if (type == "response.output_text.delta") answer.Append(root.GetProperty("delta").GetString());
            if (type == "response.completed") return HttpAiClient.CleanAnswer(answer.ToString());
            if (type is "response.failed" or "response.incomplete" or "error")
                throw new InvalidDataException("ChatGPT не завершил ответ. Проверь лимит подписки и доступ к модели.");
        }
        throw new InvalidDataException("Ответ ChatGPT прервался до завершения. Повтори вопрос.");
    }

    private async Task<JsonDocument> TokenAsync(Dictionary<string, string> fields, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/api/accounts/oauth/token") { Content = new FormUrlEncodedContent(fields) };
        return await SendJsonAsync(request, token);
    }
    private async Task<JsonDocument> GetJsonAsync(string uri, string? access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (access != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return await SendJsonAsync(request, token);
    }
    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        CheckStatus(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(); var block = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + count > 2_000_000) throw new InvalidDataException("Ответ сервиса слишком большой.");
            buffer.Write(block, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"ChatGPT вернул HTTP {(int)response.StatusCode}. Проверь доступ к сервису, вход и лимиты подписки.");
    }
}
