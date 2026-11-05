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

    /// <summary>
    /// A provider that rotates its signing keys, on the logout side.
    ///
    /// The login side has had this test since a rotation stopped every login on the server
    /// (#167), and <see cref="FakeIdentityProvider.RotateSigningKey"/> was written for it.
    /// Pointing the same two lines at this endpoint was never done: the discovery document
    /// is cached for half an hour and a logout token is accepted for five minutes, so a
    /// rotation inside that window meant every logout the provider sent was refused and the
    /// sessions it had ended stayed open.
    /// </summary>
    [Fact]
    public async Task A_logout_signed_with_a_rotated_key_still_ends_the_session()
    {
        // Warms the cache with the old key, as a login does.
        var token = await SignInAsync("sub-rotated", "rotateduser");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));

        _env.Idp.RotateSigningKey();

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.PostAsync(
            Endpoint(),
            Form(_env.Idp.MintLogoutToken("sub-rotated", JellyfinEnvironment.ClientId)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(token));
    }

    private async Task<HttpStatusCode> WhoAmIAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/Users/Me");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);

        using var client = JellyfinEnvironment.TrustingClient();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Freshness was the only replay control: the same token stayed valid for as long as
    /// its iat was young, so anyone who could read one in flight — a proxy terminating
    /// TLS, a log recording request bodies — could post it again and again, knocking the
    /// user out each time they signed back in.
    /// </summary>
    [Fact]
    public async Task The_same_logout_token_cannot_be_used_twice()
    {
        var first = await SignInAsync("sub-replay", "ben-replay");
        var logoutToken = _env.Idp.MintLogoutToken("sub-replay", JellyfinEnvironment.ClientId);

        using var client = JellyfinEnvironment.TrustingClient();
        using var accepted = await client.PostAsync(Endpoint(), Form(logoutToken));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(first));

        // The user signs back in, and the captured token is posted again.
        var second = await SignInAsync("sub-replay", "ben-replay");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(second));

        using var refused = await client.PostAsync(Endpoint(), Form(logoutToken));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already been used", await refused.Content.ReadAsStringAsync());

        // The session the replay was aimed at is untouched.
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(second));
    }

    /// <summary>A second, genuine logout for the same user still works.</summary>
    [Fact]
    public async Task A_fresh_logout_token_for_the_same_user_still_ends_the_session()
    {
        await SignInAsync("sub-twice", "ben-twice");
        using var client = JellyfinEnvironment.TrustingClient();

        using var first = await client.PostAsync(
            Endpoint(), Form(_env.Idp.MintLogoutToken("sub-twice", JellyfinEnvironment.ClientId)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var again = await SignInAsync("sub-twice", "ben-twice");
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(again));

        using var second = await client.PostAsync(
            Endpoint(), Form(_env.Idp.MintLogoutToken("sub-twice", JellyfinEnvironment.ClientId)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(again));
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
