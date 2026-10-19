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
    /// <summary>Authentik's own colour, which a branded button is painted in.</summary>
    private const string Orange = "#FD4B2D";


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
        var painted = await ButtonColoursAsync(branded: false, providerLogos: false);

        // Jellyfin's secondary button, whatever shade this release paints it: the test
        // cares that the plugin did not paint it something of its own.
        var theirs = await JellyfinButtonBackgroundAsync();
        Assert.Equal(theirs, painted.Background);
        Assert.Equal("none", painted.Mark);
        Assert.NotEqual(string.Empty, painted.Ink);
    }

    [Fact]
    public async Task A_branded_button_takes_the_providers_colour_and_a_readable_mark()
    {
        var painted = await ButtonColoursAsync(branded: true, providerLogos: false);

        // Authentik's orange, which is light enough that the mark and the label go black.
        Assert.True(
            painted.Background == "rgb(253, 75, 45)",
            $"the button is {painted.Background}, not Authentik's orange — {painted.Markup}");
        Assert.Equal("rgb(16, 20, 24)", painted.Ink);
        Assert.Equal("brightness(0)", painted.Mark);
        Assert.NotEqual(await JellyfinButtonBackgroundAsync(), painted.Background);
    }

    [Fact]
    public async Task A_logo_fetched_from_the_provider_is_not_flattened_to_one_colour()
    {
        var painted = await ButtonColoursAsync(branded: true, providerLogos: true);

        // brightness(0) turns a logo that carries its own background into a black
        // rectangle, which is what a household saw on its own Authentik's logo. It keeps
        // its colours and gets something light to sit on instead.
        Assert.Equal("none", painted.Mark);
        Assert.Equal("rgb(255, 255, 255)", painted.MarkBackground);
    }

    /// <summary>What the browser actually paints: surface, text, and the mark's filter.</summary>
    private async Task<Painted> ButtonColoursAsync(bool branded, bool providerLogos)
    {
        await _env.PatchConfigAsync(config =>
        {
            config["UseProviderLogos"] = providerLogos;
            var provider = config["Providers"]!.AsArray()[0]!;
            provider["Name"] = "Authentik";
            provider["BrandedLoginButton"] = branded;
        });

        // Saving the configuration returns before the branding carries the new stylesheet,
        // by a few hundred milliseconds on a loaded server. A page opened inside that
        // window is styled by the previous setting, and the test then measures Jellyfin's
        // own grey and calls it the plugin's — which is how this passed on 12.0 and 12.1
        // and failed on 12.2.
        var served = await ServedCssAsync(
            branded
                ? "background:" + Orange
                : $".sso-login-button[data-provider=\"{JellyfinEnvironment.ProviderId}\"]{{--sso-mark:",
            TimeSpan.FromSeconds(30));

        await using var context = await NewPageContextAsync();
        var page = await ShowLoginPageAsync(context);

        var painted = await page.EvaluateAsync<string[]>(
            """
            () => {
              const button = document.querySelector('button.sso-login-button');
              const style = getComputedStyle(button);
              const mark = getComputedStyle(button.querySelector('.sso-login-mark'));
              return [
                style.backgroundColor, style.color, mark.filter,
                mark.backgroundColor, button.outerHTML,
              ];
            }
            """);

        var inPage = await page.EvaluateAsync<string>(
            """
            () => [...document.querySelectorAll('style')]
              .map(s => s.textContent || '')
              .filter(t => t.includes('sso-login-button'))
              .join(' | ')
            """);

        return new Painted(
            painted[0],
            painted[1],
            painted[2],
            painted[3],
            $"{painted[4]}\nserved: {Excerpt(served)}\nin page: {Excerpt(inPage)}");
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

    /// <summary>
    /// The stylesheet Jellyfin serves — where the plugin writes — once it carries
    /// <paramref name="expected"/>. Fails with what it did carry instead.
    /// </summary>
    private async Task<string> ServedCssAsync(string expected, TimeSpan within)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        var deadline = DateTime.UtcNow + within;
        var css = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            var branding = System.Text.Json.Nodes.JsonNode.Parse(
                await client.GetStringAsync($"{_env.BaseUrl}/Branding/Configuration"));
            css = branding?["CustomCss"]?.GetValue<string>() ?? string.Empty;

            if (css.Contains(expected, StringComparison.Ordinal))
            {
                return css;
            }

            await Task.Delay(200);
        }

        Assert.Fail(
            $"The branding never carried \"{expected}\" within {within.TotalSeconds:0}s.\n"
            + $"served: {Excerpt(css)}");
        return css;
    }

    private static string Excerpt(string css)
    {
        var at = css.IndexOf("data-provider", StringComparison.Ordinal);
        return at < 0 ? css : css[at..Math.Min(css.Length, at + 220)];
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

        // The button fades into its colour — .15s on background — and an ease curve is
        // nearly flat at the end, so two readings 120ms apart both caught
        // rgb(252, 75, 45) and looked settled one step short of the orange. Taking the
        // transition away makes the computed value the final one, with nothing to wait
        // for and nothing to be nearly right about.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = "*,*::before,*::after{transition:none !important;animation:none !important;}",
        });

        return page;
    }

    /// <summary>What the browser paints, and the element it painted, for a failure.</summary>
    private sealed record Painted(
        string Background, string Ink, string Mark, string MarkBackground, string Markup);
}
