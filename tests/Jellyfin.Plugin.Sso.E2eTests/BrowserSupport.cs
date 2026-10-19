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
