using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The public address on a real server. Without it the redirect URI a provider is given
/// comes from the request's headers, which whoever sends the request writes; a provider
/// that accepts a redirect URI on any host then delivers the code wherever the header
/// said. With it the header decides nothing, and a login opened at another address is
/// taken to the public one before it starts.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class PublicAddressTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public PublicAddressTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => _env.PatchConfigAsync(config => config["PublicAddress"] = string.Empty);

    private Task SetAsync(string address) => _env.PatchConfigAsync(config => config["PublicAddress"] = address);

    /// <summary>One request to the start endpoint, as somebody who writes their own headers.</summary>
    private async Task<HttpResponseMessage> StartAsync(string pathAndQuery, bool forged)
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, _env.BaseUrl + pathAndQuery);
        if (forged)
        {
            request.Headers.Add("X-Forwarded-Host", "attacker.example");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Prefix", "/evil");
        }

        return await client.SendAsync(request);
    }

    private static string RedirectUri(HttpResponseMessage response)
        => System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["redirect_uri"]!;

    /// <summary>
    /// What the setting is for, shown first without it: the forged host is in the
    /// redirect URI the provider is handed. This is the state of things #302 describes,
    /// and it stays the state of a server that leaves the field empty.
    /// </summary>
    [Fact]
    public async Task Without_a_public_address_a_forged_host_is_what_the_provider_is_given()
    {
        using var response = await StartAsync("/sso/oidc/start", forged: true);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"https://attacker.example/evil/sso/oidc/callback/{JellyfinEnvironment.ProviderId}",
            RedirectUri(response));
    }

    [Fact]
    public async Task With_a_public_address_a_forged_host_reaches_neither_the_provider_nor_a_redirect()
    {
        await SetAsync(_env.BaseUrl);

        // The forged request looks as if it arrived elsewhere, so it is sent to the public
        // address — the configured one, not the one in its header.
        using var first = await StartAsync("/sso/oidc/start", forged: true);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal($"{_env.BaseUrl}/sso/oidc/start?at=public", first.Headers.Location!.ToString());

        // And forging again on the way there changes nothing about what the provider gets.
        using var second = await StartAsync("/sso/oidc/start?at=public", forged: true);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(
            $"{_env.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}",
            RedirectUri(second));
    }

    [Fact]
    public async Task A_login_at_the_public_address_goes_straight_to_the_provider_and_works()
    {
        await SetAsync(_env.BaseUrl + "/");

        using var start = await StartAsync("/sso/oidc/start", forged: false);
        // The provider's authorize address, which is the one that carries a redirect URI.
        Assert.Equal(
            $"{_env.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}", RedirectUri(start));

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-public", "publicuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);
    }

    /// <summary>
    /// The server opened at another name, the way a LAN address is another name for it.
    /// This login used to end, after the sign-in at the provider, on "could not be matched
    /// to a state this browser started". It is taken to the public address first, with
    /// the Quick Connect code it came with, and finishes there.
    /// </summary>
    [Fact]
    public async Task A_login_opened_at_another_address_is_taken_to_the_public_one_and_finishes_there()
    {
        await SetAsync(_env.BaseUrl);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{_env.BaseUrl}/sso/oidc/start/{JellyfinEnvironment.ProviderId}");
        request.Headers.Host = "jellyfin.lan:8096";
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var publicStart = response.Headers.Location!.ToString();
        Assert.Equal(
            $"{_env.BaseUrl}/sso/oidc/start/{JellyfinEnvironment.ProviderId}?at=public", publicStart);

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-lan", "lanuser", ["media"]), startUrl: publicStart);
        Assert.True(login.Succeeded, login.Body);
    }

    [Fact]
    public async Task The_code_of_a_quick_connect_link_survives_the_way_to_the_public_address()
    {
        await SetAsync(_env.BaseUrl);

        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/sso/oidc/start/qc?code=123456");
        request.Headers.Host = "jellyfin.lan:8096";
        using var response = await client.SendAsync(request);

        Assert.Equal($"{_env.BaseUrl}/sso/oidc/start/qc?code=123456&at=public", response.Headers.Location!.ToString());
    }

    /// <summary>
    /// The plugin's own save refuses a value that is no address. Jellyfin's generic
    /// configuration endpoint checks nothing, and the configuration is a file. Such a
    /// value stops the login and says so; it does not quietly hand the decision back to
    /// the headers.
    /// </summary>
    [Fact]
    public async Task A_stored_value_that_is_no_address_stops_the_login_and_says_why()
    {
        using var admin = JellyfinEnvironment.TrustingClient();
        admin.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(_env.AdminToken);
        var url = $"{_env.BaseUrl}/Plugins/{JellyfinEnvironment.PluginId}/Configuration";
        var config = JsonNode.Parse(await admin.GetStringAsync(url))!;
        config["PublicAddress"] = "jellyfin.example.com";
        (await admin.PostAsJsonAsync(url, config)).EnsureSuccessStatusCode();

        using var response = await StartAsync("/sso/oidc/start", forged: true);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("public address", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("jellyfin.example.com")]
    [InlineData("ftp://jellyfin.example.com")]
    [InlineData("https://user@jellyfin.example.com")]
    public async Task The_save_refuses_a_public_address_that_is_not_one(string address)
    {
        var result = await _env.TrySaveConfigAsync(config => config["PublicAddress"] = address);

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Contains("is not a public address", result.Body);
        Assert.Equal(string.Empty, (await _env.PluginConfigAsync())["PublicAddress"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_address_is_stored_in_the_form_the_provider_is_sent()
    {
        await SetAsync("  https://Jellyfin.Example.com/jf/  ");

        Assert.Equal(
            "https://jellyfin.example.com/jf",
            (await _env.PluginConfigAsync())["PublicAddress"]!.GetValue<string>());
    }
}
