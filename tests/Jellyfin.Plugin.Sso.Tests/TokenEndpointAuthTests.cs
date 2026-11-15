using System.Net;
using System.Reflection;
using Duende.IdentityModel.Client;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// How a confidential client's secret reaches the token endpoint. The OpenID Foundation
/// conformance suite, run as a confidential client for the first time, passed with the
/// secret in an HTTP Basic header and failed at the first module when it wanted the
/// secret as a form parameter: the plugin had one way and no other. The provider's
/// discovery document says which it takes, so the plugin reads it there.
/// </summary>
public sealed class TokenEndpointAuthTests : IDisposable
{
    private const string Variable = "SSO_TOKEN_ENDPOINT_AUTH_TEST_SECRET";
    private const string Secret = "s3cr3t/with+characters=that&need:escaping";

    private readonly OidcClientFactory _factory = new(NullLogger<OidcClientFactory>.Instance);
    private readonly HttpClient _client;
    private string? _methods;
    private HttpRequestMessage? _tokenRequest;
    private string? _tokenBody;

    public TokenEndpointAuthTests()
    {
        Environment.SetEnvironmentVariable(Variable, Secret);
        _client = new HttpClient(new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
            {
                json = """{"issuer":"https://idp.example","authorization_endpoint":"https://idp.example/authorize","token_endpoint":"https://idp.example/token","jwks_uri":"https://idp.example/keys"METHODS}"""
                    .Replace("METHODS", _methods is null ? string.Empty : $",\"token_endpoint_auth_methods_supported\":{_methods}", StringComparison.Ordinal);
            }
            else if (path == "/token")
            {
                _tokenRequest = request;
                _tokenBody = await request.Content!.ReadAsStringAsync();
                json = """{"access_token":"a","id_token":"i","token_type":"Bearer"}""";
            }
            else
            {
                json = """{"keys":[]}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_factory, _client);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        _factory.Dispose();
        _client.Dispose();
    }

    private async Task ExchangeAsync(OidcClientType type)
    {
        var provider = new ProviderConfig
        {
            Id = "p",
            Authority = "https://idp.example",
            ClientId = "jellyfin",
            ClientType = type,
            ConfidentialSecretRef = type == OidcClientType.Confidential ? "env:" + Variable : null,
        };
        var discovery = await _factory.GetDiscoveryDocumentAsync(provider);
        await _factory.ExchangeCodeAsync(provider, discovery, "code", "https://jf.example/cb", "verifier");
    }

    /// <summary>
    /// A provider that publishes no list supports Basic: that is the default the
    /// specification gives, and what every release so far sent.
    /// </summary>
    [Fact]
    public async Task The_secret_goes_in_a_basic_header_when_the_provider_lists_nothing()
    {
        await ExchangeAsync(OidcClientType.Confidential);

        Assert.Equal("Basic", _tokenRequest!.Headers.Authorization?.Scheme);
        Assert.DoesNotContain("client_secret", _tokenBody, StringComparison.Ordinal);
    }

    /// <summary>Both listed: Basic, as before. Google lists both.</summary>
    [Fact]
    public async Task The_secret_goes_in_a_basic_header_when_the_provider_takes_either()
    {
        _methods = """["client_secret_post","client_secret_basic"]""";

        await ExchangeAsync(OidcClientType.Confidential);

        Assert.Equal("Basic", _tokenRequest!.Headers.Authorization?.Scheme);
        Assert.DoesNotContain("client_secret", _tokenBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The provider says it takes the form parameter and not Basic. A header there is
    /// answered with invalid_client, which is the login that could not work.
    /// </summary>
    [Fact]
    public async Task The_secret_goes_in_the_form_when_that_is_the_only_way_the_provider_takes_it()
    {
        _methods = """["client_secret_post","private_key_jwt"]""";

        await ExchangeAsync(OidcClientType.Confidential);

        Assert.Null(_tokenRequest!.Headers.Authorization);
        Assert.Contains("client_secret=" + Uri.EscapeDataString(Secret), _tokenBody, StringComparison.Ordinal);
        Assert.Contains("client_id=jellyfin", _tokenBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A public client has nothing to authenticate with. It names itself with client_id
    /// in the form and sends no Authorization header — it used to send a Basic header
    /// with an empty password, which is an attempt to authenticate that a strict provider
    /// answers with invalid_client (#278).
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("""["client_secret_post"]""")]
    [InlineData("""["client_secret_basic","none"]""")]
    public async Task A_public_client_names_itself_in_the_form_and_sends_no_credentials(string? methods)
    {
        _methods = methods;

        await ExchangeAsync(OidcClientType.Public);

        Assert.Null(_tokenRequest!.Headers.Authorization);
        Assert.DoesNotContain("client_secret", _tokenBody, StringComparison.Ordinal);
        Assert.Contains("client_id=jellyfin", _tokenBody, StringComparison.Ordinal);
        Assert.Contains("code_verifier=verifier", _tokenBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, ClientCredentialStyle.AuthorizationHeader)]
    [InlineData("", ClientCredentialStyle.AuthorizationHeader)]
    [InlineData("client_secret_basic", ClientCredentialStyle.AuthorizationHeader)]
    [InlineData("client_secret_post,client_secret_basic", ClientCredentialStyle.AuthorizationHeader)]
    [InlineData("private_key_jwt", ClientCredentialStyle.AuthorizationHeader)]
    [InlineData("client_secret_post", ClientCredentialStyle.PostBody)]
    [InlineData("none,client_secret_post", ClientCredentialStyle.PostBody)]
    public void The_form_is_chosen_only_where_it_is_listed_and_basic_is_not(
        string? listed, ClientCredentialStyle expected)
        => Assert.Equal(
            expected,
            OidcClientFactory.SecretStyleFor(listed?.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
