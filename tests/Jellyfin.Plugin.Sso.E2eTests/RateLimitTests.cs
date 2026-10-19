using System.Net;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The endpoints a login needs before anybody is signed in cannot ask for a token, so
/// anyone who can reach the server can reach them. Each one costs the server work — a
/// pending state for ten minutes, a token exchange with the provider — and the limit is
/// what keeps that from being free. These tests drive it through the running server
/// rather than the class, because the part worth checking is that the limit is actually
/// wired to the endpoints.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class RateLimitTests : IAsyncLifetime
{
    private const int Limit = 5;

    private readonly JellyfinEnvironment _env;

    public RateLimitTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public async Task InitializeAsync()
    {
        await _env.ResetAsync();
        await _env.PatchConfigAsync(config => config["LoginRequestsPerMinute"] = Limit);
    }

    /// <summary>Back to off, so the rest of the suite is not counted.</summary>
    public async Task DisposeAsync()
        => await _env.PatchConfigAsync(config => config["LoginRequestsPerMinute"] = 0);

    private async Task<HttpResponseMessage> StartAsync(HttpClient client)
        => await client.GetAsync(
            $"{_env.BaseUrl}/sso/oidc/start/{JellyfinEnvironment.ProviderId}",
            HttpCompletionOption.ResponseHeadersRead);

    [Fact]
    public async Task A_caller_past_the_limit_is_turned_away_and_told_when_to_come_back()
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        for (var i = 0; i < Limit; i++)
        {
            using var allowed = await StartAsync(client);
            Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
        }

        using var refused = await StartAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task The_limit_covers_the_callback_as_well_as_the_start()
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        for (var i = 0; i < Limit; i++)
        {
            using var _ = await StartAsync(client);
        }

        // A callback carries no valid state here; the point is that it is refused before
        // the server looks, because looking is the work being rationed.
        using var callback = await client.GetAsync(
            $"{_env.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}?code=x&state=y");

        Assert.Equal(HttpStatusCode.TooManyRequests, callback.StatusCode);
    }

    [Fact]
    public async Task The_login_page_itself_is_never_rationed()
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        for (var i = 0; i < Limit * 3; i++)
        {
            using var _ = await StartAsync(client);
        }

        // Jellyfin's own login page, the button's script and the mark on it have to keep
        // working for everybody else in the house while one caller is being refused.
        using var script = await client.GetAsync($"{_env.BaseUrl}/sso/login-button.js");
        using var mark = await client.GetAsync(
            $"{_env.BaseUrl}/sso/icon/provider/{JellyfinEnvironment.ProviderId}");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mark.StatusCode);
    }

    [Fact]
    public async Task Turning_the_limit_off_lets_everything_through()
    {
        await _env.PatchConfigAsync(config => config["LoginRequestsPerMinute"] = 0);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        for (var i = 0; i < Limit * 3; i++)
        {
            using var response = await StartAsync(client);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }
    }
}
