using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The last hop of the login happens in the browser: the callback page authenticates, writes
/// Jellyfin's credential entry to localStorage and hands over to the SPA. That handoff broke
/// twice with "Must supply a serverAddress", which no server-side test can catch.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class BrowserLoginTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public BrowserLoginTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public async Task InitializeAsync()
    {
        await _env.ResetAsync();
        _playwright = await BrowserSupport.InstallAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task A_browser_login_ends_up_signed_in_on_the_home_screen()
    {
        _env.Idp.NextUser = new FakeUser("sub-browser", "browseruser", ["media"]);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            // The fake provider's certificate is self-signed.
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

        await page.GotoAsync("/sso/oidc/start");
        await WaitForWebAppAsync(page, "home-screen", consoleErrors);

        // The SPA only gets this far with usable credentials in localStorage.
        var stored = await page.EvaluateAsync<string?>("() => localStorage.getItem('jellyfin_credentials')");
        Assert.NotNull(stored);

        var server = JsonNode.Parse(stored!)!["Servers"]!.AsArray().Single()!;
        Assert.False(string.IsNullOrWhiteSpace(server["AccessToken"]!.GetValue<string>()));
        Assert.Equal("browseruser", server["UserName"]!.GetValue<string>());
        Assert.Equal(_env.BaseUrl, server["ManualAddress"]!.GetValue<string>());

        Assert.DoesNotContain(
            consoleErrors,
            error => error.Contains("serverAddress", StringComparison.OrdinalIgnoreCase));

        // The web client adopted the session rather than bouncing back to the login page.
        await WaitForSignedInAsync(page, "home-screen", consoleErrors);
        Assert.DoesNotContain("login.html", page.Url);
    }

    [Fact]
    public async Task The_session_survives_a_reload()
    {
        _env.Idp.NextUser = new FakeUser("sub-reload", "reloaduser", ["media"]);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            // The fake provider's certificate is self-signed.
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync("/sso/oidc/start");
        await WaitForWebAppAsync(page, "reload");

        await page.ReloadAsync();
        await WaitForSignedInAsync(page, "reload");

        Assert.DoesNotContain("login.html", page.Url);
    }

    /// <summary>
    /// Waits for the handoff into the web client. On failure it writes a screenshot and the
    /// page HTML to tests/runs/playwright and reports what the page was showing, because
    /// "timed out waiting for a URL" says nothing about why the login stalled.
    /// </summary>
    private static async Task WaitForWebAppAsync(IPage page, string name, List<string>? consoleErrors = null)
    {
        try
        {
            await page.WaitForURLAsync("**/web/**", new PageWaitForURLOptions { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            var directory = await BrowserSupport.CaptureAsync(page, name);

            Assert.Fail(
                "The login never reached the web client.\n"
                + $"url:     {page.Url}\n"
                + $"text:    {await page.InnerTextAsync("body")}\n"
                + $"console: {string.Join(" | ", consoleErrors ?? [])}\n"
                + $"saved:   {directory}");
        }
    }

    /// <summary>
    /// Waits until the web client itself reports a usable session: its ApiClient has an
    /// access token and a current user. That is what the localStorage handoff is for.
    /// </summary>
    private static async Task WaitForSignedInAsync(IPage page, string name, List<string>? consoleErrors = null)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => !!(window.ApiClient && window.ApiClient.accessToken() && window.ApiClient.getCurrentUserId())",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            await BrowserSupport.CaptureAsync(page, name);
            Assert.Fail(
                "The web client never adopted the SSO session.\n"
                + $"url:     {page.Url}\n"
                + $"text:    {await page.InnerTextAsync("body")}\n"
                + $"console: {string.Join(" | ", consoleErrors ?? [])}");
        }
    }
}
