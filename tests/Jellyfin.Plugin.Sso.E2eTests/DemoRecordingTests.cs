using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Records the login the README shows, against a real Authentik rather than a mock-up:
/// Jellyfin's login page, the plugin's button, Authentik's own form, and the web client
/// coming up signed in. The flow is the one <see cref="RealIdentityProviderTests"/>
/// asserts; this run exists to be watched, so it is paced for a reader and it fails if
/// any step does not happen, which keeps the recording honest.
///
/// Not part of any automated run — the clip is a documentation artifact, regenerated with
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
        _profile = Path.Combine(_directory, "profile");
        if (Directory.Exists(_profile))
        {
            Directory.Delete(_profile, recursive: true);
        }

        Directory.CreateDirectory(_profile);

        _idp = await AuthentikProvider.StartAsync(
            _env.Certificate,
            _env.WorkDirectory,
            $"{_env.BaseUrl}/sso/oidc/callback/{ProviderId}");

        _playwright = await BrowserSupport.InstallAsync();

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

    public async Task DisposeAsync()
    {
        _playwright.Dispose();
        await _idp.DisposeAsync();
    }

    [Fact]
    public async Task Record_a_login_through_the_button()
    {
        var run = await WalkToTheProviderAsync();
        var page = run.Page;

        // Authentik's own form, filled at a readable speed rather than pasted.
        var username = page.Locator("input[name='uidField']");
        await Beat(page, 1_200);
        await username.PressSequentiallyAsync(_idp.MemberUser.Username, new LocatorPressSequentiallyOptions
        {
            Delay = 90,
        });
        await Beat(page, 500);
        await page.ClickAsync("button[type='submit']");

        // The stages swap the whole form out, so the password field has to be waited for.
        await username.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Detached,
            Timeout = 60_000,
        });
        var password = page.Locator("input[name='password']");
        await password.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000,
        });
        await Beat(page, 700);

        // The field masks it, and it belongs to a container that is thrown away with the
        // test run — so the recording can show it being typed like any other password.
        await password.PressSequentiallyAsync(AuthentikProvider.Password, new LocatorPressSequentiallyOptions
        {
            Delay = 70,
        });
        await Beat(page, 400);
        await page.ClickAsync("button[type='submit']");

        // Back in the web client, signed in: the libraries of the user's group are there.
        await page.WaitForURLAsync(
            url => !url.Contains("login.html", StringComparison.Ordinal)
                && !url.Contains("/if/flow/", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 60_000 });
        await page.WaitForSelectorAsync(
            ".homeSectionsContainer, .itemsContainer, .sectionTitle",
            new PageWaitForSelectorOptions { Timeout = 60_000 });
        await Beat(page, 3_000);

        // The session is real, not a screen that merely looks signed in.
        var policy = await _env.UserPolicyAsync(_idp.MemberUser.Username);
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());

        // The recording is written when the context closes, and a persistent context takes
        // the browser down with it — so ask for the file's name first and copy it after,
        // rather than asking the closed browser to save it.
        var clip = Path.Combine(_directory, "login-flow.webm");
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

    /// <summary>One walk from Jellyfin's login page to the provider's own form.</summary>
    private async Task<Walk> WalkToTheProviderAsync()
    {
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            BaseURL = _env.BaseUrl,
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

        // Authentik's login page is the slow part of this recording, and when it does not
        // come up the interesting question is what it was waiting for.
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

        await Beat(page, 2_000);

        // Hovering first: the clip should show this is a button, not a line of text.
        await button.HoverAsync();
        await Beat(page, 900);

        var clock = Stopwatch.StartNew();
        await button.ClickAsync();
        try
        {
            await page.Locator("input[name='uidField']").WaitForAsync(new LocatorWaitForOptions
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
