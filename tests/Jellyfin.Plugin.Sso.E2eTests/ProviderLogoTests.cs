using System.Net;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The login button carries the logo the provider shows on its own login page, so a
/// household that has rebranded its Authentik sees its own mark in Jellyfin too. The
/// server fetches it; the visitor's browser never reaches the provider before they have
/// chosen to sign in.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class ProviderLogoTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public ProviderLogoTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_providers_own_logo_is_served_when_it_has_one()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.GetAsync(
            $"{_env.BaseUrl}/sso/icon/provider/{JellyfinEnvironment.ProviderId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    [Fact]
    public async Task The_built_in_mark_is_served_when_the_setting_is_off()
    {
        await _env.PatchConfigAsync(config => config["UseProviderLogos"] = false);

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.GetAsync(
            $"{_env.BaseUrl}/sso/icon/provider/{JellyfinEnvironment.ProviderId}");

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_square_brand_image_is_preferred_over_the_wide_one()
    {
        // A provider publishes two brand images: a favicon, which is meant to be a square
        // mark, and a logo, which is the wide lockup from the top of its login page.
        // Authentik's own default logo is four times as wide as it is tall, and in a
        // mark-sized box that was a coloured dash rather than a mark.
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.GetAsync(
            $"{_env.BaseUrl}/sso/icon/provider/{JellyfinEnvironment.ProviderId}");

        // The fake publishes the favicon as PNG and the logo as GIF.
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_provider_with_only_a_wide_logo_still_gets_its_own_mark()
    {
        const string ProviderId = "logo-only-provider";

        _env.Idp.ServeFavicon = false;
        try
        {
            // A fresh provider id, because a logo already fetched is cached under the old one.
            await _env.PatchConfigAsync(config => config["Providers"]!.AsArray().Add(new JsonObject
            {
                ["Id"] = ProviderId,
                ["Name"] = "Logo Only",
                ["Enabled"] = true,
                ["Authority"] = _env.Idp.ContainerAuthority,
                ["ClientId"] = JellyfinEnvironment.ClientId,
            }));

            using var client = JellyfinEnvironment.TrustingClient();
            var response = await client.GetAsync($"{_env.BaseUrl}/sso/icon/provider/{ProviderId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/gif", response.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            _env.Idp.ServeFavicon = true;
        }
    }

    [Fact]
    public async Task An_unknown_provider_has_no_mark_to_serve()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/icon/provider/not-a-provider");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
