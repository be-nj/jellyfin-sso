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

        using var browser = JellyfinEnvironment.TrustingClient();
        var authorizeUrl = await StartSelfServiceLinkAsync(token, browser);
        _env.Idp.NextUser = new FakeUser("sub-selflinker", "selflinker", ["media"]);
        var result = await FollowAsync(authorizeUrl, browser);

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
        using var browser = JellyfinEnvironment.TrustingClient();
        var authorizeUrl = await StartSelfServiceLinkAsync(token, browser);
        _env.Idp.NextUser = new FakeUser("sub-laterlogin", "laterlogin", ["media"]);
        await FollowAsync(authorizeUrl, browser);

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
        using var browser = JellyfinEnvironment.TrustingClient();
        var authorizeUrl = await StartSelfServiceLinkAsync(token, browser);
        _env.Idp.NextUser = new FakeUser("sub-contested", "firstowner", ["media"]);

        var result = await FollowAsync(authorizeUrl, browser);

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
        using var browser = JellyfinEnvironment.TrustingClient();
        var authorizeUrl = await StartSelfServiceLinkAsync(token, browser);
        _env.Idp.NextUser = new FakeUser("sub-unlinker", "unlinker", ["media"]);
        await FollowAsync(authorizeUrl, browser);

        var response = await UnlinkAsync(token, JellyfinEnvironment.ProviderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    /// <summary>
    /// Back-channel logout finds an account through its link and nothing else, so an
    /// account that unlinked itself used to keep tokens the provider could no longer end.
    /// One authenticated request, no administrator rights, and a disabled account at the
    /// provider revoked "0 account(s)" while the tokens kept working — Jellyfin's do not
    /// expire. Unlinking ends them now.
    /// </summary>
    [Fact]
    public async Task Unlinking_ends_the_sessions_it_would_otherwise_orphan()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-orphan", "orphanuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var auth = await _env.AuthenticateAsync("orphanuser", login.Secret!);
        var token = JsonNode.Parse(await auth.Content.ReadAsStringAsync())!["AccessToken"]!
            .GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));

        var response = await UnlinkAsync(token, JellyfinEnvironment.ProviderId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The token that asked for the unlink is gone with the link.
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(token));
    }

    /// <summary>
    /// The same reasoning as the self-service unlink above, on the endpoint the Links tab
    /// calls — which did not do it. An administrator unlinking somebody is usually cutting
    /// off their access, and that is the reading under which leaving their tokens alive
    /// matters most: back-channel logout finds an account through its link, so once the
    /// link is gone nothing can end those sessions and Jellyfin does not expire them.
    /// </summary>
    [Fact]
    public async Task An_administrators_unlink_ends_the_sessions_too()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-cutoff", "cutoffuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var auth = await _env.AuthenticateAsync("cutoffuser", login.Secret!);
        var token = JsonNode.Parse(await auth.Content.ReadAsStringAsync())!["AccessToken"]!
            .GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(token));

        var user = await _env.FindUserAsync("cutoffuser");
        await _env.RemoveLinkAsync(Guid.Parse(user!["Id"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(token));
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task Unlinking_needs_the_provider_named()
    {
        var (_, token) = await _env.CreateLocalUserWithTokenAsync("vague");

        var response = await UnlinkAsync(token, providerId: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unlinking_is_refused_while_self_service_linking_is_off()
    {
        var (_, token) = await _env.CreateLocalUserWithTokenAsync("switchedoff");
        await _env.PatchConfigAsync(config => config["AllowSelfServiceLinking"] = false);

        var response = await UnlinkAsync(token, JellyfinEnvironment.ProviderId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    /// <summary>
    /// A page on a sibling subdomain reports "same-site", not "cross-site". Rejecting only
    /// the latter left the logout drivable by exactly the adversary the cookie prefix is
    /// written against.
    /// </summary>
    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    public async Task A_logout_driven_from_anywhere_but_this_site_is_ignored(string site)
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/sso/oidc/logout");
        request.Headers.Add("Sec-Fetch-Site", site);

        var endedAt = _env.Idp.SessionsEnded;
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/web/index.html", response.Headers.Location!.ToString());
        Assert.Equal(endedAt, _env.Idp.SessionsEnded);
    }

    /// <summary>A browser's own address bar reports "none"; that is not another site.</summary>
    [Fact]
    public async Task A_logout_typed_into_the_address_bar_is_honoured()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/sso/oidc/logout");
        request.Headers.Add("Sec-Fetch-Site", "none");

        using var response = await client.SendAsync(request);

        Assert.StartsWith(
            $"{_env.Idp.ContainerAuthority}/endsession",
            response.Headers.Location!.ToString());
    }

    /// <summary>The navigation the documentation describes still works.</summary>
    [Fact]
    public async Task A_logout_from_the_web_client_itself_is_honoured()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/sso/oidc/logout");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            $"{_env.Idp.ContainerAuthority}/endsession",
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Logout_ends_the_provider_session_when_switched_on()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"{_env.Idp.ContainerAuthority}/endsession", location);
        Assert.DoesNotContain("id_token_hint", location);
        // The provider has to know where to send the user back.
        Assert.Contains($"post_logout_redirect_uri={Uri.EscapeDataString($"{_env.BaseUrl}/web/index.html")}", location);

        // And following it actually comes back to Jellyfin.
        var endedAt = _env.Idp.SessionsEnded;
        var back = await FollowAsync(location);
        Assert.Equal(endedAt + 1, _env.Idp.SessionsEnded);
        Assert.Equal(HttpStatusCode.OK, back.Status);
    }

    /// <summary>
    /// A server published under a sub-path. The return URL used to carry that prefix
    /// twice, because the origin already ends with it and the home path already begins
    /// with it — and a provider refuses a post-logout redirect it has not been given,
    /// which aborts the logout after the retained token has already been spent. Every
    /// other assertion on this value runs at the root, which is why nobody noticed.
    /// </summary>
    [Fact]
    public async Task The_return_path_carries_the_sub_path_once()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/sso/oidc/logout");
        request.Headers.Add("X-Forwarded-Prefix", "/jellyfin");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Uri.UnescapeDataString(response.Headers.Location!.ToString());

        Assert.Contains("/jellyfin/web/index.html", location, StringComparison.Ordinal);
        Assert.DoesNotContain("/jellyfin/jellyfin", location, StringComparison.Ordinal);
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

    /// <summary>
    /// The escalation this binding exists to stop: an account holder asks for an authorize
    /// URL, which carries their own user id, and gets somebody else to finish it. Without
    /// the cookie the callback would attach the stranger's identity to the asking account,
    /// and that account would inherit the stranger's groups on their next login.
    /// </summary>
    [Fact]
    public async Task A_link_flow_finished_in_another_browser_is_refused()
    {
        var (_, token) = await _env.CreateLocalUserWithTokenAsync("linkthief");

        using var attacker = JellyfinEnvironment.TrustingClient();
        var authorizeUrl = await StartSelfServiceLinkAsync(token, attacker);

        // The victim's browser: same URL, no cookie from the attacker's session.
        _env.Idp.NextUser = new FakeUser("sub-victim", "victim", ["media"]);
        using var victim = JellyfinEnvironment.TrustingClient();
        var result = await FollowAsync(authorizeUrl, victim);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Contains("this browser started", result.Body);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    /// <summary>
    /// The same gap on the plain login path: an attacker completes their own login in
    /// somebody else's browser, which would otherwise sign that browser in as them.
    /// </summary>
    [Fact]
    public async Task A_login_callback_opened_in_another_browser_is_refused()
    {
        using var attacker = JellyfinEnvironment.TrustingClient(followRedirects: false);
        _env.Idp.NextUser = new FakeUser("sub-forcer", "forcer", ["media"]);

        // Walk as far as the callback, then hand that URL to a browser that has no cookie.
        var url = $"{_env.BaseUrl}/sso/oidc/start/{JellyfinEnvironment.ProviderId}";
        string? callbackUrl = null;
        for (var hop = 0; hop < 10 && url is not null; hop++)
        {
            using var response = await attacker.GetAsync(url);
            var next = response.Headers.Location;
            if (next is null)
            {
                break;
            }

            url = next.IsAbsoluteUri ? next.ToString() : new Uri(new Uri(url), next).ToString();
            if (url.Contains("/sso/oidc/callback/", StringComparison.Ordinal))
            {
                callbackUrl = url;
                break;
            }
        }

        Assert.NotNull(callbackUrl);

        using var victim = JellyfinEnvironment.TrustingClient();
        var result = await FollowAsync(callbackUrl, victim);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.DoesNotContain("AuthenticateByName", result.Body);
    }

    /// <summary>
    /// Starts the link flow and returns where to send the browser. The caller passes the
    /// client it will walk that URL with, because the two steps are one browser: the
    /// plugin sets a cookie here and requires it back at the callback, so a helper with a
    /// cookie jar of its own would be testing something no browser does.
    /// </summary>
    private async Task<string> StartSelfServiceLinkAsync(string token, HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_env.BaseUrl}/sso/link/start?providerId={JellyfinEnvironment.ProviderId}");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonNode.Parse(body)!["AuthorizeUrl"]!.GetValue<string>();
    }

    private async Task<HttpResponseMessage> UnlinkAsync(string token, string? providerId)
    {
        var url = $"{_env.BaseUrl}/sso/link/self"
            + (providerId is null ? string.Empty : $"?providerId={providerId}");

        using var client = JellyfinEnvironment.TrustingClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);
        return await client.SendAsync(request);
    }

    private async Task<HttpStatusCode> WhoAmIAsync(string token)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/Users/Me");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    // ─── What the provider says about itself ──────────────────────────────────

    /// <summary>
    /// A logout sends the user's own ID token to the end-session endpoint as an
    /// id_token_hint, so that address is one the plugin hands something to — and the host
    /// check that exists for exactly those addresses was written down, tested, and then
    /// called with three of the four. The test passed because it exercised the rule
    /// instead of the call. This one exercises the call: a provider whose document moves
    /// that endpoint cannot be used at all, which is the same answer the other three give.
    ///
    /// Under its own provider id, because a discovery document is cached for half an hour
    /// per id and authority — so a test that reused the suite's provider would pass or
    /// fail depending on which test ran first.
    /// </summary>
    [Fact]
    public async Task A_document_that_moves_the_end_session_endpoint_is_refused()
    {
        const string ProviderId = "moved-end-session";
        _env.Idp.EndSessionElsewhere = true;
        try
        {
            await _env.PatchConfigAsync(config => config["Providers"]!.AsArray().Add(
                new JsonObject
                {
                    ["Id"] = ProviderId,
                    ["Name"] = "Provider with a moved end-session endpoint",
                    ["Enabled"] = true,
                    ["Authority"] = _env.Idp.ContainerAuthority,
                    ["ClientId"] = JellyfinEnvironment.ClientId,
                    ["Scopes"] = new JsonArray { "openid", "profile", "email", "groups" },
                    ["GroupsClaim"] = "groups",
                }));

            using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
            var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/start/{ProviderId}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(
                "could not be reached or is not configured correctly",
                await response.Content.ReadAsStringAsync());
        }
        finally
        {
            _env.Idp.EndSessionElsewhere = false;
        }
    }

    /// <summary>
    /// OIDC Core §5.3.2: the sub in a userinfo response has to be verified against the one
    /// in the ID token, and a response that names another subject must not be used. What
    /// is read from there decides a user's groups, which is to say their libraries and
    /// their administrator flag.
    /// </summary>
    [Fact]
    public async Task A_userinfo_answer_about_somebody_else_is_not_used()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]![0]!["GroupsFromUserInfo"] = true);
        _env.Idp.UserInfoAnswersAboutSomebodyElse = true;
        try
        {
            var login = await _env.StartWebLoginAsync(
                new FakeUser("sub-mixed-up", "mixedup", ["jellyfin-admins"]));

            Assert.False(login.Succeeded, login.Body);
            Assert.Null(await _env.FindUserAsync("mixedup"));
            Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
        }
        finally
        {
            _env.Idp.UserInfoAnswersAboutSomebodyElse = false;
        }
    }

    /// <summary>Walks a redirect chain by hand, keeping one client — and one cookie jar.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> FollowAsync(
        string url, HttpClient? client = null)
    {
        using var own = client is null ? JellyfinEnvironment.TrustingClient() : null;
        client ??= own!;
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
