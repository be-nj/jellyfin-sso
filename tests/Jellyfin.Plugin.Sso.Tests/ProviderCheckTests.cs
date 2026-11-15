using System.Net;
using System.Reflection;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The check on the provider card answers "would a login get as far as the provider"
/// without anybody attempting one. What it says has to be what a login would find, in
/// words an administrator can act on, and it reads a secret without ever repeating it.
/// </summary>
public sealed class ProviderCheckTests : IDisposable
{
    private const string Keys = """{"keys":[{"kty":"RSA","alg":"RS256","use":"sig","kid":"k1","n":"sXchDaQebHnPiGvyDOAT4saGEUetSyo9MKLOoWFsueri23bOdgWp4Dy1WlUzewbgBHod5pcM9H95GQRV3JDXboIRROSBigeC5yjU1hGzHHyXss8UDprecbAYxknTcQkhslANGRUZmdTOQ5qTRsLAt6BTYuyvVRdhS8exSZEy_c4gs_7svlJJQ4H9_NxsiIoLwAEk7-Q3UXERGYw_75IDrGA84-lA_-Ct4eTlXHBIY2EaV7t7LjJaynVJCpkv4LKjTTAumiGUIuQhrNhZLuF_RJLqHpM2kgWFLU7-VTdL1VbC2tejvcI2BlMkEpk1BzBZI0KQB0GaDWFLN-aEAw3vRw","e":"AQAB"}]}""";

    private readonly OidcClientFactory _factory = new(NullLogger<OidcClientFactory>.Instance);
    private readonly List<string> _requests = [];
    private readonly HttpClient _client;

    public ProviderCheckTests()
    {
        _client = new HttpClient(new Handler(request =>
        {
            _requests.Add(request.RequestUri!.Host);
            string json;
            if (request.RequestUri.Host == "down.example")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            if (!request.RequestUri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal))
                json = Keys;
            else if (request.RequestUri.Host == "accounts.google.com")
                json = """{"issuer":"https://accounts.google.com","authorization_endpoint":"https://accounts.google.com/o/oauth2/v2/auth","token_endpoint":"https://oauth2.googleapis.com/token","userinfo_endpoint":"https://openidconnect.googleapis.com/v1/userinfo","jwks_uri":"https://www.googleapis.com/oauth2/v3/certs"}""";
            else
                json = """{"issuer":"https://idp.example","authorization_endpoint":"https://idp.example/authorize","token_endpoint":"https://idp.example/token","jwks_uri":"https://idp.example/keys"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_factory, _client);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _client.Dispose();
    }

    private static ProviderConfig Provider(string authority = "https://idp.example") => new()
    {
        Id = "p", Name = "Test", Enabled = true, Authority = authority, ClientId = "jellyfin",
    };

    private static ProviderCheckLine Line(ProviderCheckResult result, string what)
        => Assert.Single(result.Lines, line => line.What == what);

    [Fact]
    public async Task A_working_provider_reports_its_issuer_endpoints_and_keys()
    {
        var result = await new ProviderCheck(_factory).RunAsync(Provider());

        Assert.True(result.Ok);
        Assert.Equal("Issuer https://idp.example", Line(result, "Discovery document").Detail);
        Assert.Equal("https://idp.example/token", Line(result, "Token endpoint").Detail);
        Assert.Equal("1 published (RS256)", Line(result, "Signing keys").Detail);
        // Optional endpoints that are absent are said to be absent, and are not a failure.
        Assert.True(Line(result, "End-session endpoint").Ok);
        Assert.Equal("Not published.", Line(result, "End-session endpoint").Detail);
    }

    /// <summary>
    /// The report this check exists for (#260): Google without its hosts named. The
    /// refusal a login would only have left in the server log is the answer here, hosts
    /// to name included.
    /// </summary>
    [Fact]
    public async Task Google_without_its_hosts_reports_which_hosts_to_name()
    {
        var result = await new ProviderCheck(_factory).RunAsync(Provider("https://accounts.google.com"));

        Assert.False(result.Ok);
        var discovery = Line(result, "Discovery document");
        Assert.False(discovery.Ok);
        Assert.EndsWith(
            "www.googleapis.com oauth2.googleapis.com openidconnect.googleapis.com",
            discovery.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_is_reported_not_thrown()
    {
        var result = await new ProviderCheck(_factory).RunAsync(Provider("https://down.example"));

        Assert.False(result.Ok);
        Assert.Contains("OIDC discovery failed", Line(result, "Discovery document").Detail);
    }

    /// <summary>Half a card is checked as far as it goes, and nothing is fetched for it.</summary>
    [Fact]
    public async Task A_provider_without_an_issuer_is_told_so_and_nothing_is_fetched()
    {
        var result = await new ProviderCheck(_factory).RunAsync(new ProviderConfig { Id = "p" });

        Assert.False(result.Ok);
        Assert.False(Line(result, "Issuer URL").Ok);
        Assert.False(Line(result, "Client ID").Ok);
        Assert.Empty(_requests);
        // Not being enabled yet is worth saying and is not a fault.
        Assert.True(Line(result, "Enabled").Ok);
    }

    [Fact]
    public async Task An_issuer_that_is_not_an_address_is_reported_not_thrown()
    {
        var result = await new ProviderCheck(_factory).RunAsync(Provider("not an address"));

        Assert.False(Line(result, "Discovery document").Ok);
    }

    /// <summary>
    /// A setup that reads groups from userinfo needs the endpoint; any other does not.
    /// </summary>
    [Fact]
    public async Task A_missing_userinfo_endpoint_matters_only_where_groups_come_from_it()
    {
        var plain = await new ProviderCheck(_factory).RunAsync(Provider());
        Assert.True(Line(plain, "Userinfo endpoint").Ok);

        var provider = Provider();
        provider.GroupsFromUserInfo = true;
        var needsIt = await new ProviderCheck(_factory).RunAsync(provider);

        Assert.False(needsIt.Ok);
        Assert.False(Line(needsIt, "Userinfo endpoint").Ok);
    }

    /// <summary>
    /// The secret is read to see that it is there. Its value is in no line of the answer.
    /// </summary>
    [Fact]
    public async Task A_confidential_clients_secret_is_found_without_being_repeated()
    {
        const string Variable = "SSO_PROVIDER_CHECK_TEST_SECRET";
        const string Value = "s3cr3t-value-that-must-not-appear";
        Environment.SetEnvironmentVariable(Variable, Value);
        try
        {
            var provider = Provider();
            provider.ClientType = OidcClientType.Confidential;
            provider.ConfidentialSecretRef = "env:" + Variable;

            var result = await new ProviderCheck(_factory).RunAsync(provider);

            Assert.True(result.Ok);
            Assert.True(Line(result, "Client secret").Ok);
            Assert.DoesNotContain(result.Lines, line => line.Detail.Contains(Value, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

    [Fact]
    public async Task A_confidential_client_without_its_secret_fails_the_check()
    {
        var provider = Provider();
        provider.ClientType = OidcClientType.Confidential;
        provider.ConfidentialSecretRef = "env:SSO_PROVIDER_CHECK_TEST_UNSET";

        var result = await new ProviderCheck(_factory).RunAsync(provider);

        Assert.False(result.Ok);
        Assert.Contains("not set", Line(result, "Client secret").Detail);
    }

    /// <summary>A public client has no secret to look for, so the check does not mention one.</summary>
    [Fact]
    public async Task A_public_client_is_not_asked_for_a_secret()
    {
        var result = await new ProviderCheck(_factory).RunAsync(Provider());

        Assert.DoesNotContain(result.Lines, line => line.What == "Client secret");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
