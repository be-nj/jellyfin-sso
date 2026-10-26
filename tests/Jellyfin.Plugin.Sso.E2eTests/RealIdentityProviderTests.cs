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

    private protected readonly JellyfinEnvironment _env;
    private protected IRealIdentityProvider _idp = null!;

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

    /// <summary>
    /// Walks the login the way a person would. Chromium in CI occasionally loses every
    /// connection under the running page — "net::ERR_NETWORK_CHANGED", the runner's
    /// network reconfiguring under it — which has nothing to do with the plugin, so a
    /// login that fails that way is walked once more in a fresh context.
    /// </summary>
    private protected async Task<IPage> SignInAsync(RealIdentityUser user)
    {
        try
        {
            return await SignInOnceAsync(user);
        }
        catch (BrowserNetworkFlake first)
        {
            try
            {
                return await SignInOnceAsync(user);
            }
            catch (BrowserNetworkFlake second)
            {
                Assert.Fail(
                    $"The browser lost the network twice in a row.\nfirst: {first.Message}"
                    + $"\nsecond: {second.Message}");
                throw;
            }
        }
    }

    /// <summary>One attempt, from an empty browser context to a signed-in web client.</summary>
    private async Task<IPage> SignInOnceAsync(RealIdentityUser user)
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
            ThrowIfNetworkFlake("opening /sso/oidc/start", [ex.Message]);

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
            ThrowIfNetworkFlake($"{_idp.Name}'s login form", consoleErrors);

            await BrowserSupport.CaptureAsync(page, $"{_idp.Name.ToLowerInvariant()}-form");
            var logs = (await _env.ServerLogsAsync()).Split('\n')
                .Where(l => l.Contains("Sso", StringComparison.Ordinal)
                    || l.Contains("Exception", StringComparison.Ordinal)
                    || l.Contains("[ERR]", StringComparison.Ordinal))
                .TakeLast(5);
            Assert.Fail(
                $"{_idp.Name}'s login form never appeared.\n"
                + $"server log: {string.Join(" / ", logs)}\n"
                + $"provider log: {await _idp.DiagnosticsAsync()}\n"
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
            ThrowIfNetworkFlake("the web client's session", consoleErrors);

            await BrowserSupport.CaptureAsync(page, $"{_idp.Name.ToLowerInvariant()}-login");
            var serverLog = (await _env.ServerLogsAsync()).Split('\n')
                .Where(l => l.Contains("Sso", StringComparison.Ordinal)
                    || l.Contains("Exception", StringComparison.Ordinal))
                .TakeLast(5);
            Assert.Fail(
                $"The login through {_idp.Name} never reached a signed-in web client.\n"
                + $"server log: {string.Join(" / ", serverLog)}\n"
                + $"url:     {page.Url}\n"
                + $"text:    {await page.InnerTextAsync("body")}\n"
                + $"console: {string.Join(" | ", consoleErrors)}");
        }

        return page;
    }

    /// <summary>
    /// Chromium reports a network that moved under it as a load failure on every pending
    /// resource. Recognising those by name keeps the retry from papering over a plugin
    /// bug: anything else fails the test with its diagnostics, as before.
    /// </summary>
    private static void ThrowIfNetworkFlake(string what, IEnumerable<string> messages)
    {
        string[] transient =
        [
            "ERR_NETWORK_CHANGED",
            "ERR_NETWORK_IO_SUSPENDED",
            "ERR_INTERNET_DISCONNECTED",
            "ERR_NAME_NOT_RESOLVED",
            "ERR_CONNECTION_RESET",
        ];

        var hit = messages.FirstOrDefault(
            message => transient.Any(name => message.Contains(name, StringComparison.Ordinal)));

        if (hit is not null)
        {
            throw new BrowserNetworkFlake($"{what} — {hit.Trim()}");
        }
    }
}

/// <summary>The browser lost its network mid-login; the run says nothing about the plugin.</summary>
internal sealed class BrowserNetworkFlake : Exception
{
    public BrowserNetworkFlake(string message)
        : base(message)
    {
    }
}

/// <summary>Authentik — the provider this plugin was written for.</summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "RealIdp")]
public sealed class AuthentikTests : RealIdentityProviderTests
{
    public AuthentikTests(JellyfinEnvironment env)
        : base(env)
    {
    }

    internal override async Task<IRealIdentityProvider> StartProviderAsync(JellyfinEnvironment env)
        => await AuthentikProvider.StartAsync(
            env.Certificate, env.WorkDirectory, $"{env.BaseUrl}/sso/oidc/callback/{ProviderId}");
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

    [Fact]
    public async Task Without_userinfo_groups_login_succeeds_with_no_libraries_and_an_actionable_warning()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]![0]!["GroupsFromUserInfo"] = false);
        var logsBefore = await _env.ServerLogsAsync();

        var page = await SignInAsync(_idp.MemberUser);

        Assert.DoesNotContain("login.html", page.Url);
        var user = await _env.FindUserAsync(_idp.MemberUser.Username);
        Assert.NotNull(user);
        var policy = await _env.UserPolicyAsync(_idp.MemberUser.Username);
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Empty(policy["EnabledFolders"]!.AsArray());
        Assert.Empty((await _env.PluginConfigAsync())["SeenGroups"]!.AsArray());

        var logs = (await _env.ServerLogsAsync())[logsBefore.Length..];
        Assert.Contains("libraries=0, groups=", logs);
        Assert.Contains(
            "No groups were read from ID token claim groups for provider real-provider", logs);
        Assert.Contains("Fetch groups from userinfo endpoint", logs);
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
