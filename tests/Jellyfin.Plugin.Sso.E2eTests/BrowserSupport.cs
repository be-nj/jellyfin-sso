using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>Shared Playwright plumbing: browser install and failure artifacts.</summary>
internal static class BrowserSupport
{
    public static async Task<IPlaywright> InstallAsync()
    {
        // Keep the browser download inside tests/runs/ like every other test artifact.
        Environment.SetEnvironmentVariable(
            "PLAYWRIGHT_BROWSERS_PATH",
            Path.Combine(RunsDirectory(), "playwright", "browsers"));

        // CI images ship the shared libraries Chromium needs; a bare container does not,
        // so E2E_PLAYWRIGHT_WITH_DEPS=1 lets it install them (needs root).
        string[] install = Environment.GetEnvironmentVariable("E2E_PLAYWRIGHT_WITH_DEPS") == "1"
            ? ["install", "chromium", "--with-deps"]
            : ["install", "chromium"];

        if (Program.Main(install) != 0)
        {
            throw new InvalidOperationException("Could not install the Chromium build Playwright needs.");
        }

        return await Playwright.CreateAsync();
    }

    /// <summary>
    /// The network errors a CI runner produces while Docker is still rearranging itself,
    /// as Chromium reports them.
    ///
    /// Starting or stopping a container creates or tears down a bridge network and
    /// rewrites firewall rules, and a navigation in flight while that happens is
    /// abandoned. It says nothing about the server or the page: one of three matrix jobs
    /// failed with ERR_NETWORK_CHANGED on a navigation 237 milliseconds into a test, and
    /// the other two did not.
    ///
    /// One list, because there were two of them. RealIdentityProviderTests kept its own,
    /// and the two had diverged: this one knew ERR_CONNECTION_REFUSED and
    /// ERR_EMPTY_RESPONSE, that one knew ERR_NETWORK_IO_SUSPENDED,
    /// ERR_INTERNET_DISCONNECTED, ERR_NAME_NOT_RESOLVED and ERR_CONNECTION_RESET, and
    /// neither knew the other's. Both read this one now, so a name learned once is known
    /// everywhere.
    /// </summary>
    private static readonly string[] RunnerNetworkErrors =
    [
        "ERR_NETWORK_CHANGED",
        "ERR_NETWORK_IO_SUSPENDED",
        "ERR_INTERNET_DISCONNECTED",
        "ERR_NAME_NOT_RESOLVED",
        "ERR_CONNECTION_RESET",
        "ERR_CONNECTION_REFUSED",
        "ERR_EMPTY_RESPONSE",
    ];

    /// <summary>Does this message name one of the runner's own network errors?</summary>
    public static bool IsRunnerNetworkError(string message)
        => RunnerNetworkErrors.Any(name => message.Contains(name, StringComparison.Ordinal));

    /// <summary>
    /// Navigates, and tries again when the runner's own network moved under the request.
    ///
    /// Every navigation in this suite goes through here. Not every container is up before
    /// the first test: the three real providers and the prefixing proxy start inside the
    /// class that needs them and stop when it is done, so whichever test is navigating at
    /// that moment is a matter of ordering, not of which class asked for the container.
    /// Restricting the retry to the classes that start something of their own therefore
    /// left the rest exposed to their neighbours — seventeen of the suite's nineteen
    /// navigations, the `real-idp` check among them.
    ///
    /// This retries one operation that failed for a stated reason, which is a different
    /// thing from waiting a while and hoping: anything but those errors is raised at once,
    /// and a last attempt that fails fails the test. Three attempts one and two seconds
    /// apart also cannot turn a server that is down into a server that is up — the wait
    /// strategy in JellyfinEnvironment does that, and it has already returned before any
    /// test navigates anywhere.
    /// </summary>
    public static async Task<IResponse?> GotoAsync(IPage page, string url, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await page.GotoAsync(url);
            }
            catch (PlaywrightException ex)
                when (attempt < attempts && IsRunnerNetworkError(ex.Message))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    /// <summary>Saves a screenshot and the page HTML under tests/runs/playwright.</summary>
    /// <summary>
    /// How long jellyfin-web gets to do its own work: boot, adopt a handed-over session,
    /// render the page a test is about. Not how long the plugin gets — the plugin's own
    /// answers are HTTP requests, and those are fast or broken.
    ///
    /// Generous on purpose, and it costs nothing when the client is quick: every wait
    /// using it returns the moment its condition holds, so this number only decides what
    /// happens on a loaded machine. Thirty seconds was not enough three times in two
    /// days, at three different waits — the session handoff twice (once locally beside
    /// the unit suite, once on a GitHub runner) and the user profile page once
    /// ([#224](https://github.com/be-nj/jellyfin-sso/issues/224)). Three waits, one
    /// cause: a single-core-ish runner with containers on it, and a web client that has
    /// to fetch and execute a bundle before anything a test looks for exists.
    ///
    /// For the session in particular, the handoff cannot race it: SsoPages writes the
    /// credentials into localStorage and only then navigates to the web client, so the
    /// client always boots after they exist. A client that has not adopted them yet is
    /// still starting, not stuck, and waiting longer is the right answer rather than a
    /// way of hiding one.
    /// </summary>
    public const int WebClientTimeout = 90_000;

    /// <summary>
    /// Waits until the web client itself reports a usable session: its ApiClient has an
    /// access token and knows which user it belongs to. That is what the localStorage
    /// handoff is for, and asserting it is stronger than reading the credentials back —
    /// it says the client adopted them rather than that they were written.
    ///
    /// Six call sites waited on this with the same string and their own timeout, and the
    /// one that failed in CI had no diagnosis at all: a bare TimeoutException from
    /// Playwright, with no record of what the page was showing. The message below names
    /// which half went wrong, because the two mean opposite things. Credentials present
    /// and no session means the client was still starting. No credentials means the login
    /// never finished, and the problem is upstream of anything this wait can see.
    /// </summary>
    public static async Task WaitForSignedInAsync(
        IPage page, string name, IEnumerable<string>? consoleMessages = null)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "() => !!(window.ApiClient && window.ApiClient.accessToken() "
                + "&& window.ApiClient.getCurrentUserId())",
                null,
                new PageWaitForFunctionOptions { Timeout = WebClientTimeout });
        }
        catch (TimeoutException)
        {
            await CaptureAsync(page, name);

            Assert.Fail(
                $"The web client never adopted the SSO session within "
                + $"{WebClientTimeout / 1000} s.\n"
                + $"url:         {page.Url}\n"
                + $"credentials: {await DescribeAsync(page, CredentialsProbe)}\n"
                + $"ApiClient:   {await DescribeAsync(page, ApiClientProbe)}\n"
                + $"console:     {string.Join(" | ", consoleMessages ?? [])}");
        }
    }

    private const string CredentialsProbe =
        "() => { const raw = localStorage.getItem('jellyfin_credentials');"
        + " if (!raw) return 'none in localStorage';"
        + " try { const s = (JSON.parse(raw).Servers || [])[0];"
        + " return s ? ('for ' + s.UserName + ' at ' + s.ManualAddress) : 'written but empty'; }"
        + " catch (e) { return 'unreadable: ' + e.message; } }";

    private const string ApiClientProbe =
        "() => !window.ApiClient ? 'not created yet \u2014 the client is still starting'"
        + " : (window.ApiClient.accessToken() ? 'has a token, no user id yet'"
        + " : 'created without a token')";

    /// <summary>
    /// One probe's answer, or why it could not be asked. A page in the middle of a
    /// navigation throws here, and losing the whole failure message to that would leave
    /// exactly the empty report this helper exists to replace.
    /// </summary>
    private static async Task<string> DescribeAsync(IPage page, string probe)
    {
        try
        {
            return await page.EvaluateAsync<string>(probe);
        }
        catch (PlaywrightException e)
        {
            return $"could not be read: {e.Message.Split('\n')[0]}";
        }
    }

    public static async Task<string> CaptureAsync(IPage page, string name)
    {
        var directory = Path.Combine(RunsDirectory(), "playwright");
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(directory, $"{name}.png"),
            FullPage = true,
        });
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.html"), await page.ContentAsync());
        return directory;
    }

    /// <summary>The repository root, found by the manifest that sits in it.</summary>
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    public static string RunsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root."),
            "tests",
            "runs");
    }
}
