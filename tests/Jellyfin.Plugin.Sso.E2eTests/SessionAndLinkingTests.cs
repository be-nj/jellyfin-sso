using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.WebUtilities;

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

    // ─── Administrator linking ────────────────────────────────────────────────

    [Fact]
    public async Task An_administrator_links_an_account_by_subject()
    {
        var (userId, _) = await _env.CreateLocalUserWithTokenAsync("ben-linked");

        var response = await PostLinkAsync(JellyfinEnvironment.ProviderId, "sub-42", userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var link = Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
        Assert.Equal("sub-42", link!["Sub"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_link_to_a_user_Jellyfin_does_not_have_is_refused()
    {
        var response = await PostLinkAsync(JellyfinEnvironment.ProviderId, "sub-42", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task A_link_to_a_provider_that_does_not_exist_is_refused()
    {
        var (userId, _) = await _env.CreateLocalUserWithTokenAsync("ben-unknown-provider");

        var response = await PostLinkAsync("no-such-provider", "sub-42", userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task One_account_cannot_hold_two_identities_of_the_same_provider()
    {
        var (userId, _) = await _env.CreateLocalUserWithTokenAsync("ben-twice");
        await PostLinkAsync(JellyfinEnvironment.ProviderId, "sub-42", userId);

        var response = await PostLinkAsync(JellyfinEnvironment.ProviderId, "sub-43", userId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task The_same_identity_cannot_be_linked_twice()
    {
        var (first, _) = await _env.CreateLocalUserWithTokenAsync("ben-first");
        var (second, _) = await _env.CreateLocalUserWithTokenAsync("ben-second");
        await PostLinkAsync(JellyfinEnvironment.ProviderId, "sub-42", first);

        var response = await PostLinkAsync(JellyfinEnvironment.ProviderId, "sub-42", second);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    private async Task<HttpResponseMessage> PostLinkAsync(
        string providerId, string sub, Guid jellyfinUserId)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(_env.AdminToken);

        return await client.PostAsJsonAsync(
            $"{_env.BaseUrl}/sso/admin/links",
            new { ProviderId = providerId, Sub = sub, JellyfinUserId = jellyfinUserId });
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
        Assert.DoesNotContain("id_token_hint", location);
        // The provider has to know where to send the user back.
        Assert.Contains($"post_logout_redirect_uri={Uri.EscapeDataString($"{_env.BaseUrl}/web/index.html")}", location);

        // And following it actually comes back to Jellyfin.
        var endedAt = _env.Idp.SessionsEnded;
        var back = await FollowAsync(location);
        Assert.Equal(endedAt + 1, _env.Idp.SessionsEnded);
        Assert.Equal(HttpStatusCode.OK, back.Status);
    }

    [Fact]
    public async Task Logout_sends_the_browser_login_id_token_once_after_Jellyfin_logout()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-logout-hint", "logouthint", ["media"]), client);
        Assert.True(login.Succeeded, login.Body);
        var auth = await _env.AuthenticateAsync("logouthint", login.Secret!);
        var token = JsonNode.Parse(await auth.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
        using var logout = new HttpRequestMessage(HttpMethod.Post, $"{_env.BaseUrl}/Sessions/Logout");
        logout.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);
        (await client.SendAsync(logout)).EnsureSuccessStatusCode();

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var hint = query["id_token_hint"].ToString();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(hint);
        Assert.Equal("sub-logout-hint", jwt.Subject);
        Assert.Contains(JellyfinEnvironment.ClientId, jwt.Audiences);
        Assert.DoesNotContain(hint, login.Body);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var repeated = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
        Assert.DoesNotContain("id_token_hint", repeated.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_uses_the_login_provider_and_never_sends_its_hint_to_another(bool explicitOther)
    {
        await _env.AddSecondProviderAsync();
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-second-logout", "secondlogout", ["media"]), client,
            providerId: JellyfinEnvironment.SecondProviderId);
        Assert.True(login.Succeeded, login.Body);

        var endpoint = $"{_env.BaseUrl}/sso/oidc/logout"
            + (explicitOther ? $"/{JellyfinEnvironment.ProviderId}" : "");
        var response = await client.GetAsync(endpoint);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(
            JellyfinEnvironment.ClientId + (explicitOther ? "" : "-2"),
            query["client_id"].ToString());
        Assert.Equal(!explicitOther, query.ContainsKey("id_token_hint"));
    }

    [Fact]
    public async Task A_later_browser_login_replaces_only_that_browsers_logout_hint()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);
        using var first = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var second = JellyfinEnvironment.TrustingClient(followRedirects: false);
        Assert.True((await _env.StartWebLoginAsync(
            new FakeUser("sub-original", "original", ["media"]), first)).Succeeded);
        Assert.True((await _env.StartWebLoginAsync(
            new FakeUser("sub-other-browser", "otherbrowser", ["media"]), second)).Succeeded);
        Assert.True((await _env.StartWebLoginAsync(
            new FakeUser("sub-replacement", "replacement", ["media"]), first)).Succeeded);

        foreach (var (client, sub) in new[] { (first, "sub-replacement"), (second, "sub-other-browser") })
        {
            var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
            var hint = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["id_token_hint"].ToString();
            Assert.Equal(sub, new JwtSecurityTokenHandler().ReadJwtToken(hint).Subject);
        }
    }

    [Fact]
    public async Task Logout_does_not_send_a_retained_token_after_the_client_id_changes()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        Assert.True((await _env.StartWebLoginAsync(
            new FakeUser("sub-changed-client", "changedclient", ["media"]), client)).Succeeded);
        await _env.PatchConfigAsync(config =>
            config["Providers"]![0]!["ClientId"] = "replacement-client");

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);

        Assert.Equal("replacement-client", query["client_id"].ToString());
        Assert.False(query.ContainsKey("id_token_hint"));
    }

    /// <summary>
    /// A server that never signs anybody out of their provider has no use for a stored ID
    /// token, so none is kept. Switching the setting on afterwards finds nothing to send:
    /// the next login is what starts the retention.
    /// </summary>
    [Fact]
    public async Task Nothing_is_retained_while_the_provider_logout_is_switched_off()
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-no-retention", "noretention", ["media"]), client);
        Assert.True(login.Succeeded, login.Body);

        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
        Assert.DoesNotContain("id_token_hint", response.Headers.Location!.ToString());
    }

    /// <summary>
    /// And the other direction: retained with the setting on, then the setting goes off
    /// and the browser logs out locally. The token is dropped there rather than kept for
    /// a logout that may never come, so turning the setting back on sends no hint until
    /// somebody logs in again.
    /// </summary>
    [Fact]
    public async Task A_local_logout_drops_the_retained_login_rather_than_keeping_it()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        Assert.True((await _env.StartWebLoginAsync(
            new FakeUser("sub-local-logout", "locallogout", ["media"]), client)).Succeeded);

        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = false);
        var local = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
        Assert.Equal("/web/index.html", local.Headers.Location!.ToString());

        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);
        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");
        Assert.DoesNotContain("id_token_hint", response.Headers.Location!.ToString());
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
