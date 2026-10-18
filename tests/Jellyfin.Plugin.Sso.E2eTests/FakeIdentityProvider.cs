using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
/// It serves HTTPS with a self-signed certificate covering both names, because the
/// plugin's discovery client refuses plain HTTP for anything but loopback. The
/// certificate is written to <see cref="CertificatePath"/> for the server container to
/// trust.
/// </summary>
public sealed class FakeIdentityProvider : IAsyncDisposable
{
    private const string KeyId = "fake-idp-key";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _signingKey;
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private readonly ConcurrentDictionary<string, FakeUser> _accessTokens = new();

    private sealed record PendingCode(
        string ClientId, string RedirectUri, string Nonce, string CodeChallenge, FakeUser User);

    private FakeIdentityProvider(int port, string certificateDirectory)
    {
        Port = port;
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };

        var certificate = CreateCertificate();
        CertificatePath = Path.Combine(certificateDirectory, "fake-idp.crt");
        Directory.CreateDirectory(certificateDirectory);
        File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenAnyIP(port, listen => listen.UseHttps(certificate)));
        _app = builder.Build();

        MapEndpoints(_app);
    }

    /// <summary>PEM file holding the self-signed certificate, to be trusted by the server.</summary>
    public string CertificatePath { get; } = string.Empty;

    public int Port { get; }

    /// <summary>
    /// How the Jellyfin container reaches this provider; also the token issuer. The name is
    /// mapped to the Docker host gateway when the container is created.
    /// </summary>
    public string ContainerAuthority => $"https://host.docker.internal:{Port}";

    /// <summary>How the test process and the browser reach this provider.</summary>
    public string BrowserAuthority => $"https://localhost:{Port}";

    /// <summary>The identity handed out by the next authorize call.</summary>
    public FakeUser NextUser { get; set; } = new("sub-default", "default-user", []);

    /// <summary>Signals whether the provider ever issued a token, useful in negative tests.</summary>
    public int TokensIssued { get; private set; }

    /// <summary>How often the end-session endpoint was reached.</summary>
    public int SessionsEnded { get; private set; }

    public static async Task<FakeIdentityProvider> StartAsync(string certificateDirectory)
    {
        var idp = new FakeIdentityProvider(FreePort(), certificateDirectory);
        await idp._app.StartAsync();
        return idp;
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = ContainerAuthority,
            // The browser follows this one, so it must be host-resolvable.
            ["authorization_endpoint"] = $"{BrowserAuthority}/authorize",
            // Jellyfin calls these server-side from inside the container.
            ["token_endpoint"] = $"{ContainerAuthority}/token",
            ["userinfo_endpoint"] = $"{ContainerAuthority}/userinfo",
            ["jwks_uri"] = $"{ContainerAuthority}/jwks",
            // Browser-facing like authorize: the user's browser is sent here to sign out.
            ["end_session_endpoint"] = $"{BrowserAuthority}/endsession",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["scopes_supported"] = new[] { "openid", "profile", "email", "groups" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["grant_types_supported"] = new[] { "authorization_code" },
        }));

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

            var code = Guid.NewGuid().ToString("N");
            _codes[code] = new PendingCode(clientId, redirectUri, nonce, codeChallenge, NextUser);

            var separator = redirectUri.Contains('?') ? '&' : '?';
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
            SessionsEnded++;
            return Results.Redirect(postLogoutRedirectUri ?? "/");
        });

        app.MapGet("/userinfo", (HttpRequest request) =>
        {
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
                ["sub"] = user.Sub,
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

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: ContainerAuthority,
            audience: pending.ClientId,
            claims: claims,
            notBefore: now.AddSeconds(-5),
            expires: now.AddMinutes(5),
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Challenge(string verifier)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=fake-idp", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("host.docker.internal");
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Kestrel needs the private key to stay attached to the instance it serves.
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx), password: null);
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _rsa.Dispose();
    }
}
