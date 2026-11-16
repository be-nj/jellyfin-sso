using System.Net;
using System.Reflection;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A provider's answer to a request that failed ends up in an exception message, and the
/// callback logs that exception. Names and groups out of a token were already kept to one
/// line for this reason; what the provider says about a failure was not, so an
/// <c>error</c> member with a newline in it wrote a line of its own into the server log.
/// </summary>
public sealed class ProviderTextInLogTests : IDisposable
{
    private const string Forged = "invalid_client\n[12:00:00] [INF] Jellyfin.Server: a line the provider wrote";

    private readonly OidcClientFactory _factory = new(NullLogger<OidcClientFactory>.Instance);
    private readonly HttpClient _client;
    private string _issuer = "https://idp.example";

    public ProviderTextInLogTests()
    {
        _client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                var document = new Dictionary<string, string>
                {
                    ["issuer"] = _issuer,
                    ["authorization_endpoint"] = "https://idp.example/authorize",
                    ["token_endpoint"] = "https://idp.example/token",
                    ["userinfo_endpoint"] = "https://idp.example/userinfo",
                    ["jwks_uri"] = "https://idp.example/keys",
                };
                return Json(HttpStatusCode.OK, document);
            }

            return path is "/token" or "/userinfo"
                ? Json(HttpStatusCode.BadRequest, new Dictionary<string, string> { ["error"] = Forged })
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"keys":[]}""") });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_factory, _client);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _client.Dispose();
    }

    private static Task<HttpResponseMessage> Json(HttpStatusCode status, Dictionary<string, string> body)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body)),
        });

    private static ProviderConfig Provider() => new() { Id = "p", Authority = "https://idp.example", ClientId = "jellyfin" };

    private static void OneLine(Exception ex)
    {
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
    }

    [Fact]
    public async Task What_the_token_endpoint_says_stays_on_one_line()
    {
        var discovery = await _factory.GetDiscoveryDocumentAsync(Provider());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _factory.ExchangeCodeAsync(Provider(), discovery, "code", "https://jf.example/cb", "verifier"));

        OneLine(ex);
        // Still said, so the operator can read what the provider objected to.
        Assert.Contains("Token exchange failed: invalid_client", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a line the provider wrote", ex.Message, StringComparison.Ordinal);
    }

    // Not tested for userinfo: a failed userinfo request reaches the message without any
    // text of the provider's — the library reports it by status — so there is nothing
    // there to keep to one line, and a test of it passed without checking anything.

    /// <summary>
    /// The discovery document names its own issuer, and a mismatch is reported with the
    /// name it gave.
    /// </summary>
    [Fact]
    public async Task What_a_refused_discovery_document_called_itself_stays_on_one_line()
    {
        _issuer = "https://elsewhere.example\n[12:00:00] [INF] Jellyfin.Server: a line the provider wrote";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _factory.GetDiscoveryDocumentAsync(Provider()));

        OneLine(ex);
        Assert.Contains("OIDC discovery failed", ex.Message, StringComparison.Ordinal);
        // The forged part is in the message — on the same line — or this test checks nothing.
        Assert.Contains("a line the provider wrote", ex.Message, StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
