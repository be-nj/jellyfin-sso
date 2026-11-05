using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The path a TV takes: the app asks Jellyfin for a Quick Connect code, somebody signs in
/// through the provider in a browser and approves that code, and the app is signed in.
/// The approval is a deliberate second step — a link carrying somebody else's code would
/// otherwise hand them a session, with a genuine provider login in between to make it
/// look right.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class QuickConnectTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public QuickConnectTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_login_approves_the_code_only_after_the_person_says_so()
    {
        var (code, secret) = await InitiateAsync();

        // The SSO login itself must not approve anything yet.
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), quickConnectCode: code);

        Assert.Equal(HttpStatusCode.OK, login.CallbackStatus);
        Assert.Contains("Approve this device?", login.Body!);
        Assert.Contains(code, login.Body!);
        Assert.False(await IsAuthorizedAsync(secret));

        // …and then it does.
        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;
        Assert.NotEmpty(token);

        using var client = JellyfinEnvironment.TrustingClient();
        var approval = await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));

        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.True(await IsAuthorizedAsync(secret));
    }

    /// <summary>
    /// The whole flow on a server with two providers, which is the one way into Quick
    /// Connect that nothing walked: the picker's link.
    ///
    /// It built /sso/oidc/start/{id}?code=… — the ordinary login route, which does not read
    /// the code — so choosing a provider signed the browser in and left the device waiting
    /// for ever. Three tests covered Quick Connect and three covered more than one
    /// provider, and the pair of them was covered by none, which is how this survived from
    /// October.
    /// </summary>
    [Fact]
    public async Task A_second_provider_does_not_cost_the_waiting_device_its_approval()
    {
        var (code, secret) = await InitiateAsync();
        await _env.AddSecondProviderAsync();

        using var picker = JellyfinEnvironment.TrustingClient();
        var choices = await picker.GetStringAsync($"{_env.BaseUrl}/sso/oidc/start/qc?code={code}");

        var chosen = Regex.Match(choices, @"href=""(?<href>[^""]*/sso/oidc/start/[^""]+)""")
            .Groups["href"].Value;
        Assert.NotEmpty(chosen);

        // Followed, rather than rebuilt: the address under test is the one the page offers.
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-picked", "picked-user", ["media"]),
            startUrl: $"{_env.BaseUrl}{chosen}");

        Assert.Equal(HttpStatusCode.OK, login.CallbackStatus);
        Assert.Contains("Approve this device?", login.Body!);
        Assert.False(await IsAuthorizedAsync(secret));

        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""")
            .Groups["t"].Value;
        Assert.NotEmpty(token);

        using var client = JellyfinEnvironment.TrustingClient();
        var approval = await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));

        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        Assert.True(await IsAuthorizedAsync(secret));
    }

    [Fact]
    public async Task An_approval_token_cannot_be_used_twice()
    {
        var (code, _) = await InitiateAsync();
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), quickConnectCode: code);
        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;

        using var client = JellyfinEnvironment.TrustingClient();
        await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));
        var second = await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_invented_approval_token_is_refused()
    {
        using var client = JellyfinEnvironment.TrustingClient();

        var response = await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", new string('A', 64))]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Starts a Quick Connect request the way a TV app would.</summary>
    private async Task<(string Code, string Secret)> InitiateAsync()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Add(
            "Authorization",
            "MediaBrowser Client=\"e2e-tv\", Device=\"TV\", DeviceId=\"e2e-tv-device\", Version=\"1.0\"");

        var response = await client.PostAsync($"{_env.BaseUrl}/QuickConnect/Initiate", null);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
        return (body!["Code"]!.GetValue<string>(), body["Secret"]!.GetValue<string>());
    }

    /// <summary>Whether Jellyfin now hands the device a token for that request.</summary>
    private async Task<bool> IsAuthorizedAsync(string secret)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        var response = await client.GetAsync($"{_env.BaseUrl}/QuickConnect/Connect?secret={secret}");

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
        return body?["Authenticated"]?.GetValue<bool>() == true;
    }
}
