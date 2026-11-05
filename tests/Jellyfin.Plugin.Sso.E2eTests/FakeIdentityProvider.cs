using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>The identity the fake provider will hand out on the next login.</summary>
public sealed record FakeUser(string Sub, string PreferredUsername, string[] Groups);

/// <summary>
/// A real OIDC provider, small enough to control from a test: discovery, JWKS,
/// authorization code + PKCE, token and userinfo. The authorize endpoint has no login
/// form — it immediately redirects back with a code for <see cref="NextUser"/>, which
/// keeps the flow deterministic whether it is driven by HttpClient or by a browser.
///
/// It is reachable under two names: the Jellyfin container resolves
/// <see cref="ContainerAuthority"/> (host.docker.internal), while the test process
/// and the browser use <see cref="BrowserAuthority"/> (localhost). The issuer is the
/// container-facing one, because that is what Jellyfin validates tokens against.
///
/// It serves HTTPS with the suite's shared certificate, because the plugin's discovery
/// client refuses plain HTTP for anything but loopback.
/// </summary>
public sealed class FakeIdentityProvider : IAsyncDisposable
{
    private const string KeyId = "fake-idp-key";

    private RSA _rsa = RSA.Create(2048);
    private RsaSecurityKey _signingKey;

    /// <summary>
    /// A key nobody publishes. Set this and the next ID token is signed by a stranger,
    /// which is what a token the plugin must refuse looks like.
    /// </summary>
    private readonly RsaSecurityKey _strangersKey =
        new(RSA.Create(2048)) { KeyId = "a-stranger" };

    /// <summary>Signs the next token with a key that is not in the published key set.</summary>
    public bool SignWithAStrangersKey { get; set; }

    /// <summary>
    /// The redirect URIs this provider has been told about, the way a real one is
    /// configured. Both the authorize request and the post-logout redirect are checked
    /// against it, because Keycloak and Authentik check theirs — and because not checking
    /// is what let a doubled sub-path in the post-logout URL pass its own test for weeks.
    /// Empty means the check is off, which keeps the tests that predate it working.
    /// </summary>
    public HashSet<string> RegisteredRedirectUris { get; } = new(StringComparer.Ordinal);

    /// <summary>How often a request named a redirect URI this provider does not know.</summary>
    public int UnregisteredRedirectsRefused { get; private set; }
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private readonly ConcurrentDictionary<string, FakeUser> _accessTokens = new();

    private sealed record PendingCode(
        string ClientId,
        string RedirectUri,
        string Nonce,
        string CodeChallenge,
        FakeUser User,
        string? PictureUrl);

    /// <summary>
    /// Replaces the signing key and publishes the new one, the way a provider rotating
    /// its keys does. Tokens signed before this are then signed by a key the published
    /// set no longer carries.
    ///
    /// A new key id every time, which a real provider also gives: the first version added
    /// a fixed "-rotated" suffix, so a second rotation in one test run published different
    /// key material under a key id the plugin had already cached. That is not a rotation —
    /// it is the shape of a forgery, the key is found and the signature does not check out
    /// — and it left the provider unusable for every test that followed.
    /// </summary>
    public void RotateSigningKey()
    {
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa)
        {
            KeyId = $"{KeyId}-{Interlocked.Increment(ref _rotations)}",
        };
    }

    private int _rotations;

    private FakeIdentityProvider(int port, TestCertificate certificate)
    {
        Port = port;
        PlainPort = TestCertificate.FreePort();
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(port, listen => listen.UseHttps(certificate.Certificate));

            // The same endpoints again without TLS. Nothing in the suite uses this: the
            // plugin's discovery client refuses plain HTTP off loopback, which is the
            // point. It exists for the manual stack, where the browser that has to follow
            // the authorize redirect belongs to a phone or a television box and has no
            // way to be told about a certificate signed by nobody.
            kestrel.ListenAnyIP(PlainPort);
        });
        _app = builder.Build();

        MapEndpoints(_app);
    }

    public int Port { get; }

    /// <summary>
    /// How the Jellyfin container reaches this provider; also the token issuer. The name is
    /// mapped to the Docker host gateway when the container is created.
    /// </summary>
    public string ContainerAuthority => $"https://{TestCertificate.HostName}:{Port}";

    /// <summary>How the test process and the browser reach this provider.</summary>
    public string BrowserAuthority => $"https://localhost:{Port}";

    /// <summary>The port serving the same endpoints without TLS; see the constructor.</summary>
    public int PlainPort { get; }

    /// <summary>
    /// Where the discovery document sends a browser to authorize. HTTPS, as a provider's
    /// is — unless E2E_IDP_PLAIN_AUTHORIZE is set, which the manual stack does so that a
    /// device with no way to trust this certificate can still follow the redirect. Only
    /// this endpoint moves: the token, userinfo and JWKS endpoints the plugin itself calls
    /// stay on TLS either way.
    /// </summary>
    public string AuthorizeEndpoint =>
        Environment.GetEnvironmentVariable("E2E_IDP_PLAIN_AUTHORIZE") == "1"
            ? $"http://localhost:{PlainPort}/authorize"
            : $"{BrowserAuthority}/authorize";

    /// <summary>The identity handed out by the next authorize call.</summary>
    public FakeUser NextUser { get; set; } = new("sub-default", "default-user", []);

    /// <summary>The smallest valid PNG, so the logo path has real bytes to carry.</summary>
    internal static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>The same in GIF, so a test can tell the two brand images apart.</summary>
    internal static byte[] OnePixelGif() => Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    /// <summary>
    /// Whether the favicon is published. Turning it off is how a test watches the plugin
    /// fall back to the wide brand logo, which is all some providers offer.
    /// </summary>
    public bool ServeFavicon { get; set; } = true;

    /// <summary>
    /// The picture claim handed out with the next identity, or null for a provider that
    /// publishes none. <see cref="AvatarUrl"/> is the one it actually serves.
    /// </summary>
    public string? NextPictureUrl { get; set; }

    /// <summary>A picture this provider serves, reachable from the Jellyfin container.</summary>
    public string AvatarUrl => $"{ContainerAuthority}/static/avatar.png";

    /// <summary>How often the avatar was fetched, so a test can see a refetch.</summary>
    public int AvatarsServed { get; private set; }

    /// <summary>
    /// When true the userinfo endpoint answers with a server error, the way a provider
    /// that has one in its discovery document but cannot serve it does.
    /// </summary>
    public bool UserInfoFails { get; set; }

    /// <summary>
    /// When true the discovery document puts the end-session endpoint on another host,
    /// which no real provider does and a tampered document would. A logout sends the
    /// user's ID token there, so the plugin has to refuse the document.
    /// </summary>
    public bool EndSessionElsewhere { get; set; }

    /// <summary>
    /// When true the userinfo endpoint answers about a different subject than the ID token
    /// named. OIDC Core forbids using such a response, because what is read from it
    /// decides a user's groups.
    /// </summary>
    public bool UserInfoAnswersAboutSomebodyElse { get; set; }

    /// <summary>
    /// When set, the next authorize call reports this OAuth error instead of issuing a
    /// code — what a provider does when the user cancels or the client is misconfigured.
    /// </summary>
    public string? NextAuthorizeError { get; set; }

    /// <summary>Signals whether the provider ever issued a token, useful in negative tests.</summary>
    public int TokensIssued { get; private set; }

    /// <summary>How often the end-session endpoint was reached.</summary>
    public int SessionsEnded { get; private set; }

    /// <summary>
    /// Puts every knob back to the behaviour of an ordinary provider.
    ///
    /// This provider is one object shared by every test in the collection, and each knob is
    /// a way for one test to make it misbehave. A test that sets one and then fails before
    /// its own cleanup leaves it set for everything that runs afterwards — and the failure
    /// it causes there looks like a defect in the plugin rather than a test leaking state.
    /// One rotated signing key did exactly that earlier today: six tests in another class
    /// failed and the cause was two files away.
    ///
    /// So the reset lives here, next to the knobs, and a knob added without a line here is
    /// a knob that leaks. Called from <see cref="JellyfinEnvironment.ResetAsync"/>, which
    /// every test class already runs before each test.
    /// </summary>
    public void ResetBehaviour()
    {
        NextUser = new FakeUser("sub-default", "default-user", []);
        ServeFavicon = true;
        NextPictureUrl = null;
        UserInfoFails = false;
        EndSessionElsewhere = false;
        UserInfoAnswersAboutSomebodyElse = false;
        NextAuthorizeError = null;
    }

    public static async Task<FakeIdentityProvider> StartAsync(TestCertificate certificate)
    {
        var idp = new FakeIdentityProvider(TestCertificate.FreePort(), certificate);
        await idp._app.StartAsync();
        return idp;
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = ContainerAuthority,
            // The browser follows this one, so it must be host-resolvable.
            ["authorization_endpoint"] = AuthorizeEndpoint,
            // Jellyfin calls these server-side from inside the container.
            ["token_endpoint"] = $"{ContainerAuthority}/token",
            ["userinfo_endpoint"] = $"{ContainerAuthority}/userinfo",
            ["jwks_uri"] = $"{ContainerAuthority}/jwks",
            // On the authority's host, as every real provider's is: the plugin sends a
            // user's ID token here as a logout hint and refuses a document that moves it
            // elsewhere. EndSessionElsewhere is how a test watches it refuse.
            ["end_session_endpoint"] = EndSessionElsewhere
                ? $"https://localhost:{Port}/endsession"
                : $"{ContainerAuthority}/endsession",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["scopes_supported"] = new[] { "openid", "profile", "email", "groups" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["grant_types_supported"] = new[] { "authorization_code" },
        }));

        // A branded provider: the plugin should put this on the login button rather than
        // its own stock mark. Both brand images are published, as Authentik publishes
        // both, and they carry different content types so a test can say which was taken.
        app.MapGet("/api/v3/core/brands/current/", () => Results.Json(new Dictionary<string, object>
        {
            ["branding_title"] = "Fake IdP",
            ["branding_favicon"] = "/static/brand-favicon.png",
            ["branding_logo"] = "/static/brand-logo.gif",
        }));

        app.MapGet("/static/brand-favicon.png", () => ServeFavicon
            ? Results.File(OnePixelPng(), "image/png")
            : Results.NotFound());

        app.MapGet("/static/brand-logo.gif", () => Results.File(OnePixelGif(), "image/gif"));

        app.MapGet("/static/avatar.png", () =>
        {
            AvatarsServed++;
            return Results.File(OnePixelPng(), "image/png");
        });

        app.MapGet("/jwks", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_signingKey);
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;
            return Results.Json(new { keys = new[] { jwk } });
        });

        app.MapGet("/authorize", (
            [FromQuery(Name = "client_id")] string clientId,
            [FromQuery(Name = "redirect_uri")] string redirectUri,
            [FromQuery] string state,
            [FromQuery] string nonce,
            [FromQuery(Name = "code_challenge")] string codeChallenge,
            [FromQuery(Name = "code_challenge_method")] string codeChallengeMethod) =>
        {
            if (codeChallengeMethod != "S256")
            {
                return Results.BadRequest("only S256 is supported");
            }

            // Checked here and not only at the token endpoint. A real provider refuses an
            // unregistered redirect before it shows anybody a login form, which is the
            // difference between a misconfiguration the operator sees at once and one
            // that surfaces as a broken callback.
            if (!Registered(redirectUri))
            {
                UnregisteredRedirectsRefused++;
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    detail = $"redirect_uri is not registered: {redirectUri}",
                });
            }

            var separator = redirectUri.Contains('?') ? '&' : '?';

            if (NextAuthorizeError is { } error)
            {
                NextAuthorizeError = null;
                return Results.Redirect(
                    $"{redirectUri}{separator}error={Uri.EscapeDataString(error)}"
                    + $"&state={Uri.EscapeDataString(state)}");
            }

            var code = Guid.NewGuid().ToString("N");
            _codes[code] = new PendingCode(
                clientId, redirectUri, nonce, codeChallenge, NextUser, NextPictureUrl);
            return Results.Redirect(
                $"{redirectUri}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");
        });

        app.MapPost("/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();

            if (form["grant_type"] != "authorization_code")
            {
                return Results.BadRequest(new { error = "unsupported_grant_type" });
            }

            if (!_codes.TryRemove(form["code"].ToString(), out var pending))
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            // PKCE: the verifier must hash to the challenge sent at authorize time.
            var verifier = form["code_verifier"].ToString();
            if (Challenge(verifier) != pending.CodeChallenge)
            {
                return Results.BadRequest(new { error = "invalid_grant", detail = "pkce mismatch" });
            }

            if (form["redirect_uri"].ToString() != pending.RedirectUri)
            {
                return Results.BadRequest(new { error = "invalid_grant", detail = "redirect_uri mismatch" });
            }

            var accessToken = Guid.NewGuid().ToString("N");
            _accessTokens[accessToken] = pending.User;
            TokensIssued++;

            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = accessToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["id_token"] = MintIdToken(pending),
            });
        });

        app.MapGet("/endsession", (
            [FromQuery(Name = "post_logout_redirect_uri")] string? postLogoutRedirectUri) =>
        {
            // A real provider refuses a post-logout redirect it has not been given, and
            // aborts the logout. This used to redirect wherever it was told, which is why
            // a URL carrying the sub-path twice passed the test that asserted it.
            if (postLogoutRedirectUri is { Length: > 0 } && !Registered(postLogoutRedirectUri))
            {
                UnregisteredRedirectsRefused++;
                return Results.BadRequest(new
                {
                    error = "invalid_request",
                    detail = $"post_logout_redirect_uri is not registered: {postLogoutRedirectUri}",
                });
            }

            SessionsEnded++;
            return Results.Redirect(postLogoutRedirectUri ?? "/");
        });

        app.MapGet("/userinfo", (HttpRequest request) =>
        {
            if (UserInfoFails)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            var header = request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..]
                : string.Empty;

            if (!_accessTokens.TryGetValue(token, out var user))
            {
                return Results.Unauthorized();
            }

            return Results.Json(new Dictionary<string, object>
            {
                ["sub"] = UserInfoAnswersAboutSomebodyElse ? "sub-somebody-else" : user.Sub,
                ["preferred_username"] = user.PreferredUsername,
                ["groups"] = user.Groups,
            });
        });
    }

    private string MintIdToken(PendingCode pending)
    {
        var claims = new List<Claim>
        {
            new("sub", pending.User.Sub),
            new("preferred_username", pending.User.PreferredUsername),
            new("nonce", pending.Nonce),
        };
        claims.AddRange(pending.User.Groups.Select(g => new Claim("groups", g)));

        if (pending.PictureUrl is { Length: > 0 } picture)
        {
            claims.Add(new Claim("picture", picture));
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: ContainerAuthority,
            audience: pending.ClientId,
            claims: claims,
            notBefore: now.AddSeconds(-5),
            expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                SignWithAStrangersKey ? _strangersKey : _signingKey,
                SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// A back-channel logout token for a subject, signed with the same key the ID tokens
    /// are. This is what a provider posts to the plugin when a session ends on its side.
    /// </summary>
    public string MintLogoutToken(string sub, string clientId, string? sid = "session-1")
    {
        var payload = new JwtPayload
        {
            { "iss", ContainerAuthority },
            { "aud", clientId },
            { "sub", sub },
            { "jti", Guid.NewGuid().ToString("N") },
            { "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            {
                "events",
                new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"]
                        = new Dictionary<string, object>(),
                }
            },
        };

        if (sid is not null)
        {
            payload.Add("sid", sid);
        }

        var token = new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256)),
            payload);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Whether this provider has been told about an address. No registrations at all means
    /// anything goes, so a test that does not care about this does not have to say so.
    /// </summary>
    private bool Registered(string uri)
        => RegisteredRedirectUris.Count == 0 || RegisteredRedirectUris.Contains(uri);

    private static string Challenge(string verifier)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _rsa.Dispose();
    }
}
