using System.Net;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Which addresses the plugin will fetch from when somebody else chose the address. The
/// logo comes out of a provider's brand document and the profile picture out of a token
/// claim that is user-editable on several providers, so both are somebody else's text.
/// A request the server makes reaches what the server can reach, and both of these hand
/// the bytes back out — the logo through an endpoint anybody can load, the picture through
/// the user's own profile.
/// </summary>
public sealed class OutboundAddressesTests
{
    private static readonly Uri Authority = new("https://idp.example.com");

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.50")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:192.168.1.50")]
    public void An_address_that_only_means_something_from_inside_is_not_public(string ip)
        => Assert.False(OutboundAddresses.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("192.167.1.1")]
    [InlineData("100.128.0.1")]
    [InlineData("203.0.113.10")]
    [InlineData("2606:4700::1111")]
    public void An_ordinary_internet_address_is_public(string ip)
        => Assert.True(OutboundAddresses.IsPublic(IPAddress.Parse(ip)));

    /// <summary>
    /// The provider's own host is allowed wherever it points. A household identity provider
    /// is usually on the same network as Jellyfin, and the operator chose that address.
    /// </summary>
    [Theory]
    [InlineData("https://idp.example.com/media/avatar.png")]
    [InlineData("http://idp.example.com:9000/x.png")]
    [InlineData("https://IDP.EXAMPLE.COM/x.png")]
    public async Task The_providers_own_host_is_allowed(string url)
        => Assert.True(await OutboundAddresses.AllowedAsync(
            new Uri(url), Authority, allowPrivateNetworks: false));

    [Theory]
    [InlineData("http://127.0.0.1:3000/render.png")]
    [InlineData("http://10.89.0.7:3000/render.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://vaultwarden/icon.png")]
    public async Task Somewhere_else_inside_the_network_is_refused(string url)
        => Assert.False(await OutboundAddresses.AllowedAsync(
            new Uri(url), Authority, allowPrivateNetworks: false));

    /// <summary>The operator can say yes, for a setup that serves pictures from the LAN.</summary>
    [Fact]
    public async Task The_operator_can_allow_the_private_network()
        => Assert.True(await OutboundAddresses.AllowedAsync(
            new Uri("http://10.89.0.7:3000/render.png"), Authority, allowPrivateNetworks: true));

    /// <summary>
    /// A name nothing answers for is refused rather than waved through. Allowing it would
    /// mean a name that resolves to something private a moment later got a free pass.
    /// </summary>
    [Fact]
    public async Task A_name_that_resolves_to_nothing_is_refused()
        => Assert.False(await OutboundAddresses.AllowedAsync(
            new Uri("https://nothing.invalid/x.png"), Authority, allowPrivateNetworks: false));

    [Fact]
    public async Task With_no_provider_in_hand_only_public_addresses_remain()
        => Assert.False(await OutboundAddresses.AllowedAsync(
            new Uri("http://192.168.1.50/x.png"), authority: null, allowPrivateNetworks: false));
}
