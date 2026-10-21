using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Records the login the documentation shows, against real providers rather than a
/// mock-up: Jellyfin's login page, the plugin's button, the provider's own form, and the
/// web client coming up signed in. The flow is the one <see cref="RealIdentityProviderTests"/>
/// asserts; this run exists to be watched, so it is paced for a reader and it fails if any
/// step does not happen, which keeps the recording honest.
///
/// One clip per provider: Authentik is the one the README shows, Keycloak and Authelia are
/// linked from it, because the three login pages look nothing alike and the question
/// "what does this look like with mine" deserves an answer.
///
/// Not part of any automated run — the clips are documentation artifacts, regenerated with
/// `make demo` when the login looks different.
/// </summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "Demo")]
public sealed class DemoRecordingTests : IAsyncLifetime
{
    private const string ProviderId = "demo-provider";

    /// <summary>Playwright records at the context's viewport size; this is the clip's size.</summary>
    private static readonly ViewportSize Frame = new() { Width = 1280, Height = 800 };

    private readonly JellyfinEnvironment _env;
    private IPlaywright _playwright = null!;
    private IRealIdentityProvider _idp = null!;
    private string _directory = string.Empty;
    private string _profile = string.Empty;

    public DemoRecordingTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public async Task InitializeAsync()
    {
        await _env.ResetAsync();

        _directory = Path.Combine(BrowserSupport.RunsDirectory(), "demo");
        _playwright = await BrowserSupport.InstallAsync();
    }

    public async Task DisposeAsync()
    {
        _playwright.Dispose();
        if (_idp is not null)
        {
            await _idp.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("authentik")]
    [InlineData("keycloak")]
    [InlineData("authelia")]
    public async Task Record_a_login_through_the_button(string provider)
    {
        _idp = await StartAsync(provider);

        // A browser profile per provider, so one recording never starts signed in from
        // the last one.
        _profile = Path.Combine(_directory, "profile", provider);
        if (Directory.Exists(_profile))
        {
            Directory.Delete(_profile, recursive: true);
        }

        Directory.CreateDirectory(_profile);

        var (parents, kids, family) = await _env.AddDemoLibrariesAsync();

        await _env.PatchConfigAsync(config =>
        {
            config["Providers"] = new JsonArray
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
            };

            // Jellyfin's own logout drops its token and leaves the provider's session
            // alone, so the second login would walk straight back in as the first user.
            // This is the setting that ends both, and the clip is the demonstration.
                // The point of the clip: one group opens Parents and Family, the other Kids
            // and Family, and the same server looks different to each of them.
            config["GroupMappings"] = new JsonArray
            {
                Mapping(_idp.MemberUser.Groups[0], parents, family),
                Mapping(_idp.KidUser.Groups[0], kids, family),
            };
        });

        var run = await WalkToTheProviderAsync();
        var page = run.Page;

        await SignInForTheRecordingAsync(page, _idp.MemberUser);
        await Beat(page, 2_400);

        // The session is real, not a screen that merely looks signed in.
        var parentPolicy = await _env.UserPolicyAsync(_idp.MemberUser.Username);
        Assert.False(parentPolicy["IsAdministrator"]!.GetValue<bool>());
        Assert.Equal(2, parentPolicy["EnabledFolders"]!.AsArray().Count);

        await SignOutForTheRecordingAsync(run);

        // And again as somebody else, to the same server and a different shelf.
        await StartTheLoginAsync(page);
        await SignInForTheRecordingAsync(page, _idp.KidUser);
        await Beat(page, 2_400);

        var kidPolicy = await _env.UserPolicyAsync(_idp.KidUser.Username);
        Assert.Equal(2, kidPolicy["EnabledFolders"]!.AsArray().Count);
        Assert.NotEqual(
            parentPolicy["EnabledFolders"]!.ToJsonString(),
            kidPolicy["EnabledFolders"]!.ToJsonString());

        // The recording is written when the context closes, and a persistent context takes
        // the browser down with it — so ask for the file's name first and copy it after,
        // rather than asking the closed browser to save it.
        var clip = Path.Combine(_directory, $"login-flow-{provider}.webm");
        var recorded = await page.Video!.PathAsync();
        await run.Context.CloseAsync();
        File.Copy(recorded, clip, overwrite: true);

        Assert.True(new FileInfo(clip).Length > 100_000, $"{clip} is too small to be a recording.");

        // A clip in which the viewer waits for a provider is not worth committing.
        Assert.True(
            run.FormAfter < TimeSpan.FromSeconds(15),
            $"The provider's login form took {run.FormAfter.TotalSeconds:F1}s to appear, "
            + "which is too slow to watch.");
    }

    private static JsonNode Mapping(string group, params string[] libraries) => new JsonObject
    {
        ["Group"] = group,
        ["LibraryIds"] = new JsonArray(libraries.Select(id => (JsonNode)id!).ToArray()),
        ["IsAdministrator"] = false,
    };

    /// <summary>
    /// This provider's own form, then the wait for the web client to come back signed in.
    /// </summary>
    private async Task SignInForTheRecordingAsync(IPage page, RealIdentityUser user)
    {
        await _idp.SignInForTheRecordingAsync(page, user);

        await page.WaitForURLAsync(
            url => url.StartsWith(ServerBaseUrl(), StringComparison.Ordinal)
                && !url.Contains("/login", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 60_000 });
        await page.WaitForSelectorAsync(
            ".homeSectionsContainer, .itemsContainer, .sectionTitle",
            new PageWaitForSelectorOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// Signs out the way a viewer would: the menu at the top left, then Sign Out in the
    /// drawer. Jellyfin's own route, so the clip shows the plugin inside the web client
    /// rather than a URL somebody had to know.
    ///
    /// Both selectors ask for the visible one. jellyfin-web keeps the login page's own
    /// header and drawer in the document after the login, measuring nothing, and they come
    /// first — clicking those waits for something that will never be clickable.
    /// </summary>
    private async Task SignOutForTheRecordingAsync(Walk run)
    {
        var page = run.Page;
        await Beat(page, 600);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "User Menu" })
            .ClickAsync();
        await Beat(page, 900);

        var signOut = page.Locator("[role=menu] [role=menuitem], .MuiMenuItem-root").Filter(
            new LocatorFilterOptions { HasTextRegex = SignOutText() });
        if (await signOut.CountAsync() == 0)
        {
            var items = await page.Locator("[role=menu] [role=menuitem], .MuiMenuItem-root")
                .AllInnerTextsAsync();
            Assert.Fail($"No sign-out entry in the user menu. Entries: {string.Join(" | ", items)}");
        }

        await signOut.First.ClickAsync();

        // Jellyfin 12 routes to #/login; older clients used #/login.html.
        await page.WaitForURLAsync(
            url => url.Contains("/login", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 30_000 });
        await Beat(page, 700);

        // Jellyfin has forgotten this session. The provider has not: without an
        // id_token_hint it asks rather than acts, so the next login would walk straight
        // back in as the same person (issue #84). The clip shows the next person at their
        // own browser, which is what the cookies are cleared for — and what two people in
        // a household actually are.
        await run.Context.ClearCookiesAsync();
        await Beat(page, 600);
    }

    /// <summary>
    /// What the entry is called. Jellyfin 12's web client is a React application and its
    /// wording has moved before; matching both spellings costs nothing.
    /// </summary>
    private static Regex SignOutText() => new("sign ?out|log ?out", RegexOptions.IgnoreCase);

    /// <summary>The plugin's button on the login page, and the wait for the provider.</summary>
    private async Task StartTheLoginAsync(IPage page)
    {
        var button = page.Locator("button.sso-login-button");
        await button.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await Beat(page, 900);
        await button.ClickAsync();

        await page.Locator(_idp.LoginFormSelector).WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000,
        });
    }

    private Task<IRealIdentityProvider> StartAsync(string provider) => provider switch
    {
        "authentik" => Upcast(AuthentikProvider.StartAsync(
            _env.Certificate,
            _env.WorkDirectory,
            $"{_env.BaseUrl}/sso/oidc/callback/{ProviderId}")),
        "keycloak" => Upcast(KeycloakProvider.StartAsync(_env.Certificate, _env.WorkDirectory)),
        "authelia" => Upcast(AutheliaProvider.StartAsync(
            _env.Certificate, _env.WorkDirectory, _env.LocalhostBaseUrl, ProviderId)),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No such provider."),
    };

    private static async Task<IRealIdentityProvider> Upcast<T>(Task<T> starting)
        where T : IRealIdentityProvider
        => await starting;

    /// <summary>Jellyfin's address as this recording's browser has to reach it.</summary>
    private string ServerBaseUrl() => _idp.ServerBaseUrl ?? _env.BaseUrl;

    /// <summary>One walk from Jellyfin's login page to the provider's own form.</summary>
    private async Task<Walk> WalkToTheProviderAsync()
    {
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            BaseURL = ServerBaseUrl(),
            IgnoreHTTPSErrors = true,
            ViewportSize = Frame,
            RecordVideoDir = Path.Combine(_directory, "raw"),
            RecordVideoSize = new RecordVideoSize { Width = Frame.Width, Height = Frame.Height },
            // The provider is published on the host under the name its issuer uses, so
            // that the server container and the browser agree about the address.
            Args = [$"--host-resolver-rules=MAP {TestCertificate.HostName} 127.0.0.1"],
        };

        var context = await _playwright.Chromium.LaunchPersistentContextAsync(_profile, options);

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();

        // Playwright drives the mouse but a recording shows no cursor, so a viewer sees
        // the page change without seeing what was clicked. This draws the pointer it is
        // moving, and a ring where it clicks, into the recording itself.
        //
        // The same option also writes the name of each action into a corner —
        // `Hover Locator("button.sso-login-button")`, which is the right thing in a
        // failure trace and the wrong thing in a clip somebody is meant to watch. It
        // cannot be switched off on its own and `HideOverlaysAsync` does not cover it,
        // so the font is set small enough to disappear.
        await page.Screencast.ShowActionsAsync(new ScreencastShowActionsOptions
        {
            Cursor = ScreencastCursor.Pointer,
            Duration = 900,
            Position = AnnotatePosition.BottomRight,
            FontSize = 1,
        });

        // The provider's login page is the slow part of this recording, and when it does
        // not come up the interesting question is what it was waiting for.
        var failed = new List<string>();
        page.RequestFailed += (_, request) => failed.Add($"{request.Method} {request.Url} {request.Failure}");
        var slow = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                slow.Add($"{response.Status} {response.Url}");
            }
        };

        await page.GotoAsync("/web/index.html#/login.html");
        var button = page.Locator("button.sso-login-button");
        await button.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });

        // The mark on the button is fetched from the provider; a clip that starts before
        // it arrives shows the button changing shape under the viewer.
        await page.WaitForFunctionAsync(
            "() => { const m = document.querySelector('.sso-login-mark');"
            + " return m && m.complete && m.naturalWidth > 0; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        await Beat(page, 1_600);

        // One action, one click. Hovering the button first reads better on a live screen,
        // but the recording decorates every pointer action with a ring, so a hover and a
        // click look like somebody clicking twice and the page ignoring the first.
        var clock = Stopwatch.StartNew();
        await button.ClickAsync();
        try
        {
            await page.Locator(_idp.LoginFormSelector).WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 180_000,
            });
        }
        catch (TimeoutException)
        {
            await BrowserSupport.CaptureAsync(page, "demo-recording");
            Assert.Fail(
                $"{_idp.Name}'s login form did not appear within {clock.Elapsed.TotalSeconds:F0}s.\n"
                + $"url: {page.Url}\n"
                + $"failed requests: {string.Join(" / ", failed.TakeLast(10))}\n"
                + $"error responses: {string.Join(" / ", slow.TakeLast(10))}\n"
                + $"provider: {await _idp.DiagnosticsAsync()}");
            throw;
        }

        return new Walk(context, page, clock.Elapsed);
    }

    /// <summary>
    /// A pause in the recording. `WaitForTimeoutAsync` is the wrong tool in a test that
    /// asserts something; here the pause is the point — without it the clip is a flicker.
    /// </summary>
    private static Task Beat(IPage page, int milliseconds) => page.WaitForTimeoutAsync(milliseconds);

    private sealed record Walk(IBrowserContext Context, IPage Page, TimeSpan FormAfter);
}
