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

    [Theory]
    // Anything that is not a path is dropped: these would turn a link back into Jellyfin
    // into a link to somebody else's site, and the header can be set by whoever asks.
    [InlineData("https://example.invalid")]
    [InlineData("//example.invalid")]
    [InlineData("/\\example.invalid")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/jellyfin\r\nX-Injected: 1")]
    [InlineData("/jellyfin<script>")]
    [InlineData("http://example.invalid/jellyfin")]
    // The apostrophe ends a JavaScript string literal, and this value is written into
    // several of them in the pages the plugin serves. A path does not need one.
    [InlineData("/'-alert(document.domain)-'")]
    [InlineData("/x';fetch('//evil.invalid')")]
    // The remaining sub-delimiters go the same way: legal in a path, unnecessary here, and
    // each one is a character fewer to reason about in a page.
    [InlineData("/a(b)")]
    [InlineData("/a;b")]
    [InlineData("/a,b")]
    [InlineData("/a=b")]
    [InlineData("/a&b")]
    [InlineData("/a@b")]
    public void A_forwarded_prefix_that_is_not_a_path_is_ignored(string hostile)
        => Assert.Equal(string.Empty, ForwardedPaths.Prefix(hostile, null));

    /// <summary>A path that genuinely needs one of those characters percent-encodes it.</summary>
    [Fact]
    public void A_percent_encoded_prefix_is_still_a_path()
        => Assert.Equal("/caf%C3%A9", ForwardedPaths.Prefix("/caf%C3%A9", null));

    [Fact]
    public void A_hostile_forwarded_prefix_does_not_hide_the_servers_own()
        => Assert.Equal("/jellyfin", ForwardedPaths.Prefix("https://example.invalid", "/jellyfin"));

    [Theory]
    [InlineData("jellyfin.example.com", "jellyfin.example.com")]
    [InlineData("jellyfin.example.com:8096", "jellyfin.example.com:8096")]
    [InlineData("[2001:db8::1]:8096", "[2001:db8::1]:8096")]
    [InlineData("example.invalid/path", "fallback:8096")]
    [InlineData("example.invalid\\@evil", "fallback:8096")]
    [InlineData("", "fallback:8096")]
    public void Only_something_shaped_like_a_host_is_taken_from_the_header(
        string forwarded, string expected)
        => Assert.Equal(expected, ForwardedPaths.Host(forwarded, "fallback:8096"));

    [Theory]
    [InlineData("https", "https")]
    [InlineData("http", "http")]
    [InlineData("javascript", "https")]
    [InlineData("HTTPS", "https")]
    [InlineData(null, "https")]
    public void Only_http_and_https_are_taken_from_the_header(string? forwarded, string expected)
        => Assert.Equal(expected, ForwardedPaths.Scheme(forwarded, "https"));

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
