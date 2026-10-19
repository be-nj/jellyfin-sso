using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Produces the screenshots the README embeds, from the real plugin page running in a
/// real Jellyfin. Keeping them here rather than taking them by hand means they cannot
/// quietly drift away from the UI they document: regenerate with
///
///   make screenshots
///
/// Not part of an ordinary test run — it writes into the repository.
/// </summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "Screenshots")]
public sealed class ScreenshotTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public ScreenshotTests(JellyfinEnvironment env)
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
    public async Task Capture_the_pages_the_readme_describes()
    {
        var directory = Path.Combine(BrowserSupport.RepositoryRoot(), "docs", "img");
        Directory.CreateDirectory(directory);

        // The screenshots carry the same example values as the README, so a reader can
        // match what they see here against what they are told to type.
        await _env.PatchConfigAsync(config =>
        {
            var provider = config["Providers"]!.AsArray()[0]!;
            provider["Name"] = "Authentik";
            provider["Authority"] = "https://auth.example.com/application/o/jellyfin/";
            provider["ClientId"] = "jellyfin";
            provider["Scopes"] = new JsonArray();
        });

        var page = await OpenConfigPageAsync();

        // The provider card, with the redirect URI the quick start tells people to copy.
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");
        await page.Locator(".sso-provider").First.ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = Path.Combine(directory, "provider-card.png"),
        });

        // A group mapping with its libraries and permissions.
        await page.ClickAsync(".sso-tab[data-tab=\"mappings\"]");
        await page.Locator(".sso-mapping").First.ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = Path.Combine(directory, "group-mapping.png"),
        });

        foreach (var file in new[] { "provider-card.png", "group-mapping.png" })
        {
            Assert.True(new FileInfo(Path.Combine(directory, file)).Length > 5_000, file);
        }
    }

    /// <summary>Opens the plugin page with a seeded administrator session.</summary>
    private async Task<IPage> OpenConfigPageAsync()
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1100, Height = 900 },
        });

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
                    ["ManualAddress"] = _env.BaseUrl,
                    ["LastConnectionMode"] = 2,
                    ["DateLastAccessed"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            },
        };

        await context.AddInitScriptAsync(
            "window.localStorage.setItem('jellyfin_credentials', "
            + JsonSerializer.Serialize(credentials.ToJsonString()) + ");"
            + "window.localStorage.setItem('_deviceId2', 'e2e-screenshots');");

        var page = await context.NewPageAsync();
        await page.GotoAsync("/web/index.html#/configurationpage?name=SSO%20Authentication");
        await page.WaitForSelectorAsync("#GroupMappings", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = 30_000,
        });
        await page.WaitForFunctionAsync(
            "() => document.querySelectorAll('.sso-mapping').length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        return page;
    }
}
