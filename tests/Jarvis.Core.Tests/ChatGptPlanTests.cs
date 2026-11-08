using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using Jarvis.Core;
using Xunit;

namespace Jarvis.Core.Tests;

public class ChatGptPlanTests
{
    [Fact]
    public async Task PlanInferenceUsesOAuthResponsesStreamAndNeverChatCompletionOrApiKey()
    {
        using var http = new HttpClient(new FixtureHandler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-oauth", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\ndata: {\"type\":\"response.completed\"}\n\n") };
        }));
        Assert.Equal("ok", await new ChatGptPlanClient(http).AskAsync(new() { AccessToken = "fixture-oauth" }, "chosen-model", "question", CancellationToken.None));
    }

    [Fact]
    public async Task RefreshRotatesTokensWithTheIssuedClientAndPreservesVerifiedIdentity()
    {
        using var http = new HttpClient(new FixtureHandler(async request =>
        {
            var form = ChatGptPlanClient.ParseQuery(await request.Content!.ReadAsStringAsync());
            Assert.Equal("refresh_token", form["grant_type"]);
            Assert.Equal("oaiapp_saved", form["client_id"]);
            Assert.Equal("old-refresh", form["refresh_token"]);
            Assert.Equal(ChatGptPlanClient.Resource, form["resource"]);
            Assert.False(form.ContainsKey("scope"));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}") };
        }));
        var result = await new ChatGptPlanClient(http).RefreshAsync(new() { ClientId = "oaiapp_saved", Subject = "verified-user", RefreshToken = "old-refresh", Scope = "chatgpt.tokens.use.direct" }, CancellationToken.None);
        Assert.Equal("new-refresh", result.RefreshToken); Assert.Equal("new-access", result.AccessToken);
        Assert.Equal("verified-user", result.Subject);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    [Fact]
    public void RegistrationUsesPkceAndReturningAccountKeepsItsClientId()
    {
        var attempt = OAuthAttempt.Create("http://127.0.0.1:54321/auth/callback");
        var uri = ChatGptPlanClient.AuthorizeUri(attempt, "urn:uuid:fixture");
        var query = ChatGptPlanClient.ParseQuery(uri.Query);
        Assert.Equal("auth.openai.com", uri.Host);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("JARVIS", query["agent_name_hint"]);
        Assert.Equal(ChatGptPlanClient.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier))), query["code_challenge"]);
        Assert.DoesNotContain(attempt.Verifier, uri.AbsoluteUri);
        Assert.NotEqual(attempt.State, OAuthAttempt.Create(attempt.RedirectUri).State);
        var returning = ChatGptPlanClient.ParseQuery(ChatGptPlanClient.AuthorizeUri(attempt with { ClientId = "oaiapp_saved" }, "host").Query);
        Assert.Equal("oaiapp_saved", returning["client_id"]);
        Assert.False(returning.ContainsKey("agent_name_hint"));
    }

    [Fact]
    public void CallbackRejectsWrongStateDuplicateParametersAndSwappedRegistrations()
    {
        var attempt = OAuthAttempt.Create("http://127.0.0.1:1234/auth/callback");
        var valid = $"?state={attempt.State}&code=fixture&client_id=oaiapp_new";
        Assert.Equal(("oaiapp_new", "fixture"), ChatGptPlanClient.ValidateCallback(attempt, valid));
        Assert.Throws<InvalidDataException>(() => ChatGptPlanClient.ValidateCallback(attempt, valid.Replace(attempt.State, "wrong")));
        Assert.Throws<InvalidDataException>(() => ChatGptPlanClient.ValidateCallback(attempt, valid + "&state=duplicate"));
        Assert.Throws<InvalidDataException>(() => ChatGptPlanClient.ValidateCallback(attempt, valid + "&error=access_denied"));
        Assert.Throws<InvalidDataException>(() => ChatGptPlanClient.ValidateCallback(attempt, $"?state={attempt.State}&code=fixture"));
        Assert.Throws<InvalidDataException>(() => ChatGptPlanClient.ValidateCallback(attempt with { ClientId = "oaiapp_saved" }, valid));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("signature", false)]
    [InlineData("issuer", false)]
    [InlineData("audience", false)]
    [InlineData("expiry", false)]
    [InlineData("nonce", false)]
    [InlineData("algorithm", false)]
    public void IdentityIsVerifiedBeforeAcceptingTheAccount(string change, bool expected)
    {
        using var rsa = RSA.Create(2048);
        var key = rsa.ExportParameters(false);
        using var jwks = JsonDocument.Parse(JsonSerializer.Serialize(new { keys = new[] { new { kid = "test", kty = "RSA", n = ChatGptPlanClient.Encode(key.Modulus!), e = ChatGptPlanClient.Encode(key.Exponent!) } } }));
        var now = DateTimeOffset.UtcNow;
        string B64(object value) => ChatGptPlanClient.Encode(JsonSerializer.SerializeToUtf8Bytes(value));
        var unsigned = B64(new { alg = change == "algorithm" ? "none" : "RS256", kid = "test" }) + "." + B64(new
        {
            iss = change == "issuer" ? "https://evil.test" : ChatGptPlanClient.Issuer,
            aud = change == "audience" ? "other-client" : "oaiapp_test",
            exp = now.AddMinutes(change == "expiry" ? -5 : 5).ToUnixTimeSeconds(),
            nonce = change == "nonce" ? "wrong" : "nonce", sub = "fixture-subject", email = "fixture@example.test"
        });
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (change == "signature") signature[0] ^= 1;
        var jwt = unsigned + "." + ChatGptPlanClient.Encode(signature);
        if (expected) Assert.Equal(("fixture-subject", "fixture@example.test"), ChatGptPlanClient.ValidateIdentity(jwt, jwks.RootElement, "oaiapp_test", "nonce", now));
        else Assert.Throws<InvalidDataException>(() => ChatGptPlanClient.ValidateIdentity(jwt, jwks.RootElement, "oaiapp_test", "nonce", now));
    }

    [Theory]
    [InlineData("response.completed", true)]
    [InlineData("response.failed", false)]
    [InlineData("response.incomplete", false)]
    [InlineData("", false)]
    public async Task StreamingAnswerRequiresTerminalSuccess(string finalEvent, bool expected)
    {
        var text = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Ответ\"}\n\n";
        if (finalEvent.Length > 0) text += "data: {\"type\":\"" + finalEvent + "\"}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        if (expected) Assert.Equal("Ответ", await ChatGptPlanClient.ReadAnswerAsync(stream, CancellationToken.None));
        else await Assert.ThrowsAsync<InvalidDataException>(() => ChatGptPlanClient.ReadAnswerAsync(stream, CancellationToken.None));
    }
}
