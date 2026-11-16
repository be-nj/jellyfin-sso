using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The failure message of the session wait has to answer the question #282 left open:
/// after a reload, did the server refuse the stored token, or did the web client never
/// use it? These make the wait fail on purpose — a session nobody could adopt — and read
/// what it said, so the diagnosis is known to work before the next real sighting needs it.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class ReloadDiagnosticsTests(JellyfinEnvironment env) : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        await env.ResetAsync();
        _playwright = await BrowserSupport.InstallAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    /// <summary>
    /// A token the server has dropped, stored where the web client looks for it. The
    /// client's first authenticated request comes back 401 and it deletes the token, so
    /// the message has to show both halves — the 401 and the credentials now without a
    /// token — and must not print the token, which jellyfin-web itself writes to the
    /// console.
    /// </summary>
    [Fact]
    public async Task A_stored_token_the_server_dropped_is_reported_as_refused()
    {
        env.Idp.NextUser = new FakeUser("sub-diagnosed", "diagnoseduser", ["media"]);
        await using var traced = await TracedContext.StartAsync(_browser, new BrowserNewContextOptions
        {
            BaseURL = env.BaseUrl,
            IgnoreHTTPSErrors = true,
        });
        var page = await traced.NewPageAsync();
        await BrowserSupport.GotoAsync(page, "/sso/oidc/start");
        await BrowserSupport.WaitForSignedInAsync(page, "diagnosed-first");

        var token = await page.EvaluateAsync<string>("() => window.ApiClient.accessToken()");
        var userId = (await env.FindUserAsync("diagnoseduser"))!["Id"]!.GetValue<string>();
        await env.RemoveLinkAsync(Guid.Parse(userId));

        await page.ReloadAsync();
        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => BrowserSupport.WaitForSignedInAsync(page, "diagnosed-reload", timeoutMs: 15_000));

        Assert.Contains("for diagnoseduser", failure.Message);
        Assert.Contains("without a token", failure.Message);
        Assert.Contains("GET /System/Info -> 401", failure.Message);
        Assert.DoesNotContain(token, failure.Message);
    }
}
