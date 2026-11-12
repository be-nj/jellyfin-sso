using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Sso.E2eTests;

[Collection(JellyfinCollection.Name)]
public sealed class ReviewSecurityTests(JellyfinEnvironment env) : IAsyncLifetime
{
    public Task InitializeAsync() => env.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Previously_issued_secret_must_not_authenticate_after_revocation(bool unlink)
    {
        var login = await env.StartWebLoginAsync(new FakeUser("review-sub", "review-user", ["media"]));
        Assert.True(login.Succeeded, login.Body);
        if (unlink)
        {
            var user = await env.FindUserAsync("review-user");
            await env.RemoveLinkAsync(Guid.Parse(user!["Id"]!.GetValue<string>()));
        }
        else
        {
            using var client = JellyfinEnvironment.TrustingClient();
            using var logout = await client.PostAsync(
                $"{env.BaseUrl}/sso/oidc/backchannel-logout/{JellyfinEnvironment.ProviderId}",
                new FormUrlEncodedContent(new Dictionary<string,string> {
                    ["logout_token"] = env.Idp.MintLogoutToken("review-sub", JellyfinEnvironment.ClientId)
                }));
            logout.EnsureSuccessStatusCode();
        }
        using var authenticated = await env.AuthenticateAsync("review-user", login.Secret!);
        Assert.Equal(HttpStatusCode.Unauthorized, authenticated.StatusCode);
    }

    [Fact]
    public async Task Avatar_download_must_not_undo_administrator_disabling_account()
    {
        await env.PatchConfigAsync(c => c["SyncProfilePictures"] = true);
        env.Idp.NextPictureUrl = env.Idp.AvatarUrl;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Idp.BeforeAvatar = async () => { entered.TrySetResult(); await resume.Task; };
        var login = env.StartWebLoginAsync(new FakeUser("review-avatar-sub", "review-avatar", ["media"]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var user = await env.FindUserAsync("review-avatar");
            var policy = user!["Policy"]!.DeepClone();
            policy["IsDisabled"] = true;
            using var admin = JellyfinEnvironment.TrustingClient();
            admin.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(env.AdminToken);
            using var saved = await admin.PostAsJsonAsync($"{env.BaseUrl}/Users/{user["Id"]!.GetValue<string>()}/Policy", policy);
            saved.EnsureSuccessStatusCode();
            Assert.True((await env.UserPolicyAsync("review-avatar"))["IsDisabled"]!.GetValue<bool>());
        }
        finally { resume.TrySetResult(); }
        var completed = await login;
        Assert.True(completed.Succeeded, completed.Body);
        Assert.True((await env.UserPolicyAsync("review-avatar"))["IsDisabled"]!.GetValue<bool>(),
            "The avatar update re-enabled the account that the administrator had just disabled");
    }

    [Fact]
    public async Task Logout_during_avatar_download_prevents_issuing_a_new_secret()
    {
        await env.PatchConfigAsync(c => c["SyncProfilePictures"] = true);
        env.Idp.NextPictureUrl = env.Idp.AvatarUrl;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        env.Idp.BeforeAvatar = async () => { entered.TrySetResult(); await resume.Task; };
        var login = env.StartWebLoginAsync(new FakeUser("review-pending-sub", "review-pending", ["media"]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await LogoutAsync("review-pending-sub");
        }
        finally { resume.TrySetResult(); }
        var completed = await login;
        Assert.Equal(HttpStatusCode.BadRequest, completed.CallbackStatus);
        Assert.Contains("This login was revoked", completed.Body, StringComparison.Ordinal);
        Assert.Null(completed.Secret);
    }

    [Fact]
    public async Task Logout_invalidates_a_quick_connect_approval_before_it_is_clicked()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Add("Authorization",
            "MediaBrowser Client=\"review-tv\", Device=\"TV\", DeviceId=\"review-tv\", Version=\"1.0\"");
        using var initiated = await client.PostAsync($"{env.BaseUrl}/QuickConnect/Initiate", null);
        initiated.EnsureSuccessStatusCode();
        var device = (await initiated.Content.ReadFromJsonAsync<JsonNode>())!;
        var login = await env.StartWebLoginAsync(new FakeUser("review-qc-sub", "review-qc", ["media"]),
            quickConnectCode: device["Code"]!.GetValue<string>());
        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;
        Assert.NotEmpty(token);
        await LogoutAsync("review-qc-sub");
        using var approval = await client.PostAsync($"{env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        Assert.Equal(HttpStatusCode.BadRequest, approval.StatusCode);
        Assert.Contains("expired", await approval.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var status = await client.GetFromJsonAsync<JsonNode>(
            $"{env.BaseUrl}/QuickConnect/Connect?secret={device["Secret"]!.GetValue<string>()}");
        Assert.False(status!["Authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Logout_after_approval_stops_the_device_exchanging_its_quick_connect_secret()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Add("Authorization",
            "MediaBrowser Client=\"review-tv\", Device=\"TV\", DeviceId=\"review-tv-late\", Version=\"1.0\"");
        using var initiated = await client.PostAsync($"{env.BaseUrl}/QuickConnect/Initiate", null);
        initiated.EnsureSuccessStatusCode();
        var device = (await initiated.Content.ReadFromJsonAsync<JsonNode>())!;
        var secret = device["Secret"]!.GetValue<string>();
        var login = await env.StartWebLoginAsync(new FakeUser("review-qc-late-sub", "review-qc-late", ["media"]),
            quickConnectCode: device["Code"]!.GetValue<string>());
        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;
        using var approval = await client.PostAsync($"{env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        approval.EnsureSuccessStatusCode();

        // The device has not collected its token yet: it holds an authorized secret only.
        await LogoutAsync("review-qc-late-sub");

        // Jellyfin mints the device's token at approval and caches the result, so the
        // exchange may still answer 200. What matters is that the token it hands out is dead.
        using var exchanged = await client.PostAsJsonAsync(
            $"{env.BaseUrl}/Users/AuthenticateWithQuickConnect", new { Secret = secret });
        if (!exchanged.IsSuccessStatusCode)
            return;
        var accessToken = (await exchanged.Content.ReadFromJsonAsync<JsonNode>())!["AccessToken"]!
            .GetValue<string>();
        using var me = new HttpRequestMessage(HttpMethod.Get, $"{env.BaseUrl}/Users/Me");
        me.Headers.Add("Authorization",
            $"MediaBrowser Client=\"review-tv\", Device=\"TV\", DeviceId=\"review-tv-late\", Version=\"1.0\", Token=\"{accessToken}\"");
        using var probe = JellyfinEnvironment.TrustingClient();
        using var used = await probe.SendAsync(me);
        Assert.Equal(HttpStatusCode.Unauthorized, used.StatusCode);
    }

    private async Task LogoutAsync(string sub)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        using var response = await client.PostAsync(
            $"{env.BaseUrl}/sso/oidc/backchannel-logout/{JellyfinEnvironment.ProviderId}",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["logout_token"] = env.Idp.MintLogoutToken(sub, JellyfinEnvironment.ClientId)
            }));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Started_link_flow_must_honor_disabling_self_service_before_callback()
    {
        var (_, token) = await env.CreateLocalUserWithTokenAsync("review-link");
        using var browser = JellyfinEnvironment.TrustingClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{env.BaseUrl}/sso/link/start?providerId={JellyfinEnvironment.ProviderId}");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);
        using var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var authorizeUrl = (await response.Content.ReadFromJsonAsync<JsonNode>())!["AuthorizeUrl"]!.GetValue<string>();
        await env.PatchConfigAsync(c => c["AllowSelfServiceLinking"] = false);
        var completed = await env.StartWebLoginAsync(
            new FakeUser("review-link-sub", "review-link", []), browser, startUrl: authorizeUrl);
        Assert.Equal(HttpStatusCode.Forbidden, completed.CallbackStatus);
        Assert.Contains("Self-service linking is disabled", completed.Body, StringComparison.Ordinal);
        Assert.Empty((await env.PluginConfigAsync())["Links"]!.AsArray());
    }

    [Fact]
    public async Task A_disabled_account_cannot_let_a_quick_connect_device_in()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Add("Authorization",
            "MediaBrowser Client=\"review-tv\", Device=\"TV\", DeviceId=\"review-tv-disabled\", Version=\"1.0\"");
        using var initiated = await client.PostAsync($"{env.BaseUrl}/QuickConnect/Initiate", null);
        initiated.EnsureSuccessStatusCode();
        var device = (await initiated.Content.ReadFromJsonAsync<JsonNode>())!;
        var login = await env.StartWebLoginAsync(new FakeUser("review-qc-off-sub", "review-qc-off", ["media"]),
            quickConnectCode: device["Code"]!.GetValue<string>());
        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;
        Assert.NotEmpty(token);

        // The administrator disables the account between the login and the click.
        var user = await env.FindUserAsync("review-qc-off");
        var policy = user!["Policy"]!.DeepClone();
        policy["IsDisabled"] = true;
        using var admin = JellyfinEnvironment.TrustingClient();
        admin.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(env.AdminToken);
        using var saved = await admin.PostAsJsonAsync($"{env.BaseUrl}/Users/{user["Id"]!.GetValue<string>()}/Policy", policy);
        saved.EnsureSuccessStatusCode();

        using var approval = await client.PostAsync($"{env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        Assert.Equal(HttpStatusCode.Forbidden, approval.StatusCode);
        var status = await client.GetFromJsonAsync<JsonNode>(
            $"{env.BaseUrl}/QuickConnect/Connect?secret={device["Secret"]!.GetValue<string>()}");
        Assert.False(status!["Authenticated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Jellyfins_own_configuration_endpoint_cannot_remove_a_link_without_revoking_it()
    {
        var login = await env.StartWebLoginAsync(new FakeUser("review-cfg-sub", "review-cfg", ["media"]));
        Assert.True(login.Succeeded, login.Body);
        using var admin = JellyfinEnvironment.TrustingClient();
        admin.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(env.AdminToken);
        var config = JsonNode.Parse(await admin.GetStringAsync(
            $"{env.BaseUrl}/Plugins/{JellyfinEnvironment.PluginId}/Configuration"))!;
        Assert.NotEmpty(config["Links"]!.AsArray());
        config["Links"] = new JsonArray();

        using var saved = await admin.PostAsJsonAsync(
            $"{env.BaseUrl}/Plugins/{JellyfinEnvironment.PluginId}/Configuration", config);
        saved.EnsureSuccessStatusCode();

        Assert.Contains((await env.PluginConfigAsync())["Links"]!.AsArray(),
            l => l!["Sub"]!.GetValue<string>() == "review-cfg-sub");
    }

    [Fact]
    public async Task Every_guarded_jellyfin_action_exists_on_this_server()
    {
        var logs = await env.ServerLogsAsync();
        Assert.Contains("Every Jellyfin login action the SSO revocation guard watches is present", logs,
            StringComparison.Ordinal);
    }
}
