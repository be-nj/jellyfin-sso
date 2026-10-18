using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Controllers;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The provider id and the IdP's error arrive in the query string and end up in the log
/// and in the response. Neither may carry text the caller chose: newlines would let them
/// forge log entries.
/// </summary>
public sealed class LoggableTests : IDisposable
{
    private readonly PluginHarness _harness = new();

    [Fact]
    public void A_configured_provider_is_named_in_the_log()
    {
        _harness.Configuration.Providers.Add(new ProviderConfig { Id = "provider-1", Name = "Authentik" });

        Assert.Equal("provider-1", SsoController.KnownProviderId("provider-1"));
    }

    [Theory]
    [InlineData("nonexistent")]
    [InlineData("provider-1\n[12:00:00] [INF] Fake: an administrator logged in")]
    [InlineData("")]
    public void Anything_else_is_reported_as_unknown(string providerId)
    {
        _harness.Configuration.Providers.Add(new ProviderConfig { Id = "provider-1", Name = "Authentik" });

        Assert.Equal("unknown", SsoController.KnownProviderId(providerId));
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("server_error")]
    [InlineData("invalid_scope")]
    public void A_standard_oauth_error_is_passed_through(string error)
    {
        Assert.Equal(error, SsoController.OAuthErrorCode(error));
    }

    [Theory]
    [InlineData("something_the_idp_invented")]
    [InlineData("access_denied\nforged log line")]
    [InlineData(null)]
    public void Anything_outside_RFC_6749_collapses_to_one_code(string? error)
    {
        var code = SsoController.OAuthErrorCode(error);

        Assert.Equal("unrecognised_error", code);
        Assert.DoesNotContain('\n', code);
    }

    public void Dispose() => _harness.Dispose();
}
