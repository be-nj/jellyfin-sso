using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The optional in-form button. It is placed by a script the plugin adds to Jellyfin's
/// own page as that page is served, which is the one part of this plugin that rewrites
/// something belonging to the server — so it is tested against a real browser loading a
/// real Jellyfin, not against the middleware in isolation.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class LoginFormButtonTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public LoginFormButtonTests(JellyfinEnvironment env)
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
    public async Task The_page_is_untouched_while_the_setting_is_off()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var html = await client.GetStringAsync($"{_env.BaseUrl}/web/index.html");

        Assert.DoesNotContain("/sso/login-button.js", html);
    }

    [Fact]
    public async Task Turning_it_on_adds_the_script_tag_and_nothing_else()
    {
        await EnableAsync();

        using var client = JellyfinEnvironment.TrustingClient();
        var html = await client.GetStringAsync($"{_env.BaseUrl}/web/index.html");

        Assert.Contains("<script defer src=\"/sso/login-button.js\"></script>", html);
        Assert.Contains("</body>", html);
        Assert.EndsWith("</html>", html.TrimEnd());
    }

    [Fact]
    public async Task The_script_is_empty_while_the_setting_is_off()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var script = await client.GetStringAsync($"{_env.BaseUrl}/sso/login-button.js");

        Assert.DoesNotContain("/sso/oidc/start/", script);
    }

    [Fact]
    public async Task The_button_appears_in_the_login_form()
    {
        await EnableAsync();

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/web/index.html#/login.html");

        var button = page.Locator("a.sso-form-button");
        await button.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        Assert.Equal(
            $"/sso/oidc/start/{JellyfinEnvironment.ProviderId}",
            new Uri(await button.First.GetAttributeAsync("href") ?? string.Empty, UriKind.RelativeOrAbsolute)
                .ToString());

        // Inside the form, not after it in the branding.
        Assert.True(await page.Locator("form a.sso-form-button").CountAsync() > 0);
    }

    [Fact]
    public async Task Clicking_the_button_in_the_form_signs_the_user_in()
    {
        await EnableAsync();
        _env.Idp.NextUser = new FakeUser("sub-form-click", "formclickuser", ["media"]);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/web/index.html#/login.html");

        var button = page.Locator("a.sso-form-button").First;
        await button.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await button.ClickAsync();

        // jellyfin-web routes anchor clicks through its own router wherever it owns the
        // element. This one is added to the form by the plugin's own script, so it has to
        // navigate like any link — and the login has to end in a session.
        try
        {
            await page.WaitForFunctionAsync(
                "() => !!(window.ApiClient && window.ApiClient.accessToken() && window.ApiClient.getCurrentUserId())",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            await BrowserSupport.CaptureAsync(page, "form-button-click");
            Assert.Fail($"The button in the form did not sign anybody in.\nurl: {page.Url}");
        }

        Assert.DoesNotContain("login.html", page.Url);
    }

    [Fact]
    public async Task The_button_stays_off_the_pages_of_somebody_already_signed_in()
    {
        await EnableAsync();
        _env.Idp.NextUser = new FakeUser("sub-profile", "profileuser", ["media"]);

        await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/web/index.html#/login.html");

        var button = page.Locator("a.sso-form-button").First;
        await button.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await button.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => !!(window.ApiClient && window.ApiClient.accessToken())",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        // The profile page carries password fields and a form of its own, which is
        // exactly what the script used to take for a login form.
        var userId = await page.EvaluateAsync<string>("() => window.ApiClient.getCurrentUserId()");
        await page.GotoAsync($"/web/index.html#/userprofile?userId={userId}");
        await page.Locator("input[type=password]").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        Assert.Equal(0, await page.Locator("a.sso-form-button:visible").CountAsync());
    }

    private Task EnableAsync()
        => _env.PatchConfigAsync(config =>
        {
            config["InjectLoginButtonIntoForm"] = true;
            config["Providers"]!.AsArray()[0]!["Name"] = "Fake IdP";
        });
}
