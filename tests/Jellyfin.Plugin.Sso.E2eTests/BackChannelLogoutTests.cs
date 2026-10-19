using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Until back-channel logout existed, a session the identity provider had ended lived on
/// in Jellyfin until the next login. These tests sign a user in for real, then let the
/// provider say the session is over, and check that the token the user holds stops working.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class BackChannelLogoutTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public BackChannelLogoutTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private string Endpoint(string? providerId = null)
        => $"{_env.BaseUrl}/sso/oidc/backchannel-logout/{providerId ?? JellyfinEnvironment.ProviderId}";

    private static HttpContent Form(string logoutToken)
        => new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken });

    /// <summary>Signs a user in through SSO and returns the token their browser would hold.</summary>
    private async Task<string> SignInAsync(string sub, string username)
    {
        var login = await _env.StartWebLoginAsync(new FakeUser(sub, username, []));
        Assert.True(login.Succeeded, login.Body);

        var response = await _env.AuthenticateAsync(username, login.Secret!);
        response.EnsureSuccessStatusCode();

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return payload["AccessToken"]!.GetValue<string>();
    }

    private async Task<HttpStatusCode> WhoAmIAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/Users/Me");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);

        using var client = JellyfinEnvironment.TrustingClient();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_logout_the_provider_announces_ends_the_session_it_names()
    {
        var token = await SignInAsync("sub-logout", "ben-logout");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(
            Endpoint(),
            Form(_env.Idp.MintLogoutToken("sub-logout", JellyfinEnvironment.ClientId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        // The token the browser holds is gone, not merely stale.
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(token));
    }

    [Fact]
    public async Task Another_users_logout_leaves_this_session_alone()
    {
        var token = await SignInAsync("sub-keeps", "ben-keeps");

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(
            Endpoint(),
            Form(_env.Idp.MintLogoutToken("sub-somebody-else", JellyfinEnvironment.ClientId)));

        // A provider announces a logout to every application the person used, including
        // the ones that never saw them: nothing to do is not an error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));
    }

    [Fact]
    public async Task A_token_signed_by_nobody_the_provider_knows_is_refused()
    {
        var token = await SignInAsync("sub-forged", "ben-forged");

        // A token for the right subject, signed by a key this provider never published.
        using var stranger = new ForeignIdentityProvider();

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(
            Endpoint(),
            Form(stranger.MintLogoutToken(
                "sub-forged", JellyfinEnvironment.ClientId, _env.Idp.ContainerAuthority)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));
    }

    [Fact]
    public async Task A_request_without_a_token_is_refused()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(Endpoint(), Form(string.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_provider_nobody_configured_has_no_such_endpoint()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(
            Endpoint("no-such-provider"),
            Form(_env.Idp.MintLogoutToken("sub-logout", JellyfinEnvironment.ClientId)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Turning_the_switch_off_closes_the_endpoint()
    {
        var token = await SignInAsync("sub-switched-off", "ben-switched-off");

        await _env.PatchConfigAsync(config =>
            config["Providers"]![0]!["AcceptBackChannelLogout"] = false);

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(
            Endpoint(),
            Form(_env.Idp.MintLogoutToken("sub-switched-off", JellyfinEnvironment.ClientId)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));
    }
}
