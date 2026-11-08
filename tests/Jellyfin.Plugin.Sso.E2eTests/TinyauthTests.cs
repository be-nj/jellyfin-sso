using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Tinyauth, which is not in the CI tiers.
///
/// The three providers the suite runs on every change are the ones the plugin promises to
/// work with. Tinyauth is a guide, not a promise: it is young, it says itself that it
/// implements only the Core and Discovery parts of OpenID Connect, and the one thing this
/// plugin needs most — groups — is the thing its local accounts do not have. Running it on
/// every push would spend four minutes to assert that a known limitation is still there.
///
/// So these are Manual, and they exist to keep docs/tinyauth.md honest. Each one is a
/// claim that guide makes:
///
///   make e2e E2E_FILTER='--filter "FullyQualifiedName~TinyauthTests"'
///
/// The three tests inherited from the base class come along and are the load-bearing
/// ones: that a login works at all with the plugin as a confidential client, over a
/// reverse proxy, with a secret read from a file and PKCE that Tinyauth really verifies —
/// and that the member gets their library and the admin group grants administrator. They
/// pass here only because the group mappings are keyed on preferred_username, which is
/// what the guide recommends and what <see cref="TinyauthProvider"/> sets up.
/// </summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "Manual")]
public sealed class TinyauthTests : RealIdentityProviderTests
{
    public TinyauthTests(JellyfinEnvironment env)
        : base(env)
    {
    }

    /// <summary>
    /// The limitation the guide leads with, walked rather than quoted: ask Tinyauth for
    /// the groups claim it documents, with the groups scope granted, and nothing arrives.
    /// The login still succeeds — and that is the trap, because it produces an account
    /// that can see nothing, which looks like a plugin fault and is not one.
    /// </summary>
    [Fact]
    public async Task The_documented_groups_claim_is_empty_for_a_local_account()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]![0]!["GroupsClaim"] = "groups");
        var logsBefore = await _env.ServerLogsAsync();

        var page = await SignInAsync(_idp.MemberUser);

        // Signed in, and holding nothing.
        Assert.DoesNotContain("login.html", page.Url);
        var policy = await _env.UserPolicyAsync(_idp.MemberUser.Username);
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Empty(policy["EnabledFolders"]!.AsArray());
        Assert.Empty((await _env.PluginConfigAsync())["SeenGroups"]!.AsArray());

        // The log is the only place that explains it, so the guide points at this line —
        // and it is waited for, not read, because the login answering does not mean the
        // line has left the container.
        await _env.WaitForServerLogAsync("libraries=0, groups=", logsBefore.Length);
        await _env.WaitForServerLogAsync(
            "No groups were read from ID token claim groups for provider real-provider",
            logsBefore.Length);
    }

    /// <summary>
    /// A confidential client whose secret is wrong gets no tokens and nobody gets in. The
    /// guide tells operators to put the secret in a file; this says what happens when the
    /// file holds the wrong thing — an operator who rotated it at the provider only.
    /// </summary>
    [Fact]
    public async Task A_wrong_client_secret_signs_nobody_in()
    {
        await _env.WriteServerFileAsync(
            TinyauthProvider.SecretPathInServer, "ta-this-is-not-the-secret-anymore");

        await AttemptRefusedLoginAsync(_idp.MemberUser);

        Assert.Null(await _env.FindUserAsync(_idp.MemberUser.Username));
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

    /// <summary>
    /// Tinyauth publishes no end_session_endpoint, so there is nothing for the plugin's
    /// "sign out of the provider too" switch to use. It must not turn a sign-out into an
    /// error: the person asked to leave Jellyfin and they leave Jellyfin.
    /// </summary>
    [Fact]
    public async Task Signing_out_still_works_without_an_end_session_endpoint()
    {
        await _env.PatchConfigAsync(config => config["SignOutOfIdentityProvider"] = true);

        // Driven here rather than through the shared helper, because the logout has to
        // happen in the browser that holds the login — the switch only has anything to do
        // when there is a logout session to spend.
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _idp.ServerBaseUrl ?? _env.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();
        await BrowserSupport.GotoAsync(page, "/sso/oidc/start");
        await page.Locator(_idp.LoginFormSelector)
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        await _idp.SignInAsync(page, _idp.MemberUser);
        await BrowserSupport.WaitForSignedInAsync(page, "tinyauth-login");

        var response = await BrowserSupport.GotoAsync(page, "/sso/oidc/logout");

        Assert.NotNull(response);
        Assert.True(response!.Status < 400, $"logout answered {response.Status}");
    }

    internal override async Task<IRealIdentityProvider> StartProviderAsync(JellyfinEnvironment env)
        => await TinyauthProvider.StartAsync(
            env.Certificate, env.WorkDirectory, $"{env.BaseUrl}/sso/oidc/callback/{ProviderId}");
}
