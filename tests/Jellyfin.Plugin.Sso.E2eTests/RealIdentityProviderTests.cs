using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Drives a login through a real identity provider, in a browser, including its own login
/// form. The fake provider proves the plugin follows the specification; these prove it
/// works against an implementation nobody on this side controls — above all that the
/// groups really arrive in the claims.
/// </summary>
public abstract class RealIdentityProviderTests : IAsyncLifetime
{
    internal const string ProviderId = "real-provider";

    // The provider container and the browser are expensive and stateless between tests,
    // so they are started once per provider and live until the test run ends; Ryuk cleans
    // the containers up afterwards.
    private static readonly Dictionary<Type, Task<IRealIdentityProvider>> Providers = [];
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    private readonly JellyfinEnvironment _env;
    private IRealIdentityProvider _idp = null!;

    protected RealIdentityProviderTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    /// <summary>Starts the provider under test.</summary>
    internal abstract Task<IRealIdentityProvider> StartProviderAsync(JellyfinEnvironment env);

    public async Task InitializeAsync()
    {
        await _env.ResetAsync();

        await Gate.WaitAsync();
        try
        {
            if (!Providers.TryGetValue(GetType(), out var starting))
            {
                starting = StartProviderAsync(_env);
                Providers[GetType()] = starting;
            }

            _idp = await starting;

            _playwright ??= await BrowserSupport.InstallAsync();
            _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                // The provider is published on the host, but its issuer and endpoints name
                // host.docker.internal so that the server container can reach it too.
                Args = [$"--host-resolver-rules=MAP {TestCertificate.HostName} 127.0.0.1"],
            });
        }
        finally
        {
            Gate.Release();
        }

        // Replace the fake provider so the login goes to the real one without a picker.
        await _env.PatchConfigAsync(config => config["Providers"] = new JsonArray
        {
            new JsonObject
            {
                ["Id"] = ProviderId,
                ["Name"] = _idp.Name,
                ["Enabled"] = true,
                ["Authority"] = _idp.Authority,
                ["ClientId"] = _idp.ClientId,
                ["Scopes"] = new JsonArray(_idp.Scopes.Select(s => (JsonNode)s!).ToArray()),
                ["GroupsClaim"] = _idp.GroupsClaim,
                ["GroupsFromUserInfo"] = _idp.GroupsFromUserInfo,
            },
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_login_provisions_the_user_with_the_libraries_of_their_group()
    {
        var page = await SignInAsync(_idp.MemberUser);

        var policy = await _env.UserPolicyAsync(_idp.MemberUser.Username);
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        var folders = policy["EnabledFolders"]!.AsArray().Select(f => Guid.Parse(f!.GetValue<string>()));
        Assert.Equal([Guid.Parse(_env.MoviesLibraryId)], folders);

        // The web client adopted the session, so the whole chain worked end to end.
        Assert.DoesNotContain("login.html", page.Url);
    }

    [Fact]
    public async Task The_admin_group_of_a_real_provider_grants_administrator()
    {
        await SignInAsync(_idp.AdminUser);

        var policy = await _env.UserPolicyAsync(_idp.AdminUser.Username);
        Assert.True(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.True(policy["EnableAllFolders"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_identity_is_linked_by_subject_not_by_name()
    {
        await SignInAsync(_idp.MemberUser);

        var link = Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
        Assert.Equal(ProviderId, link!["ProviderId"]!.GetValue<string>());

        // Real providers mint their own subject; it must not be the username.
        var sub = link["Sub"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(sub));
        Assert.NotEqual(_idp.MemberUser.Username, sub);
    }

    /// <summary>Walks the login the way a person would, including the provider's form.</summary>
    private async Task<IPage> SignInAsync(RealIdentityUser user)
    {
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _idp.ServerBaseUrl ?? _env.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();

        var consoleErrors = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                consoleErrors.Add(message.Text);
            }
        };

        try
        {
            await page.GotoAsync("/sso/oidc/start");
        }
        catch (PlaywrightException ex)
        {
            using var probe = JellyfinEnvironment.TrustingClient();
            string fromTestProcess;
            try
            {
                fromTestProcess = ((int)(await probe.GetAsync($"{_env.BaseUrl}/System/Info/Public")).StatusCode)
                    .ToString();
            }
            catch (Exception probeError)
            {
                fromTestProcess = probeError.GetType().Name + ": " + probeError.Message;
            }

            var logs = await _env.ServerLogsAsync();
            Assert.Fail($"Jellyfin was unreachable from the browser: {ex.Message}\n"
                + $"from the test process: {fromTestProcess}\n"
                + $"log lines: {logs.Split('\n').Length}");
        }

        try
        {
            await _idp.SignInAsync(page, user);
        }
        catch (TimeoutException)
        {
            await BrowserSupport.CaptureAsync(page, $"{_idp.Name.ToLowerInvariant()}-form");
            Assert.Fail(
                $"{_idp.Name}'s login form never appeared.\n"
                + $"url:     {page.Url}\n"
                + $"text:    {(await page.InnerTextAsync("body")).Split('\n').FirstOrDefault()}\n"
                + $"console: {string.Join(" | ", consoleErrors)}");
        }

        try
        {
            await page.WaitForFunctionAsync(
                "() => !!(window.ApiClient && window.ApiClient.accessToken() && window.ApiClient.getCurrentUserId())",
                null,
                new PageWaitForFunctionOptions { Timeout = 60_000 });
        }
        catch (TimeoutException)
        {
            await BrowserSupport.CaptureAsync(page, $"{_idp.Name.ToLowerInvariant()}-login");
            Assert.Fail(
                $"The login through {_idp.Name} never reached a signed-in web client.\n"
                + $"url:     {page.Url}\n"
                + $"text:    {await page.InnerTextAsync("body")}\n"
                + $"console: {string.Join(" | ", consoleErrors)}");
        }

        return page;
    }
}

/// <summary>Authelia, which keeps the groups out of the ID token.</summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "RealIdp")]
public sealed class AutheliaTests : RealIdentityProviderTests
{
    public AutheliaTests(JellyfinEnvironment env)
        : base(env)
    {
    }

    internal override async Task<IRealIdentityProvider> StartProviderAsync(JellyfinEnvironment env)
        => await AutheliaProvider.StartAsync(
            env.Certificate, env.WorkDirectory, env.LocalhostBaseUrl, ProviderId);
}

/// <summary>Keycloak, the provider most of the plugin's issues mention after Authentik.</summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "RealIdp")]
public sealed class KeycloakTests : RealIdentityProviderTests
{
    public KeycloakTests(JellyfinEnvironment env)
        : base(env)
    {
    }

    internal override async Task<IRealIdentityProvider> StartProviderAsync(JellyfinEnvironment env)
        => await KeycloakProvider.StartAsync(env.Certificate, env.WorkDirectory);
}
