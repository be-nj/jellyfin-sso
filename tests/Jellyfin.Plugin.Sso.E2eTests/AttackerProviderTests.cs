using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The identity provider as the attacker. Every other class in this suite trusts the fake
/// provider to behave like a provider; this one turns it, or a second provider an
/// administrator also configured, against the plugin: redirects where documents should be,
/// bodies that never end or never stop, a provider that sends the browser on to another
/// one, a provider naming a subject another provider owns, group and account names that
/// only look like the real ones.
///
/// Each case is checked the same way from the outside, against the real plugin in a real
/// Jellyfin: the login is refused with a 400 and plain words, nothing in the answer names
/// an address, no account or link is left behind, nothing is granted — and afterwards an
/// ordinary login through the same provider still works, so no failure was cached.
///
/// The validation rules the unit tests already pin (issuer, audience, azp, nonce, the
/// signing algorithms) are not repeated here. What is here needs real HTTP: whether a
/// redirect is followed, whether a timeout covers the body, what reaches which host, what
/// lands in the configuration file.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class AttackerProviderTests : IAsyncLifetime
{
    private const string DiscoveryPath = "/.well-known/openid-configuration";

    /// <summary>Where a hostile redirect points; nothing may ever arrive here.</summary>
    private const string CollectPath = "/collect";

    private readonly JellyfinEnvironment _env;
    private readonly List<FakeIdentityProvider> _strangers = [];

    public AttackerProviderTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var stranger in _strangers)
        {
            await stranger.DisposeAsync();
        }
    }

    // ─── Redirects, wrong shapes, slow and huge answers ─────────────────────────

    /// <summary>
    /// Every endpoint the plugin calls itself, answering with a redirect or with something
    /// that is not the document it should be. The redirects point at a plain-HTTP port of
    /// the provider's host — the shape of a redirect into the internal network — and the
    /// token endpoint's is a 307, which resends the body with the code and the verifier in
    /// it. The plugin must not follow any of them, must not answer 500, and must not
    /// remember the failure.
    /// </summary>
    [Theory]
    [InlineData("discovery-redirect")]
    [InlineData("keys-redirect")]
    [InlineData("token-redirect")]
    [InlineData("userinfo-redirect")]
    [InlineData("discovery-not-json")]
    [InlineData("keys-not-json")]
    [InlineData("token-not-json")]
    [InlineData("token-id-token-not-a-string")]
    [InlineData("userinfo-not-json")]
    public async Task A_misbehaving_endpoint_is_refused_and_leaves_nothing_behind(string how)
    {
        var idp = _env.Idp;
        var id = await AddProviderAsync(
            idp, "misbehaving", groupsFromUserInfo: how.StartsWith("userinfo", StringComparison.Ordinal));
        var collector = $"http://{TestCertificate.HostName}:{idp.PlainPort}{CollectPath}";

        Func<HttpContext, Task> RedirectTo(int status) => context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = collector;
            return Task.CompletedTask;
        };

        Func<HttpContext, Task> html = context =>
        {
            context.Response.ContentType = "text/html";
            return context.Response.WriteAsync("<html><body>Down for maintenance</body></html>");
        };

        var (path, behaviour) = how switch
        {
            "discovery-redirect" => (DiscoveryPath, RedirectTo(302)),
            "keys-redirect" => ("/jwks", RedirectTo(302)),
            "token-redirect" => ("/token", RedirectTo(307)),
            "userinfo-redirect" => ("/userinfo", RedirectTo(302)),
            "discovery-not-json" => (DiscoveryPath, html),
            "keys-not-json" => ("/jwks", html),
            "token-not-json" => ("/token", html),
            "token-id-token-not-a-string" => ("/token", (Func<HttpContext, Task>)(context =>
                context.Response.WriteAsJsonAsync(new
                {
                    access_token = "an-access-token",
                    token_type = "Bearer",
                    expires_in = 300,
                    id_token = 12345,
                }))),
            "userinfo-not-json" => ("/userinfo", html),
            _ => throw new ArgumentOutOfRangeException(nameof(how), how, null),
        };

        idp.Hostile[path] = behaviour;
        var username = $"misbehaving-{how}";

        var login = await _env.StartWebLoginAsync(
            new FakeUser($"sub-{how}", username, ["jellyfin-admins"]), providerId: id);

        await AssertRefusedAsync(login, username, idp);
        Assert.DoesNotContain(idp.Requests, r => r.EndsWith(CollectPath, StringComparison.Ordinal));

        idp.Hostile.Clear();
        await AssertProviderStillWorksAsync(id, $"sub-{how}", username);
    }

    /// <summary>
    /// A provider that accepts the connection and then sends its discovery document one
    /// byte a second. The plugin's client has a twenty-second timeout; this checks that it
    /// covers the body and not just the headers, that the visitor gets a refusal in words
    /// rather than a server fault, and that the next login fetches afresh. The timeout used
    /// to escape the login page as a 500 (#321).
    /// </summary>
    [Fact]
    public async Task A_discovery_document_that_trickles_in_is_given_up_on_and_refused_in_words()
    {
        var idp = _env.Idp;
        var id = await AddProviderAsync(idp, "trickle");

        idp.Hostile[DiscoveryPath] = async context =>
        {
            context.Response.ContentType = "application/json";
            try
            {
                await context.Response.WriteAsync("{\"issuer\":", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                for (var second = 0; second < 90; second++)
                {
                    await Task.Delay(1000, context.RequestAborted);
                    await context.Response.WriteAsync(" ", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The plugin hung up, which is the point.
            }
        };

        var logStart = (await _env.ServerLogsAsync()).Length;
        var clock = Stopwatch.StartNew();
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-trickle", "trickleuser", ["media"]), providerId: id);
        clock.Stop();

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(60),
            $"The login page was held for {clock.Elapsed.TotalSeconds:F0} s.");

        // What the server made of it, for the failure message: a refusal logs a warning,
        // a server fault logs the exception that escaped.
        var log = await _env.ServerLogsAsync();
        var escaped = string.Join(" | ", (log.Length > logStart ? log[logStart..] : string.Empty)
            .Split('\n')
            .Where(line => line.Contains("Exception", StringComparison.Ordinal)
                || line.Contains("[ERR]", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Take(3));
        Assert.True(
            login.CallbackStatus == HttpStatusCode.BadRequest,
            $"Expected a refusal (400), got {(int)login.CallbackStatus}. Server log: {escaped}");
        await AssertRefusedAsync(login, "trickleuser", idp);

        idp.Hostile.Clear();
        await AssertProviderStillWorksAsync(id, "sub-trickle", "trickleuser");
    }

    /// <summary>
    /// A userinfo answer of 32 MB: a valid document with one member of padding. A real
    /// one is a few hundred bytes. The plugin caps a provider's logo at 256 KB and counts
    /// it as it arrives; the documents it reads from the same provider had no cap at all,
    /// so the server buffered whatever arrived within the twenty-second timeout — over a
    /// local network, gigabytes (#322). They are capped at a mebibyte now.
    /// </summary>
    [Fact]
    public async Task A_provider_answer_of_many_megabytes_is_refused()
    {
        const int Megabytes = 32;
        var idp = _env.Idp;
        var id = await AddProviderAsync(idp, "bigbody", groupsFromUserInfo: true);

        idp.Hostile["/userinfo"] = async context =>
        {
            context.Response.ContentType = "application/json";
            var chunk = new string('a', 64 * 1024);
            try
            {
                await context.Response.WriteAsync(
                    "{\"sub\":\"sub-big-body\",\"preferred_username\":\"bigbody\","
                    + "\"groups\":[\"media\"],\"padding\":\"",
                    context.RequestAborted);
                for (var i = 0; i < Megabytes * 16; i++)
                {
                    await context.Response.WriteAsync(chunk, context.RequestAborted);
                }

                await context.Response.WriteAsync("\"}", context.RequestAborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The plugin stopped reading, which is what it should do.
            }
        };

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-big-body", "bigbody", ["media"]), providerId: id);

        await AssertRefusedAsync(login, "bigbody", idp);

        idp.Hostile.Clear();
        await AssertProviderStillWorksAsync(id, "sub-big-body", "bigbody");
    }

    /// <summary>
    /// The group names a login brings are remembered for the mapping page's suggestions,
    /// capped at 500 because "the names come from the provider, so the list needs an end".
    /// The cap counted names and not their length, so one login whose userinfo carried
    /// sixty-four names of 64 KB wrote four megabytes into the plugin's configuration
    /// file — rewritten on every later save, and sent whole to the configuration page
    /// (#323). A name over 256 characters is no longer remembered.
    /// </summary>
    [Fact]
    public async Task Group_names_of_many_kilobytes_do_not_grow_the_configuration_file_by_megabytes()
    {
        var idp = _env.Idp;
        var id = await AddProviderAsync(idp, "widegroups", groupsFromUserInfo: true);
        var before = (await _env.ConfigurationFileAsync()).Length;

        var groups = Enumerable.Range(0, 64)
            .Select(i => $"g{i:D2}-" + new string('x', 64 * 1024))
            .ToArray();
        idp.Hostile["/userinfo"] = context => context.Response.WriteAsJsonAsync(new
        {
            sub = "sub-wide-groups",
            preferred_username = "widegroups",
            groups,
        });

        await _env.StartWebLoginAsync(
            new FakeUser("sub-wide-groups", "widegroups", []), providerId: id);

        var grown = (await _env.ConfigurationFileAsync()).Length - before;
        Assert.True(
            grown < 256 * 1024,
            $"One login grew the configuration file by {grown / 1024} KB.");
    }

    // ─── Discovery that hides another host ──────────────────────────────────────

    /// <summary>
    /// A key set address written as <c>https://authority-host:port@other-host/…</c>. The
    /// part before the <c>@</c> is user information, the host is the one after it. A check
    /// that asks whether the address starts with the authority says yes; the plugin's own
    /// host check reads the host and says no. The threat model promises more than the
    /// refusal: endpoint validation precedes the key set fetch, so nothing is fetched from
    /// a host the document should not have named.
    /// </summary>
    [Fact]
    public async Task A_key_set_address_that_hides_another_host_is_refused_before_anything_is_fetched_there()
    {
        var idp = _env.Idp;
        var id = await AddProviderAsync(idp, "hiddenhost");

        const string ElsewherePath = "/keys-elsewhere";
        var document = idp.DiscoveryDocument();
        document["jwks_uri"] =
            $"https://{TestCertificate.HostName}:{idp.Port}@{idp.SecondHost}{ElsewherePath}";
        idp.Hostile[DiscoveryPath] = context => Results.Json(document).ExecuteAsync(context);
        idp.Hostile[ElsewherePath] = context => Results.Json(idp.KeySet()).ExecuteAsync(context);

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-hidden-host", "hiddenhostuser", ["media"]), providerId: id);

        await AssertRefusedAsync(login, "hiddenhostuser", idp);
        Assert.DoesNotContain(
            idp.Requests, r => r.EndsWith(ElsewherePath, StringComparison.Ordinal));

        idp.Hostile.Clear();
        await AssertProviderStillWorksAsync(id, "sub-hidden-host", "hiddenhostuser");
    }

    // ─── Two providers, one of them the attacker's ──────────────────────────────

    /// <summary>
    /// The mix-up attack. The visitor picks the attacker's provider; its authorize
    /// endpoint sends the browser on to the honest provider with the honest client id and
    /// callback, and with the state, nonce and PKCE challenge the plugin gave the attacker.
    /// The honest provider, where the visitor is already signed in, answers with a code for
    /// the visitor on its own callback. The state there belongs to the attacker's
    /// provider, so the plugin must stop before the code is redeemed anywhere.
    /// </summary>
    [Fact]
    public async Task A_code_that_comes_back_with_another_providers_state_is_never_redeemed()
    {
        var attacker = await StrangerAsync();
        var attackerId = await AddProviderAsync(attacker, "attacker", clientId: "attacker-client");
        attacker.Hostile["/authorize"] = SendOnToTheHonestProvider(Callback(JellyfinEnvironment.ProviderId));

        var issuedBefore = _env.Idp.TokensIssued;

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-mixup-victim", "mixupvictim", ["jellyfin-admins"]),
            providerId: attackerId);

        await AssertRefusedAsync(login, "mixupvictim", _env.Idp);
        Assert.Equal(issuedBefore, _env.Idp.TokensIssued);
        Assert.DoesNotContain(attacker.Requests, r => r.EndsWith("/token", StringComparison.Ordinal));
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());

        await AssertProviderStillWorksAsync(JellyfinEnvironment.ProviderId, "sub-mixup-victim", "mixupvictim");
    }

    /// <summary>
    /// The same attack where the honest provider accepts every callback of this server —
    /// a registration by pattern, <c>…/sso/oidc/callback/*</c>, which Keycloak offers and
    /// which covers each new provider card without another trip to its console. The code
    /// then comes back on the attacker's callback, with the attacker's state, and the
    /// plugin redeemed it at the attacker's token endpoint together with the PKCE verifier
    /// — everything the attacker needed to redeem it at the honest provider in turn (#325).
    ///
    /// RFC 9207 is the defence: the honest provider names itself in <c>iss</c> on the
    /// redirect, and a client compares that with the provider it sent the person to.
    /// </summary>
    [Fact]
    public async Task A_code_the_honest_provider_issued_is_not_sent_to_another_providers_token_endpoint()
    {
        var attacker = await StrangerAsync();
        var attackerId = await AddProviderAsync(attacker, "attacker", clientId: "attacker-client");

        _env.Idp.RegisteredRedirectUris.Add(Callback(attackerId));
        _env.Idp.AnnounceIssuerInAuthorizationResponse = true;
        attacker.Hostile["/authorize"] = SendOnToTheHonestProvider(Callback(attackerId));

        var stolen = new ConcurrentQueue<(string Code, string Verifier)>();
        attacker.Hostile["/token"] = async context =>
        {
            var form = await context.Request.ReadFormAsync();
            stolen.Enqueue((form["code"].ToString(), form["code_verifier"].ToString()));
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant" });
        };

        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-mixup-iss", "mixupiss", ["jellyfin-admins"]),
            providerId: attackerId);
        Assert.False(login.Succeeded, login.Body);

        // What the attacker does with what it was handed: redeem it where it came from.
        var redeemed = false;
        if (stolen.TryPeek(out var loot))
        {
            using var client = JellyfinEnvironment.TrustingClient();
            using var response = await client.PostAsync(
                $"{_env.Idp.BrowserAuthority}/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = loot.Code,
                    ["code_verifier"] = loot.Verifier,
                    ["redirect_uri"] = Callback(attackerId),
                    ["client_id"] = JellyfinEnvironment.ClientId,
                }));
            redeemed = response.IsSuccessStatusCode
                && (await response.Content.ReadAsStringAsync()).Contains("id_token", StringComparison.Ordinal);
        }

        Assert.True(
            stolen.IsEmpty,
            "The plugin sent the honest provider's code and PKCE verifier to the attacker's "
            + "token endpoint" + (redeemed ? ", and the attacker redeemed them for the visitor's tokens." : "."));
    }

    /// <summary>
    /// A second provider naming the subject the first one uses, and the name of the account
    /// that subject owns, with no groups — so that reaching the account would also strip
    /// it of its administrator flag. Accounts are keyed by provider and subject together;
    /// the attacker gets neither the account nor a link, and its back-channel logouts end
    /// nothing of the first provider's — not at its own endpoint, where it is trusted, and
    /// not at the first provider's, where it is not. The first provider's own logout still
    /// ends the session, so the two that did not were not vacuous.
    /// </summary>
    [Fact]
    public async Task An_attacker_provider_naming_another_providers_subject_reaches_neither_the_account_nor_its_sessions()
    {
        const string Sub = "sub-shared";
        var honest = await _env.StartWebLoginAsync(
            new FakeUser(Sub, "shareduser", ["jellyfin-admins"]),
            providerId: JellyfinEnvironment.ProviderId);
        Assert.True(honest.Succeeded, honest.Body);
        var session = await SessionTokenAsync("shareduser", honest.Secret!);

        var attacker = await StrangerAsync();
        var attackerId = await AddProviderAsync(attacker, "attacker", clientId: "attacker-client");
        attacker.NextUser = new FakeUser(Sub, "shareduser", []);

        var takeover = await _env.StartWebLoginAsync(attacker.NextUser, providerId: attackerId);

        Assert.False(takeover.Succeeded, takeover.Body);
        Assert.Equal(HttpStatusCode.BadRequest, takeover.CallbackStatus);
        Assert.True((await _env.UserPolicyAsync("shareduser"))["IsAdministrator"]!.GetValue<bool>());
        var link = Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
        Assert.Equal(JellyfinEnvironment.ProviderId, link!["ProviderId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(session));

        using var client = JellyfinEnvironment.TrustingClient();

        using var atItsOwn = await client.PostAsync(
            BackChannel(attackerId), LogoutForm(attacker.MintLogoutToken(Sub, "attacker-client")));
        Assert.Equal(HttpStatusCode.OK, atItsOwn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(session));

        using var atTheHonest = await client.PostAsync(
            BackChannel(JellyfinEnvironment.ProviderId),
            LogoutForm(attacker.MintLogoutToken(Sub, JellyfinEnvironment.ClientId)));
        Assert.Equal(HttpStatusCode.BadRequest, atTheHonest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIAsync(session));

        using var genuine = await client.PostAsync(
            BackChannel(JellyfinEnvironment.ProviderId),
            LogoutForm(_env.Idp.MintLogoutToken(Sub, JellyfinEnvironment.ClientId)));
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIAsync(session));
    }

    // ─── Names that only look right ─────────────────────────────────────────────

    /// <summary>
    /// Group names a provider can send that read like the administrator group and are not
    /// it: other case, padding, a Unicode hyphen, an invisible character, fullwidth
    /// letters, a Cyrillic letter, a path. Matching is exact, so none of them may grant
    /// anything — and the real name, sent afterwards, does, so the check is not vacuous.
    /// </summary>
    [Fact]
    public async Task Group_names_that_only_look_like_the_admin_group_grant_nothing()
    {
        string[] lookalikes =
        [
            "Jellyfin-Admins",
            "JELLYFIN-ADMINS",
            " jellyfin-admins",
            "jellyfin-admins ",
            "jellyfin‐admins",
            "jellyfin-admins​",
            "jellyfin-admins͏",
            "ｊellyfin-admins",
            "jellyfin-adminѕ",
            "/jellyfin-admins",
        ];

        var login = await _env.StartWebLoginAsync(new FakeUser("sub-lookalike", "lookalike", lookalikes));
        Assert.True(login.Succeeded, login.Body);

        var policy = await _env.UserPolicyAsync("lookalike");
        Assert.False(policy["IsAdministrator"]!.GetValue<bool>());
        Assert.False(policy["EnableAllFolders"]!.GetValue<bool>());
        Assert.Empty(policy["EnabledFolders"]?.AsArray() ?? []);

        var real = await _env.StartWebLoginAsync(new FakeUser("sub-lookalike", "lookalike", ["jellyfin-admins"]));
        Assert.True(real.Succeeded, real.Body);
        Assert.True((await _env.UserPolicyAsync("lookalike"))["IsAdministrator"]!.GetValue<bool>());
    }

    /// <summary>
    /// A provider naming somebody the name of the server's administrator, in a form that
    /// reads the same: other case, or with a combining grapheme joiner — an invisible mark
    /// Jellyfin's own name rule admits as a letter. The plugin trims names for exactly this
    /// reason ("a second account whose name reads the same in every list it appears in");
    /// it created one this way until invisible characters were dropped from names (#324).
    /// </summary>
    [Theory]
    [InlineData("other-case")]
    [InlineData("invisible-mark")]
    public async Task A_name_that_reads_like_the_administrators_does_not_become_a_second_account(string how)
    {
        var name = how switch
        {
            "other-case" => _env.AdminUserName.ToUpperInvariant(),
            "invisible-mark" => _env.AdminUserName + "͏",
            _ => throw new ArgumentOutOfRangeException(nameof(how), how, null),
        };

        var login = await _env.StartWebLoginAsync(new FakeUser($"sub-reads-like-{how}", name, ["media"]));

        var readingTheSame = (await _env.ListUsersAsync())
            .Select(u => u!["Name"]!.GetValue<string>())
            .Where(n => string.Equals(Skeleton(n), Skeleton(_env.AdminUserName), StringComparison.Ordinal))
            .ToList();
        Assert.True(
            readingTheSame.Count == 1,
            $"Accounts that read as '{_env.AdminUserName}': "
            + string.Join(", ", readingTheSame.Select(Escaped)) + $" (login: {login.CallbackStatus})");
        Assert.False(login.Succeeded, login.Body);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private string Callback(string providerId) => $"{_env.BaseUrl}/sso/oidc/callback/{providerId}";

    private string BackChannel(string providerId) => $"{_env.BaseUrl}/sso/oidc/backchannel-logout/{providerId}";

    private static FormUrlEncodedContent LogoutForm(string token)
        => new(new Dictionary<string, string> { ["logout_token"] = token });

    /// <summary>A second provider, run by somebody else, that an administrator also configured.</summary>
    private async Task<FakeIdentityProvider> StrangerAsync()
    {
        var stranger = await FakeIdentityProvider.StartAsync(_env.Certificate);
        _strangers.Add(stranger);
        return stranger;
    }

    /// <summary>
    /// Adds an enabled provider card for <paramref name="idp"/> under a fresh id. Fresh,
    /// because the plugin caches a discovery document per provider id for half an hour,
    /// and a test about what a document says needs the plugin to fetch it.
    /// </summary>
    private async Task<string> AddProviderAsync(
        FakeIdentityProvider idp, string name, string? clientId = null, bool groupsFromUserInfo = false)
    {
        var id = $"{name}-{Guid.NewGuid():N}"[..Math.Min(64, name.Length + 33)];
        await _env.PatchConfigAsync(config => config["Providers"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = id,
            ["Name"] = name,
            ["Enabled"] = true,
            ["Authority"] = idp.ContainerAuthority,
            ["ClientId"] = clientId ?? JellyfinEnvironment.ClientId,
            ["Scopes"] = new JsonArray { "openid", "profile", "email", "groups" },
            ["GroupsClaim"] = "groups",
            ["GroupsFromUserInfo"] = groupsFromUserInfo,
        }));
        idp.RegisteredRedirectUris.Add(Callback(id));
        return id;
    }

    /// <summary>
    /// The attacker's authorize endpoint: the browser goes on to the honest provider with
    /// the honest client id, the callback given here, and what the plugin sent the
    /// attacker — state, nonce, PKCE challenge.
    /// </summary>
    private Func<HttpContext, Task> SendOnToTheHonestProvider(string callback) => context =>
    {
        var query = context.Request.Query;
        static string E(string? value) => Uri.EscapeDataString(value ?? string.Empty);

        context.Response.Redirect(
            $"{_env.Idp.AuthorizeEndpoint}?client_id={E(JellyfinEnvironment.ClientId)}"
            + $"&redirect_uri={E(callback)}&response_type=code&scope=openid"
            + $"&state={E(query["state"])}&nonce={E(query["nonce"])}"
            + $"&code_challenge={E(query["code_challenge"])}&code_challenge_method=S256");
        return Task.CompletedTask;
    };

    /// <summary>
    /// A refusal as the visitor sees it: a 400, in words that name no address and no
    /// exception, and no account for the name the provider sent.
    /// </summary>
    private async Task AssertRefusedAsync(
        JellyfinEnvironment.SsoLoginResult login, string username, FakeIdentityProvider idp)
    {
        var body = login.Body ?? string.Empty;
        Assert.False(login.Succeeded, body);
        Assert.True(
            login.CallbackStatus == HttpStatusCode.BadRequest,
            $"Expected a refusal (400), got {(int)login.CallbackStatus}: {body}");
        Assert.DoesNotContain(TestCertificate.HostName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(idp.Port.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.Null(await _env.FindUserAsync(username));
    }

    /// <summary>An ordinary login through the same provider, once it behaves again.</summary>
    private async Task AssertProviderStillWorksAsync(string providerId, string sub, string username)
    {
        var after = await _env.StartWebLoginAsync(new FakeUser(sub, username, ["media"]), providerId: providerId);
        Assert.True(after.Succeeded, $"After the provider recovered: {(int)after.CallbackStatus} {after.Body}");
    }

    private async Task<string> SessionTokenAsync(string username, string secret)
    {
        using var response = await _env.AuthenticateAsync(username, secret);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
    }

    private async Task<HttpStatusCode> WhoAmIAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_env.BaseUrl}/Users/Me");
        request.Headers.Authorization = JellyfinEnvironment.TokenHeader(token);

        using var client = JellyfinEnvironment.TrustingClient();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// A name as a reader sees it: case folded, marks and format characters dropped.
    /// Two names with the same skeleton read the same in a list.
    /// </summary>
    private static string Skeleton(string name)
        => new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) is not (
                UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format))
            .ToArray())
            .ToLowerInvariant();

    private static string Escaped(string name)
        => string.Concat(name.Select(c => c < 0x80 ? c.ToString() : $"\\u{(int)c:X4}"));
}
