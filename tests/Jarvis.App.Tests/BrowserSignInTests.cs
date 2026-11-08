using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.App.Services;
using Jarvis.Core;
using Xunit;

namespace Jarvis.App.Tests;

public class BrowserSignInTests
{
    [Fact]
    public async Task BrowserCallbackExchangesCodeProtectsCredentialsAndPreservesRegistrationAfterSignOut()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-browser-" + Guid.NewGuid());
        using var rsa = RSA.Create(2048);
        using var handler = new SignInServer(rsa);
        using var http = new HttpClient(handler);
        var service = new ChatGptSignIn(root, new ChatGptPlanClient(http));
        var connection = new AiConnection { Name = "Fixture ChatGPT", Provider = "chatgpt" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? browser = null;
        string? initialClient = null;
        try
        {
            async Task SimulateBrowser(Uri uri)
            {
                var query = ChatGptPlanClient.ParseQuery(uri.Query);
                initialClient = query["client_id"];
                handler.Nonce = query["nonce"]; handler.RedirectUri = query["redirect_uri"];
                using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
                // An unrelated request must not steal or finish the pending sign-in.
                var wrong = await local.GetAsync(handler.RedirectUri + "?state=wrong&code=wrong", timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
                var result = await local.GetAsync(handler.RedirectUri + "?state=" + query["state"] + "&code=fixture-code&client_id=oaiapp_fixture", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            }
            await service.SignInAsync(connection, uri => browser = SimulateBrowser(uri), null, timeout.Token);
            await browser!;
            Assert.Equal("dynamic_agent_client", initialClient);
            var account = service.Read(connection)!;
            Assert.Equal("fixture@example.test", account.Email);
            Assert.True(account.SignedIn);
            var stored = File.ReadAllText(Path.Combine(root, connection.Id + ".protected"));
            Assert.DoesNotContain("fixture-access-token", stored);
            Assert.DoesNotContain("fixture-refresh-token", stored);
            Assert.DoesNotContain("fixture@example.test", stored);
            Assert.Equal("fixture-access-token", (await service.ReadyAsync(connection, timeout.Token)).AccessToken);
            var host = File.ReadAllText(Path.Combine(root, "host-id"));
            Assert.True(await service.SignOutAsync(connection, timeout.Token));
            Assert.False(service.Read(connection)!.SignedIn);
            Assert.Equal("oaiapp_fixture", service.Read(connection)!.ClientId);
            await service.SignInAsync(connection, uri => browser = SimulateBrowser(uri), null, timeout.Token);
            await browser!;
            Assert.Equal("oaiapp_fixture", initialClient);
            Assert.Equal(host, File.ReadAllText(Path.Combine(root, "host-id")));
        }
        finally
        {
            File.Delete(Path.Combine(root, connection.Id + ".protected"));
            File.Delete(Path.Combine(root, "host-id"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    [Fact]
    public async Task WaitingForBrowserCanBeCancelled()
    {
        using var callback = new OAuthLoopback();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback.ReceiveAsync("state", timeout.Token));
    }

    [Theory]
    [InlineData("Open: https://claude.ai/oauth/authorize?state=fixture", true)]
    [InlineData("https://evil.test/oauth/authorize?state=fixture", false)]
    [InlineData("https://claude.ai@evil.test/oauth/authorize", false)]
    [InlineData("https://claude.ai/help", false)]
    public void ClaudeFallbackOnlyAcceptsProviderAuthorizationPages(string text, bool valid) =>
        Assert.Equal(valid, SubscriptionClient.AuthorizationLink(text) != null);

    private sealed class SignInServer(RSA rsa) : HttpMessageHandler
    {
        public string Nonce = "";
        public string RedirectUri = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object result;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/accounts/oauth/token":
                    var fields = ChatGptPlanClient.ParseQuery(await request.Content!.ReadAsStringAsync(token));
                    Assert.Equal("oaiapp_fixture", fields["client_id"]);
                    Assert.Equal(RedirectUri, fields["redirect_uri"]);
                    Assert.Equal("authorization_code", fields["grant_type"]);
                    Assert.Equal("fixture-code", fields["code"]);
                    Assert.True(fields["code_verifier"].Length >= 43);
                    string B64(object value) => ChatGptPlanClient.Encode(JsonSerializer.SerializeToUtf8Bytes(value));
                    var unsigned = B64(new { alg = "RS256", kid = "fixture" }) + "." + B64(new { iss = ChatGptPlanClient.Issuer, aud = "oaiapp_fixture", nonce = Nonce,
                        sub = "fixture-subject", email = "fixture@example.test", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
                    var jwt = unsigned + "." + ChatGptPlanClient.Encode(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                    result = new { access_token = "fixture-access-token", refresh_token = "fixture-refresh-token", id_token = jwt,
                        token_type = "Bearer", expires_in = 3600, scope = "openid email offline_access resource.invoke chatgpt.tokens.use.direct" };
                    break;
                case "/.well-known/jwks.json":
                    var key = rsa.ExportParameters(false);
                    result = new { keys = new[] { new { kid = "fixture", kty = "RSA", n = ChatGptPlanClient.Encode(key.Modulus!), e = ChatGptPlanClient.Encode(key.Exponent!) } } };
                    break;
                case "/.well-known/openid-configuration": result = new { revocation_endpoint = "https://auth.openai.com/api/accounts/oauth/revoke" }; break;
                case "/api/accounts/oauth/revoke": return new(HttpStatusCode.OK);
                default: throw new InvalidOperationException("Unexpected fixture request");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result)) };
        }
    }
}
