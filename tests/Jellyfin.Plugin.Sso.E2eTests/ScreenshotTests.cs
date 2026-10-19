using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Produces the screenshots the README and docs/ embed, from the real plugin page in a
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

        // The screenshots carry the same example values as the documentation, so a reader
        // can match what they see here against what they are told to type.
        await _env.PatchConfigAsync(config =>
        {
            var provider = config["Providers"]!.AsArray()[0]!;
            provider["Name"] = "Authentik";
            provider["Authority"] = "https://auth.example.com/application/o/jellyfin/";
            provider["ClientId"] = "jellyfin";
            provider["Enabled"] = false;
            provider["Scopes"] = new JsonArray();
        });

        var page = await OpenConfigPageAsync();

        // The provider card, with the redirect URI the quick start tells people to copy.
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");
        await ShootAsync(page, ".sso-provider", Path.Combine(directory, "provider-card.png"));

        // A group mapping with its libraries and permissions.
        await page.ClickAsync(".sso-tab[data-tab=\"mappings\"]");
        await ShootAsync(page, ".sso-mapping", Path.Combine(directory, "group-mapping.png"));

        // The manual linking form, the fiddliest screen and the one the docs can only
        // describe in words.
        await page.ClickAsync(".sso-tab[data-tab=\"links\"]");
        // The form lives in a collapsed <details>; a screenshot of it closed shows a line.
        await page.ClickAsync(".sso-link-form-summary");
        await page.WaitForSelectorAsync("#LinkSub", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 10_000,
        });
        await ShootAsync(page, "[data-panel='links']", Path.Combine(directory, "account-links.png"));

        foreach (var file in new[] { "provider-card.png", "group-mapping.png", "account-links.png" })
        {
            Assert.True(new FileInfo(Path.Combine(directory, file)).Length > 5_000, file);
        }
    }

    /// <summary>
    /// Screenshots one element with room around it. A flush crop pasted into a document
    /// looks like a hole in it, so the element is wrapped in a padded, rounded frame in
    /// the element's own surface colour for the duration of the shot.
    /// </summary>
    private static async Task ShootAsync(IPage page, string selector, string path)
    {
        var element = page.Locator(selector).First;
        await element.EvaluateAsync(
            """
            el => {
              const opaque = colour =>
                colour && colour !== 'transparent' && !colour.startsWith('rgba(0, 0, 0, 0)');

              let surface = getComputedStyle(el).backgroundColor;
              for (let node = el; node && !opaque(surface); node = node.parentElement) {
                surface = getComputedStyle(node).backgroundColor;
              }

              const frame = document.createElement('div');
              frame.setAttribute('data-shot', '');
              frame.style.padding = '28px';
              frame.style.borderRadius = '10px';
              frame.style.background = opaque(surface) ? surface : 'rgb(32, 32, 32)';
              el.parentNode.insertBefore(frame, el);
              frame.appendChild(el);
            }
            """);

        await page.Locator("[data-shot]").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = path,
            OmitBackground = true,
        });

        // Put the page back the way it was, so the next shot sees the original tree.
        await page.EvaluateAsync(
            """
            () => {
              const frame = document.querySelector('[data-shot]');
              if (!frame) return;
              frame.parentNode.insertBefore(frame.firstElementChild, frame);
              frame.remove();
            }
            """);
    }

    /// <summary>Opens the plugin page with a seeded administrator session.</summary>
    private async Task<IPage> OpenConfigPageAsync()
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1100, Height = 900 },
            // Retina-sized, so the images stay readable when a browser scales them down.
            DeviceScaleFactor = 2,
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
