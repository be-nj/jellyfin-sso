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

    /// <summary>
    /// A code no device is waiting with: one that expired while the person was signing
    /// in, or was mistyped into the link. Jellyfin answers that with an exception the
    /// plugin did not catch, so the browser got Jellyfin's own 404 "Error processing
    /// request." after a sign-in that had worked (#300).
    /// </summary>
    [Fact]
    public async Task A_code_no_device_is_waiting_with_is_refused_in_words()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), quickConnectCode: "000000");
        var token = Regex.Match(login.Body!, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;
        Assert.NotEmpty(token);

        using var client = JellyfinEnvironment.TrustingClient();
        var approval = await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]));

        Assert.Equal(HttpStatusCode.BadRequest, approval.StatusCode);
        Assert.Contains("Quick Connect authorization failed", await approval.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The same code approved from two browsers, which is two people in a household
    /// opening the one link. Jellyfin refuses the second with a different exception, and
    /// that one reached the browser as a 500.
    /// </summary>
    [Fact]
    public async Task A_code_that_was_approved_already_is_refused_in_words()
    {
        var (code, secret) = await InitiateAsync();
        var first = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), quickConnectCode: code);
        var second = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv2", "tv-user-two", ["media"]), quickConnectCode: code);

        using var client = JellyfinEnvironment.TrustingClient();
        async Task<HttpResponseMessage> ApproveAsync(string body)
            => await client.PostAsync(
                $"{_env.BaseUrl}/sso/oidc/qc/approve",
                new FormUrlEncodedContent([new KeyValuePair<string, string>(
                    "token", Regex.Match(body, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value)]));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(first.Body!)).StatusCode);
        Assert.True(await IsAuthorizedAsync(secret));

        var again = await ApproveAsync(second.Body!);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("Quick Connect authorization failed", await again.Content.ReadAsStringAsync());
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

    // ─── The code typed in instead of taken from the link ─────────────────────

    private Task TypeTheCodeAsync()
        => _env.PatchConfigAsync(config => config["TypeQuickConnectCode"] = true);

    private static string TokenOf(string page)
        => Regex.Match(page, @"name=""token"" value=""(?<t>[A-F0-9]+)""").Groups["t"].Value;

    private async Task<(HttpStatusCode Status, string Body)> ApproveAsync(string token, string? code)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        var form = new List<KeyValuePair<string, string>> { new("token", token) };
        if (code is not null)
        {
            form.Add(new("code", code));
        }

        using var response = await client.PostAsync(
            $"{_env.BaseUrl}/sso/oidc/qc/approve", new FormUrlEncodedContent(form));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// What the setting is for. The link carries the code of somebody else's device; with
    /// the setting on the page neither shows nor approves it, and the code the person
    /// types lets their own device in and not the other one.
    /// </summary>
    [Fact]
    public async Task With_the_code_typed_in_a_code_from_the_link_is_neither_shown_nor_approved()
    {
        await TypeTheCodeAsync();
        var (theirs, theirSecret) = await InitiateAsync();
        var (mine, mySecret) = await InitiateAsync("my-own-tv");

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), quickConnectCode: theirs);

        Assert.Equal(HttpStatusCode.OK, login.CallbackStatus);
        Assert.Contains("Type the code your device is showing", login.Body!);
        Assert.DoesNotContain(theirs, login.Body!);

        // The token alone approves nothing now: the click that used to be enough.
        var page = login.Body!;
        var clicked = await ApproveAsync(TokenOf(page), code: null);
        Assert.Equal(HttpStatusCode.BadRequest, clicked.Status);
        Assert.False(await IsAuthorizedAsync(theirSecret));

        var typed = await ApproveAsync(TokenOf(clicked.Body), mine);
        Assert.Equal(HttpStatusCode.OK, typed.Status);
        Assert.True(await IsAuthorizedAsync(mySecret));
        Assert.False(await IsAuthorizedAsync(theirSecret));
    }

    [Fact]
    public async Task With_the_code_typed_in_the_link_needs_no_code_at_all()
    {
        await TypeTheCodeAsync();
        var (code, secret) = await InitiateAsync();

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), startUrl: $"{_env.BaseUrl}/sso/oidc/start/qc");
        Assert.Contains("Type the code your device is showing", login.Body!);

        // Typed the way a device shows it, in two groups.
        var approval = await ApproveAsync(TokenOf(login.Body!), $"{code[..3]} {code[3..]}");

        Assert.Equal(HttpStatusCode.OK, approval.Status);
        Assert.True(await IsAuthorizedAsync(secret));
    }

    /// <summary>
    /// A mistyped digit used to cost the whole sign-in: the token was spent before the
    /// code was looked at, and the right code on the same page answered "this approval
    /// has expired". The page comes back with a token for the next try.
    /// </summary>
    [Fact]
    public async Task A_mistyped_code_can_be_typed_again_on_the_page_that_says_so()
    {
        await TypeTheCodeAsync();
        var (code, secret) = await InitiateAsync();
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), startUrl: $"{_env.BaseUrl}/sso/oidc/start/qc");
        var first = TokenOf(login.Body!);

        var wrong = await ApproveAsync(first, code == "000000" ? "000001" : "000000");

        Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
        Assert.Contains("No device is waiting with that code", wrong.Body);
        Assert.False(await IsAuthorizedAsync(secret));
        var second = TokenOf(wrong.Body);
        Assert.NotEmpty(second);
        Assert.NotEqual(first, second);

        // The token that carried the wrong code is spent, right code or not.
        Assert.Contains("expired", (await ApproveAsync(first, code)).Body);
        Assert.False(await IsAuthorizedAsync(secret));

        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(second, code)).Status);
        Assert.True(await IsAuthorizedAsync(secret));
    }

    /// <summary>
    /// Three codes for one sign-in and no more, so a login cannot be used to try codes
    /// until one fits. The third wrong one ends it, and the right code after that is late.
    /// </summary>
    [Fact]
    public async Task The_third_wrong_code_ends_the_approval()
    {
        await TypeTheCodeAsync();
        var (code, secret) = await InitiateAsync();
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), startUrl: $"{_env.BaseUrl}/sso/oidc/start/qc");
        var wrong = code == "000000" ? "000001" : "000000";

        var first = await ApproveAsync(TokenOf(login.Body!), wrong);
        // Not a code at all counts as a try like any other.
        var second = await ApproveAsync(TokenOf(first.Body), "not a code");
        Assert.Contains("six digits", second.Body);
        var third = await ApproveAsync(TokenOf(second.Body), wrong);

        Assert.Equal(HttpStatusCode.BadRequest, third.Status);
        Assert.Contains("last try", third.Body);
        Assert.Empty(TokenOf(third.Body));
        Assert.False(await IsAuthorizedAsync(secret));
    }

    /// <summary>
    /// With the setting off the code is the one in the link, and a field posted along
    /// with the token does not replace it: otherwise the page that shows one code could
    /// be made to approve another.
    /// </summary>
    [Fact]
    public async Task A_posted_code_does_not_replace_the_one_from_the_link()
    {
        var (shown, shownSecret) = await InitiateAsync();
        var (other, otherSecret) = await InitiateAsync("another-tv");
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-tv", "tv-user", ["media"]), quickConnectCode: shown);

        var approval = await ApproveAsync(TokenOf(login.Body!), other);

        Assert.Equal(HttpStatusCode.OK, approval.Status);
        Assert.True(await IsAuthorizedAsync(shownSecret));
        Assert.False(await IsAuthorizedAsync(otherSecret));
    }

    [Fact]
    public async Task Without_the_setting_a_link_without_a_code_is_refused_before_any_sign_in()
    {
        using var client = JellyfinEnvironment.TrustingClient(followRedirects: false);

        using var response = await client.GetAsync($"{_env.BaseUrl}/sso/oidc/start/qc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no Quick Connect code", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Starts a Quick Connect request the way a TV app would.</summary>
    private async Task<(string Code, string Secret)> InitiateAsync(string deviceId = "e2e-tv-device")
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Add(
            "Authorization",
            $"MediaBrowser Client=\"e2e-tv\", Device=\"TV\", DeviceId=\"{deviceId}\", Version=\"1.0\"");

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
