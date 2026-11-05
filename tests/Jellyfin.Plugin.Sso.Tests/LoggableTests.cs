using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Logging;

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

        Assert.Equal("provider-1", Loggable.KnownProviderId("provider-1"));
    }

    [Theory]
    [InlineData("nonexistent")]
    [InlineData("provider-1\n[12:00:00] [INF] Fake: an administrator logged in")]
    [InlineData("")]
    public void Anything_else_is_reported_as_unknown(string providerId)
    {
        _harness.Configuration.Providers.Add(new ProviderConfig { Id = "provider-1", Name = "Authentik" });

        Assert.Equal("unknown", Loggable.KnownProviderId(providerId));
    }

    [Theory]
    [InlineData("access_denied")]
    [InlineData("server_error")]
    [InlineData("invalid_scope")]
    public void A_standard_oauth_error_is_passed_through(string error)
    {
        Assert.Equal(error, Loggable.OAuthErrorCode(error));
    }

    [Theory]
    [InlineData("something_the_idp_invented")]
    [InlineData("access_denied\nforged log line")]
    [InlineData(null)]
    public void Anything_outside_RFC_6749_collapses_to_one_code(string? error)
    {
        var code = Loggable.OAuthErrorCode(error);

        Assert.Equal("unrecognised_error", code);
        Assert.DoesNotContain('\n', code);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    [InlineData("none")]
    public void A_fetch_site_the_specification_defines_is_named(string site)
    {
        Assert.Equal(site, Loggable.NamedFetchSite(site));
    }

    [Theory]
    [InlineData("cross-site\n[12:00:00] [INF] Fake: an administrator logged in")]
    [InlineData("Same-Origin")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_fetch_site_is_not_echoed(string? site)
    {
        var named = Loggable.NamedFetchSite(site);

        Assert.Equal("an unrecognised value", named);
        Assert.DoesNotContain('\n', named);
    }

    /// <summary>
    /// A username and a group name are not drawn from a closed set — at a provider that
    /// lets people choose their own display name they are whatever that person typed — and
    /// they have to reach the log, because an operator working out why somebody landed in
    /// the wrong libraries needs the name the provider sent.
    /// </summary>
    [Theory]
    [InlineData("ben\n[12:00:00] [INF] SSO sync for user x: admin=True", "ben")]
    [InlineData("ben\r\nfake", "ben")]
    [InlineData("ben\u2028fake", "ben")]
    [InlineData("ben\tfake", "ben")]
    public void A_name_from_a_token_reaches_the_log_on_one_line(string name, string keeps)
    {
        var line = Loggable.OneLine(name);

        Assert.StartsWith(keeps, line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\u2028', line);
        Assert.DoesNotContain('\t', line);
    }

    [Fact]
    public void An_ordinary_name_is_written_as_it_is()
    {
        Assert.Equal("Ben Müller-O'Neill", Loggable.OneLine("Ben Müller-O'Neill"));
        Assert.Equal(string.Empty, Loggable.OneLine((string?)null));
    }

    [Fact]
    public void A_name_long_enough_to_fill_a_log_is_cut()
    {
        var line = Loggable.OneLine(new string('a', 5_000));

        Assert.Equal(Loggable.MaxLoggedLength + 1, line.Length);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_group_that_arrived_is_named_on_that_one_line()
    {
        Assert.Equal("media, kids", Loggable.OneLine(new[] { "media", "kids" }));
        Assert.DoesNotContain('\n', Loggable.OneLine(new[] { "media\nfake", "kids" }));
    }

    public void Dispose() => _harness.Dispose();
}
