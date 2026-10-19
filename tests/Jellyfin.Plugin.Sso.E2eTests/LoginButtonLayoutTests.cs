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
    }
}
