using System.Text.Json;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// RFC 9207 on the way back from the provider (#325). The end-to-end test
/// <c>A_code_the_honest_provider_issued_is_not_sent_to_another_providers_token_endpoint</c>
/// walks the mix-up itself; these pin the rule.
/// </summary>
public sealed class AuthorizationResponseIssuerTests
{
    private const string Issuer = "https://idp.example.com/realms/home";

    private static JsonElement Discovery(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_providers_own_issuer_is_accepted()
    {
        Assert.Null(AuthorizationResponseIssuer.Refusal(Issuer, Issuer, Discovery("{}")));
    }

    [Theory]
    [InlineData("https://attacker.example.com")]
    [InlineData("https://idp.example.com/realms/home/")]
    [InlineData("https://IDP.example.com/realms/home")]
    [InlineData("")]
    public void Another_issuer_is_refused_compared_exactly(string iss)
    {
        Assert.NotNull(AuthorizationResponseIssuer.Refusal(iss, Issuer, Discovery("{}")));
    }

    [Fact]
    public void A_missing_iss_is_refused_where_the_provider_says_it_sends_one()
    {
        var discovery = Discovery($$"""{"{{AuthorizationResponseIssuer.SupportedMember}}": true}""");

        Assert.NotNull(AuthorizationResponseIssuer.Refusal(null, Issuer, discovery));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"authorization_response_iss_parameter_supported": false}""")]
    [InlineData("""{"authorization_response_iss_parameter_supported": "true"}""")]
    public void A_missing_iss_is_accepted_where_the_provider_does_not_say_so(string json)
    {
        Assert.Null(AuthorizationResponseIssuer.Refusal(null, Issuer, Discovery(json)));
    }

    [Fact]
    public void A_missing_iss_is_accepted_without_a_document_to_read()
    {
        Assert.Null(AuthorizationResponseIssuer.Refusal(null, Issuer, null));
    }
}
