using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The button has to line up with Jellyfin's own buttons. Eyeballing a screenshot missed
/// an eight-pixel inset twice, so the browser measures it here: same left edge, same
/// right edge, same width as the button above it.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class LoginButtonLayoutTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public LoginButtonLayoutTests(JellyfinEnvironment env)
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
    public async Task The_button_stands_in_the_same_column_as_jellyfins_own()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["Name"] = "Fake IdP");

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1100, Height = 900 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/web/index.html#/login.html");

        var ours = page.Locator("button.sso-login-button");
        await ours.First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        // "Use Quick Connect" — a button Jellyfin itself puts in that column.
        var theirs = page.Locator("button.raised.block.emby-button").First;

        var mine = await ours.First.BoundingBoxAsync();
        var jellyfin = await theirs.BoundingBoxAsync();

        var diagnostics = await page.EvaluateAsync<string>(
            """
            () => {
              const button = document.querySelector('button.sso-login-button');
              const style = getComputedStyle(button);
              const sheets = [...document.querySelectorAll('style')]
                .filter(s => (s.textContent || '').includes('sso-login-button')).length;
              return `margin-left=${style.marginLeft} display=${style.display} `
                + `parent=${button.parentElement.className} styleTags=${sheets}`;
            }
            """);

        Assert.NotNull(mine);
        Assert.NotNull(jellyfin);
        Assert.True(true, diagnostics);
        Assert.True(
            Math.Abs(mine!.X - jellyfin!.X) <= 1,
            $"left edges differ by {mine.X - jellyfin.X:0.#}px — {diagnostics}");
        Assert.True(
            Math.Abs(mine.Width - jellyfin.Width) <= 1,
            $"widths differ by {mine.Width - jellyfin.Width:0.#}px");

        await context.CloseAsync();
    }

    [Fact]
    public async Task The_plain_button_wears_jellyfins_own_surface()
    {
        var (background, ink, mark) = await ButtonColoursAsync(branded: false);

        // Jellyfin's secondary button, whatever shade this release paints it: the test
        // cares that the plugin did not paint it something of its own.
        var theirs = await JellyfinButtonBackgroundAsync();
        Assert.Equal(theirs, background);
        Assert.Equal("none", mark);
        Assert.NotEqual(string.Empty, ink);
    }

    [Fact]
    public async Task A_branded_button_takes_the_providers_colour_and_a_readable_mark()
    {
        var (background, ink, mark) = await ButtonColoursAsync(branded: true);

        // Authentik's orange, which is light enough that the mark and the label go black.
        Assert.Equal("rgb(253, 75, 45)", background);
        Assert.Equal("rgb(16, 20, 24)", ink);
        Assert.Equal("brightness(0)", mark);
        Assert.NotEqual(await JellyfinButtonBackgroundAsync(), background);
    }

    /// <summary>What the browser actually paints: surface, text, and the mark's filter.</summary>
    private async Task<(string Background, string Ink, string Mark)> ButtonColoursAsync(bool branded)
    {
        await _env.PatchConfigAsync(config =>
        {
            var provider = config["Providers"]!.AsArray()[0]!;
            provider["Name"] = "Authentik";
            provider["BrandedLoginButton"] = branded;
        });

        await using var context = await NewPageContextAsync();
        var page = await ShowLoginPageAsync(context);

        var colours = await page.EvaluateAsync<string[]>(
            """
            () => {
              const button = document.querySelector('button.sso-login-button');
              const style = getComputedStyle(button);
              const mark = getComputedStyle(button.querySelector('.sso-login-mark'));
              return [style.backgroundColor, style.color, mark.filter];
            }
            """);

        return (colours[0], colours[1], colours[2]);
    }

    /// <summary>
    /// The surface of a secondary button Jellyfin itself puts on that page — "Use Quick
    /// Connect", not "Sign In", which is painted in the accent colour.
    /// </summary>
    private async Task<string> JellyfinButtonBackgroundAsync()
    {
        await using var context = await NewPageContextAsync();
        var page = await ShowLoginPageAsync(context);

        return await page.EvaluateAsync<string>(
            """
            () => {
              const buttons = [...document.querySelectorAll(
                'button.raised.block.emby-button:not(.sso-login-button)')];
              const secondary = buttons.find(b => !b.classList.contains('button-submit'))
                || buttons[buttons.length - 1];
              return getComputedStyle(secondary).backgroundColor;
            }
            """);
    }

    private Task<IBrowserContext> NewPageContextAsync()
        => _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = _env.BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1100, Height = 900 },
        });

    private static async Task<IPage> ShowLoginPageAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync("/web/index.html#/login.html");
        await page.Locator("button.sso-login-button").First.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        return page;
    }
}
