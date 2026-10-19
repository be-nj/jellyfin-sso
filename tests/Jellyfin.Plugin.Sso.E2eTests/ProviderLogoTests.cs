using System.Net;

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
    public async Task An_unknown_provider_has_no_mark_to_serve()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/icon/provider/not-a-provider");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
