using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Every address the plugin hands out is built from these two values. A wrong sub-path
/// does not degrade anything — it produces a redirect URI the provider rejects, or links
/// that 404, and the household sees only "login failed".
/// </summary>
public sealed class ForwardedPathsTests
{
    [Theory]
    [InlineData(null, null, "")]
    [InlineData(null, "", "")]
    [InlineData(null, "/", "")]
    [InlineData(null, "/jellyfin", "/jellyfin")]
    [InlineData(null, "/jellyfin/", "/jellyfin")]
    [InlineData(null, "jellyfin", "/jellyfin")]
    [InlineData("/media/jf", "/jellyfin", "/media/jf")]
    [InlineData("/media/jf/", null, "/media/jf")]
    public void The_sub_path_is_normalised(string? forwarded, string? pathBase, string expected)
        => Assert.Equal(expected, ForwardedPaths.Prefix(forwarded, pathBase));

    [Fact]
    public void A_proxy_prefix_wins_over_the_servers_own()
        => Assert.Equal("/outside", ForwardedPaths.Prefix("/outside", "/inside"));

    [Fact]
    public void The_base_uri_carries_scheme_host_and_sub_path()
        => Assert.Equal(
            "https://jellyfin.example.com/jellyfin",
            ForwardedPaths.BaseUri("https", "jellyfin.example.com", "/jellyfin"));

    [Fact]
    public void A_root_install_gets_no_trailing_slash()
        => Assert.Equal(
            "http://192.168.1.10:8096",
            ForwardedPaths.BaseUri("http", "192.168.1.10:8096", string.Empty));
}
