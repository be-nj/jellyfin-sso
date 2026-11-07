using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A browser against a server published under a sub-path, through a real reverse proxy.
///
/// This is the crossing the suite never made. A prefixed login has been walked by an
/// HttpClient since #5, and the configuration page has been driven by a browser since the
/// stylesheet defect — but never both, and a browser is the only thing that runs the
/// JavaScript where two defects were hiding: the configuration page fetched its API from
/// the origin rather than from the server's address, and the login handoff saved a server
/// address with the prefix missing. A browser cannot set X-Forwarded-Prefix, so nothing
/// short of a proxy could have found either.
///
/// Both are now also held by unit tests on the generated text. These are the ones that run
/// it.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class PrefixedDeploymentTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;
    private PrefixedProxy _proxy = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public PrefixedDeploymentTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public async Task InitializeAsync()
    {
        await _env.ResetAsync();

        _proxy = await PrefixedProxy.StartAsync(
            _env.Certificate, _env.WorkDirectory, new Uri(_env.BaseUrl).Port);

        // The provider refuses a redirect URI it has not been told about, as a real one
        // does — and through the proxy the plugin computes a different one than the
        // harness's own prefixed walk does, because the address comes from the proxy's
        // headers rather than from the container's published port.
        _env.Idp.RegisteredRedirectUris.Add(
            $"{_proxy.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}");
        _env.Idp.RegisteredRedirectUris.Add($"{_proxy.BaseUrl}/web/index.html");

        _playwright = await BrowserSupport.InstallAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            // The proxy's certificate is issued for this name, and nothing on the host
            // resolves it. The real-provider tests have needed the same rule for longer.
            Args = [$"--host-resolver-rules=MAP {TestCertificate.HostName} 127.0.0.1"],
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        await _proxy.DisposeAsync();
    }

    /// <summary>
    /// The page's own requests. It fetched /sso/admin/config, which is the origin and not
    /// this server, so under a prefix the configuration page did not load at all — and the
    /// addresses it offered an operator to register were missing the prefix too, which is
    /// the one value a provider refuses a login over.
    /// </summary>
    [Fact]
    public async Task The_configuration_page_loads_under_a_sub_path()
    {
        var requests = new List<string>();
        var (dashboard, page) = await DashboardAsync(requests);
        await using var owned = dashboard;

        await BrowserSupport.GotoAsync(
            page,
            $"{_proxy.BaseUrl}/web/index.html#/configurationpage?name=SSO%20Authentication");

        // A provider card only renders once the configuration fetch has come back.
        await page.Locator(".sso-provider").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = BrowserSupport.WebClientTimeout });

        // Every request the page made for its own API carried the prefix.
        var apiCalls = requests.Where(u => u.Contains("/sso/admin/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(apiCalls);
        Assert.All(apiCalls, url => Assert.Contains(
            $"{PrefixedProxy.Prefix}/sso/admin/", url, StringComparison.Ordinal));

        // And the addresses it tells an operator to register carry it exactly once. The
        // callback is the one that matters: a provider refuses a redirect URI it has not
        // been given, so an address with the prefix missing is a login nobody can finish.
        var copyable = await page.Locator("button.sso-copy")
            .EvaluateAllAsync<string[]>("buttons => buttons.map(b => b.dataset.copy)");

        Assert.Contains(
            $"{_proxy.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}", copyable);
        Assert.Contains($"{_proxy.BaseUrl}/sso/oidc/start/", string.Join("\n", copyable));
        Assert.Contains(
            $"{_proxy.BaseUrl}/sso/oidc/backchannel-logout/{JellyfinEnvironment.ProviderId}",
            copyable);

        // Exactly once: the prefix is in the server address already, so anything that
        // concatenated the two would read /jellyfin/jellyfin here.
        Assert.All(copyable, value => Assert.DoesNotContain(
            $"{PrefixedProxy.Prefix}{PrefixedProxy.Prefix}", value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The handoff. Every request the page makes is built from the base path and the one
    /// value that is not a request was not: jellyfin-web resolves its API address from the
    /// entry this writes, so a first login under a prefix ended with a client talking to
    /// the origin, where this server is not.
    /// </summary>
    [Fact]
    public async Task A_browser_login_under_a_sub_path_saves_the_prefixed_address()
    {
        _env.Idp.NextUser = new FakeUser("sub-prefixed-browser", "prefixedbrowser", ["media"]);

        await using var traced = await TracedContext.StartAsync(_browser, new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        });
        var page = await traced.NewPageAsync();
        var console = new List<string>();
        page.Console += (_, m) => console.Add($"{m.Type}: {m.Text}");

        // Through the retrying helper: this class starts a container of its own moments
        // before navigating, and Docker rearranging its networks abandons a navigation in
        // flight with ERR_NETWORK_CHANGED. One of three matrix jobs failed exactly there.
        var landed = await BrowserSupport.GotoAsync(page, $"{_proxy.BaseUrl}/sso/oidc/start");

        try
        {
            await page.WaitForFunctionAsync(
                "() => !!localStorage.getItem('jellyfin_credentials')",
                null,
                new PageWaitForFunctionOptions { Timeout = BrowserSupport.WebClientTimeout });
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"no credentials. first response {landed?.Status}, now at {page.Url}\n"
                + $"console:\n{string.Join("\n", console)}\n"
                + $"body:\n{(await page.ContentAsync())[..Math.Min(1500, (await page.ContentAsync()).Length)]}\n"
                + $"nginx:\n{await _proxy.LogsAsync()}");
        }

        // Waited out before anything is read: the handoff page writes the credentials and
        // then sends the browser to the web client, and an evaluate that lands in the
        // middle of that navigation dies with "execution context was destroyed". The
        // condition below is also the stronger claim — the client adopted the session —
        // and WaitForFunction survives the navigation where a read does not.
        await BrowserSupport.WaitForSignedInAsync(page, "prefixed-browser-login", console);

        var stored = await page.EvaluateAsync<string>(
            "() => localStorage.getItem('jellyfin_credentials')");
        var server = JsonNode.Parse(stored)!["Servers"]!.AsArray().Single()!;

        Assert.Equal("prefixedbrowser", server["UserName"]!.GetValue<string>());
        Assert.Equal(_proxy.BaseUrl, server["ManualAddress"]!.GetValue<string>());
        Assert.EndsWith(
            PrefixedProxy.Prefix,
            server["ManualAddress"]!.GetValue<string>(),
            StringComparison.Ordinal);

        Assert.DoesNotContain("login.html", page.Url);
    }

    /// <summary>
    /// The dashboard, signed in as the administrator, with its requests recorded.
    ///
    /// The context is handed back rather than owned here: a helper that disposed it would
    /// hand the caller a page whose browser context is already gone, which is a confusing
    /// way to fail and was one commit's worth of exactly that.
    /// </summary>
    private async Task<(TracedContext Traced, IPage Page)> DashboardAsync(
        List<string> requests, [CallerMemberName] string name = "dashboard")
    {
        var traced = await TracedContext.StartAsync(
            _browser,
            new BrowserNewContextOptions { IgnoreHTTPSErrors = true },
            name);

        var credentials = new JsonObject
        {
            ["Servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = _env.ServerId,
                    ["AccessToken"] = _env.AdminToken,
                    ["UserId"] = _env.AdminUserId,
                    ["UserName"] = _env.AdminUserName,
                    ["ManualAddress"] = _proxy.BaseUrl,
                    ["LastConnectionMode"] = 2,
                    ["DateLastAccessed"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            },
        };

        await traced.Context.AddInitScriptAsync(
            "window.localStorage.setItem('jellyfin_credentials', "
            + JsonSerializer.Serialize(credentials.ToJsonString()) + ");"
            + "window.localStorage.setItem('_deviceId2', 'e2e-prefixed');");

        var page = await traced.NewPageAsync();
        page.Request += (_, request) => requests.Add(request.Url);
        return (traced, page);
    }
}
