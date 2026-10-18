using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The three flows around the login itself: choosing between several providers, letting a
/// signed-in user attach their own identity, and ending the provider session on logout.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class SessionAndLinkingTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public SessionAndLinkingTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Provider picker ──────────────────────────────────────────────────────

    [Fact]
    public async Task With_one_provider_the_stable_entry_point_goes_straight_to_it()
    {
        // The redirect itself is the assertion, so it must not be followed.
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/start");

        // A redirect to the provider, not a page asking which one.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/authorize", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task With_several_providers_the_user_is_asked_which_one()
    {
        await _env.AddSecondProviderAsync();

        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/start");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Fake IdP", body);
        Assert.Contains("Second IdP", body);
        Assert.Contains($"/sso/oidc/start/{JellyfinEnvironment.ProviderId}", body);
        Assert.Contains($"/sso/oidc/start/{JellyfinEnvironment.SecondProviderId}", body);
    }

    [Fact]
    public async Task The_quick_connect_picker_keeps_the_code()
    {
        await _env.AddSecondProviderAsync();

        using var client = JellyfinEnvironment.TrustingClient();
        var body = await client.GetStringAsync($"{_env.BaseUrl}/sso/oidc/start/qc?code=ABC123");

        Assert.Contains($"/sso/oidc/start/{JellyfinEnvironment.ProviderId}?code=ABC123", body);
    }

    [Fact]
    public async Task A_login_through_the_second_provider_is_linked_to_that_provider()
    {
        await _env.AddSecondProviderAsync();

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-second", "seconduser", ["media"]),
            providerId: JellyfinEnvironment.SecondProviderId);
        Assert.True(login.Succeeded, login.Body);

        var link = Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
        Assert.Equal(JellyfinEnvironment.SecondProviderId, link!["ProviderId"]!.GetValue<string>());
    }

    // ─── Self-service linking ─────────────────────────────────────────────────

    [Fact]
    public async Task A_signed_in_user_can_link_their_own_identity()
    {
        var (userId, token) = await _env.CreateLocalUserWithTokenAsync("selflinker");

        var authorizeUrl = await StartSelfServiceLinkAsync(token);
        _env.Idp.NextUser = new FakeUser("sub-selflinker", "selflinker", ["media"]);
        var result = await FollowAsync(authorizeUrl);

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Contains("Account linked", result.Body);

        var link = Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
        Assert.Equal("sub-selflinker", link!["Sub"]!.GetValue<string>());
        Assert.Equal(userId, Guid.Parse(link["JellyfinUserId"]!.GetValue<string>()));

        // Linking alone grants nothing; the policy is applied on the next SSO login.
        var policy = await _env.UserPolicyAsync("selflinker");
        Assert.Empty(policy["EnabledFolders"]!.AsArray());
    }

    [Fact]
    public async Task A_linked_account_can_then_sign_in_with_sso()
    {
        var (_, token) = await _env.CreateLocalUserWithTokenAsync("laterlogin");
        var authorizeUrl = await StartSelfServiceLinkAsync(token);
        _env.Idp.NextUser = new FakeUser("sub-laterlogin", "laterlogin", ["media"]);
        await FollowAsync(authorizeUrl);

        // The IdP's preferred_username differs on purpose: the link, not the name, decides.
        var login = await _env.StartWebLoginAsync(new FakeUser("sub-laterlogin", "someone-else", ["media"]));

        Assert.True(login.Succeeded, login.Body);
        var auth = await _env.AuthenticateAsync("laterlogin", login.Secret!);
        Assert.Equal(HttpStatusCode.OK, auth.StatusCode);
        Assert.Null(await _env.FindUserAsync("someone-else"));
    }

    [Fact]
    public async Task An_identity_already_linked_elsewhere_is_refused()
    {
        // First user takes the identity through a normal SSO login.
        await _env.StartWebLoginAsync(new FakeUser("sub-contested", "firstowner", ["media"]));

        var (_, token) = await _env.CreateLocalUserWithTokenAsync("secondclaimant");
        var authorizeUrl = await StartSelfServiceLinkAsync(token);
        _env.Idp.NextUser = new FakeUser("sub-contested", "firstowner", ["media"]);

        var result = await FollowAsync(authorizeUrl);

        Assert.Contains("already linked to another Jellyfin account", result.Body);
        var links = (await _env.PluginConfigAsync())["Links"]!.AsArray();
        Assert.Single(links);
    }

    [Fact]
    public async Task Self_service_linking_needs_a_jellyfin_session()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.PostAsync(
            $"{_env.BaseUrl}/sso/link/start?providerId={JellyfinEnvironment.ProviderId}", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_user_can_remove_their_own_link()
    {
        var (_, token) = await _env.CreateLocalUserWithTokenAsync("unlinker");
        var authorizeUrl = await StartSelfServiceLinkAsync(token);
        _env.Idp.NextUser = new FakeUser("sub-unlinker", "unlinker", ["media"]);
        await FollowAsync(authorizeUrl);

        using var client = JellyfinEnvironment.TrustingClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_env.BaseUrl}/sso/link/self");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task The_self_service_page_is_served_only_while_the_feature_is_on()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{_env.BaseUrl}/sso/link")).StatusCode);

        await _env.PatchConfigAsync(config => config["AllowSelfServiceLinking"] = false);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{_env.BaseUrl}/sso/link")).StatusCode);
    }

    // ─── Provider logout ──────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_stays_local_unless_it_is_switched_on()
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/web/index.html", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Logout_ends_the_provider_session_when_switched_on()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"{_env.Idp.BrowserAuthority}/endsession", location);
        // The provider has to know where to send the user back.
        Assert.Contains($"post_logout_redirect_uri={Uri.EscapeDataString($"{_env.BaseUrl}/web/index.html")}", location);

        // And following it actually comes back to Jellyfin.
        var endedAt = _env.Idp.SessionsEnded;
        var back = await FollowAsync(location);
        Assert.Equal(endedAt + 1, _env.Idp.SessionsEnded);
        Assert.Equal(HttpStatusCode.OK, back.Status);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> StartSelfServiceLinkAsync(string token)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_env.BaseUrl}/sso/link/start?providerId={JellyfinEnvironment.ProviderId}");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonNode.Parse(body)!["AuthorizeUrl"]!.GetValue<string>();
    }

    private static async Task<(HttpStatusCode Status, string Body)> FollowAsync(string url)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        HttpResponseMessage response;

        for (var hop = 0; ; hop++)
        {
            response = await client.GetAsync(url);
            if (response.Headers.Location is null || hop == 10)
            {
                break;
            }

            url = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location.ToString()
                : new Uri(new Uri(url), response.Headers.Location).ToString();
        }

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
