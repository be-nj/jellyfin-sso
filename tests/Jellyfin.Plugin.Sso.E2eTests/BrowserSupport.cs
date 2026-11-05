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
    /// Starting a container creates a bridge network and rewrites firewall rules, and a
    /// navigation in flight while that happens is abandoned with ERR_NETWORK_CHANGED. It
    /// says nothing about the server or the page: one of three matrix jobs failed this way
    /// on a navigation 237 milliseconds into a test, and the other two did not.
    /// </summary>
    private static readonly string[] RunnerNetworkErrors =
    [
        "ERR_NETWORK_CHANGED",
        "ERR_CONNECTION_REFUSED",
        "ERR_EMPTY_RESPONSE",
    ];

    /// <summary>
    /// Navigates, and tries again when the runner's own network moved under the request.
    ///
    /// For the tests that start a container of their own and then drive a browser at it.
    /// This retries one operation that failed for a stated reason, which is a different
    /// thing from waiting a while and hoping: anything but those errors is raised at once,
    /// and a last attempt that fails fails the test.
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
                when (attempt < attempts
                    && RunnerNetworkErrors.Any(e => ex.Message.Contains(e, StringComparison.Ordinal)))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    /// <summary>Saves a screenshot and the page HTML under tests/runs/playwright.</summary>
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
