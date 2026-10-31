using System.Net;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The whole server-side chain against a real Jellyfin: redirect to the provider, code
/// exchange with PKCE, token validation, provisioning, permission sync, and Jellyfin
/// minting its own access token for the ephemeral secret.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class SsoLoginTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public SsoLoginTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_first_login_provisions_the_user_and_yields_a_working_token()
    {
        var login = await _env.StartWebLoginAsync(new FakeUser("sub-alice", "alice", ["media"]));

        Assert.True(login.Succeeded, login.Body);

        var auth = await _env.AuthenticateAsync("alice", login.Secret!);
        Assert.Equal(HttpStatusCode.OK, auth.StatusCode);

        var payload = JsonNode.Parse(await auth.Content.ReadAsStringAsync())!;
        Assert.False(string.IsNullOrWhiteSpace(payload["AccessToken"]!.GetValue<string>()));
        Assert.Equal("alice", payload["User"]!["Name"]!.GetValue<string>());

        var policy = await _env.UserPolicyAsync("alice");
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Equal([Library(_env.MoviesLibraryId)], Folders(policy));
    }

    [Fact]
    public async Task The_link_is_recorded_by_sub_so_a_rename_in_the_idp_keeps_the_account()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-bob", "bob", ["media"]));
        var created = await _env.FindUserAsync("bob");
        Assert.NotNull(created);

        // Same subject, new username at the IdP: the existing account must be reused.
        var second = await _env.StartWebLoginAsync(new FakeUser("sub-bob", "bob-renamed", ["media"]));
        Assert.True(second.Succeeded, second.Body);

        Assert.Null(await _env.FindUserAsync("bob-renamed"));
        var links = (await _env.PluginConfigAsync())["Links"]!.AsArray();
        var link = Assert.Single(links);
        Assert.Equal("sub-bob", link!["Sub"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_admin_group_grants_admin_and_all_libraries()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-root", "root", ["jellyfin-admins"]));
        Assert.True(login.Succeeded, login.Body);

        var policy = await _env.UserPolicyAsync("root");
        Assert.True(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.True(policy["EnableAllFolders"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Access_is_revoked_when_the_groups_disappear()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-carol", "carol", ["media", "jellyfin-admins"]));
        var before = await _env.UserPolicyAsync("carol");
        Assert.True(before["IsAdministrator"]!.GetValue<bool>());

        // Removed from every mapped group at the IdP, then logs in again.
        var second = await _env.StartWebLoginAsync(new FakeUser("sub-carol", "carol", []));
        Assert.True(second.Succeeded, second.Body);

        var after = await _env.UserPolicyAsync("carol");
        Assert.False(after["IsAdministrator"]!.GetValue<bool>());
        Assert.False(after["EnableAllFolders"]!.GetValue<bool>());
        Assert.Empty(Folders(after));
    }

    [Fact]
    public async Task Several_groups_add_up_to_several_libraries()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-dave", "dave", ["media", "kids"]));

        var folders = Folders(await _env.UserPolicyAsync("dave"));
        Assert.Equal(2, folders.Count);
        Assert.Contains(Library(_env.MoviesLibraryId), folders);
        Assert.Contains(Library(_env.KidsLibraryId), folders);
    }

    [Fact]
    public async Task An_unlinked_identity_cannot_take_over_an_existing_account()
    {
        await _env.CreateLocalUserAsync("localuser");

        // The IdP claims to be "localuser" but has never been linked to that account.
        var login = await _env.StartWebLoginAsync(new FakeUser("sub-attacker", "localuser", ["jellyfin-admins"]));

        Assert.False(login.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, login.CallbackStatus);

        // The local account keeps its own permissions and stays unlinked.
        var policy = await _env.UserPolicyAsync("localuser");
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task An_ephemeral_secret_works_once()
    {
        var login = await _env.StartWebLoginAsync(new FakeUser("sub-erin", "erin", ["media"]));

        var first = await _env.AuthenticateAsync("erin", login.Secret!);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await _env.AuthenticateAsync("erin", login.Secret!);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task An_ephemeral_secret_only_works_for_its_own_user()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-frank", "frank", ["media"]));
        var otherLogin = await _env.StartWebLoginAsync(new FakeUser("sub-gina", "gina", ["jellyfin-admins"]));

        var stolen = await _env.AuthenticateAsync("frank", otherLogin.Secret!);

        Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
    }

    [Fact]
    public async Task A_provider_error_is_reported_in_plain_words()
    {
        // What the provider sends when the user cancels on its consent screen.
        _env.Idp.NextAuthorizeError = "access_denied";

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-cancel", "canceller", ["media"]));

        Assert.Equal(HttpStatusCode.BadRequest, login.CallbackStatus);
        Assert.Equal("IdP returned error: access_denied", login.Body);
        Assert.Null(await _env.FindUserAsync("canceller"));
    }

    [Fact]
    public async Task A_callback_without_code_or_error_is_refused()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.GetAsync(
            $"{_env.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("neither an authorization code nor an error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_replayed_callback_state_is_refused()
    {
        var login = await _env.StartWebLoginAsync(new FakeUser("sub-heidi", "heidi", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        // Replaying a callback needs a state the plugin still knows; it consumed the only one.
        using var client = JellyfinEnvironment.TrustingClient();
        var replay = await client.GetAsync(
            $"{_env.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}?code=stale&state=stale");

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("state", await replay.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_admin_api_is_closed_to_anonymous_callers()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var config = await client.GetAsync($"{_env.BaseUrl}/sso/admin/config");
        var links = await client.PostAsync($"{_env.BaseUrl}/sso/admin/links", null);

        Assert.Equal(HttpStatusCode.Unauthorized, config.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, links.StatusCode);
    }

    [Fact]
    public async Task Groups_seen_at_login_are_offered_to_the_admin_ui()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-ivan", "ivan", ["media", "a-new-group"]));

        var seen = (await _env.PluginConfigAsync())["SeenGroups"]!.AsArray()
            .Select(g => g!.GetValue<string>())
            .ToList();

        Assert.Contains("a-new-group", seen);
    }

    /// <summary>
    /// Jellyfin reports enabled folders as dashed GUIDs while library ids come back from
    /// the API without dashes, so both sides are normalised before comparing.
    /// </summary>
    [Fact]
    public async Task Live_tv_and_downloads_follow_the_groups()
    {
        await _env.PatchConfigAsync(config =>
        {
            var media = JellyfinEnvironment.Mapping(config, "media");
            media["EnableLiveTvAccess"] = true;
            media["EnableContentDownloading"] = true;
        });

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-tv", "tvuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var policy = await _env.UserPolicyAsync("tvuser");
        Assert.True(policy["EnableLiveTvAccess"]!.GetValue<bool>());
        Assert.True(policy["EnableContentDownloading"]!.GetValue<bool>());
        Assert.False(policy["EnableLiveTvManagement"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_parental_rating_from_the_group_reaches_the_user_policy()
    {
        await _env.PatchConfigAsync(config =>
            JellyfinEnvironment.Mapping(config, "kids")["MaxParentalRating"] = 6);

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-kid", "kiduser", ["kids"]));
        Assert.True(login.Succeeded, login.Body);

        Assert.Equal(6, (await _env.UserPolicyAsync("kiduser"))["MaxParentalRating"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_excluded_user_keeps_the_permissions_an_admin_set()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-manual", "manualuser", ["media"]));
        var userId = (await _env.FindUserAsync("manualuser"))!["Id"]!.GetValue<string>();

        await _env.PatchConfigAsync(config =>
            config["SyncExcludedUserIds"]!.AsArray().Add(userId));

        // Logs in again with no groups at all: without the exclusion this would revoke access.
        var second = await _env.StartWebLoginAsync(new FakeUser("sub-manual", "manualuser", []));
        Assert.True(second.Succeeded, second.Body);

        Assert.Equal([Library(_env.MoviesLibraryId)], Folders(await _env.UserPolicyAsync("manualuser")));
    }

    [Fact]
    public async Task Admin_rights_are_revoked_once_the_admin_group_is_gone()
    {
        await _env.StartWebLoginAsync(new FakeUser("sub-demote", "demoteuser", ["jellyfin-admins"]));
        Assert.True((await _env.UserPolicyAsync("demoteuser"))["IsAdministrator"]!.GetValue<bool>());

        // The local setup admin still exists, so the last-administrator guard does not apply.
        var second = await _env.StartWebLoginAsync(new FakeUser("sub-demote", "demoteuser", ["media"]));
        Assert.True(second.Succeeded, second.Body);

        Assert.False((await _env.UserPolicyAsync("demoteuser"))["IsAdministrator"]!.GetValue<bool>());
    }

    // ─── Refusing a login that matches no mapping ─────────────────────────────

    [Fact]
    public async Task A_login_in_no_mapped_group_is_refused_once_the_server_asks_for_one()
    {
        await _env.PatchConfigAsync(config => config["RequireMappedGroup"] = true);

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-stranger", "stranger", ["strangers"]));

        Assert.Equal(HttpStatusCode.Forbidden, login.CallbackStatus);
        Assert.Contains("matched none", login.Body!, StringComparison.Ordinal);
        // Nothing was created: no account to clean up, and no link to a server this
        // person was never let into.
        Assert.Null(await _env.FindUserAsync("stranger"));
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task The_refusal_names_no_group_to_the_browser()
    {
        await _env.PatchConfigAsync(config => config["RequireMappedGroup"] = true);

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-quiet", "quiet", ["secret-department"]));

        // The person in front of the browser cannot act on the detail, and the detail came
        // out of somebody else's token.
        Assert.DoesNotContain("secret-department", login.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_group_is_unaffected_by_the_switch()
    {
        await _env.PatchConfigAsync(config => config["RequireMappedGroup"] = true);

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-member", "memberuser", ["media"]));

        Assert.True(login.Succeeded, login.Body);
        Assert.Equal([Library(_env.MoviesLibraryId)], Folders(await _env.UserPolicyAsync("memberuser")));
    }

    [Fact]
    public async Task An_account_that_loses_its_group_loses_the_login_too()
    {
        await _env.PatchConfigAsync(config => config["RequireMappedGroup"] = true);
        var first = await _env.StartWebLoginAsync(new FakeUser("sub-lapsed", "lapseduser", ["media"]));
        Assert.True(first.Succeeded, first.Body);

        // The group is taken away at the provider. A switch that only guarded the first
        // login would leave the account working for ever.
        var second = await _env.StartWebLoginAsync(new FakeUser("sub-lapsed", "lapseduser", []));

        Assert.Equal(HttpStatusCode.Forbidden, second.CallbackStatus);
        // The account stays as it is — this revokes the login, it does not delete people.
        Assert.NotNull(await _env.FindUserAsync("lapseduser"));
    }

    [Fact]
    public async Task An_excluded_account_can_still_log_in_without_a_group()
    {
        var first = await _env.StartWebLoginAsync(new FakeUser("sub-keeper", "keeperuser", ["media"]));
        Assert.True(first.Succeeded, first.Body);
        var userId = (await _env.FindUserAsync("keeperuser"))!["Id"]!.GetValue<string>();

        await _env.PatchConfigAsync(config =>
        {
            config["RequireMappedGroup"] = true;
            config["SyncExcludedUserIds"]!.AsArray().Add(userId);
        });

        var second = await _env.StartWebLoginAsync(new FakeUser("sub-keeper", "keeperuser", []));

        Assert.True(second.Succeeded, second.Body);
    }

    [Fact]
    public async Task Switched_on_with_no_mappings_at_all_turns_everybody_away()
    {
        await _env.PatchConfigAsync(config =>
        {
            config["RequireMappedGroup"] = true;
            config["GroupMappings"] = new JsonArray();
        });

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-nobody", "nobodyuser", ["media"]));

        Assert.Equal(HttpStatusCode.Forbidden, login.CallbackStatus);
        Assert.Contains(
            "no group mappings are configured",
            await _env.ServerLogsAsync(),
            StringComparison.Ordinal);
    }

    private static List<Guid> Folders(JsonNode policy)
        => policy["EnabledFolders"]?.AsArray().Select(f => Guid.Parse(f!.GetValue<string>())).ToList() ?? [];

    private static Guid Library(string itemId) => Guid.Parse(itemId);
}
