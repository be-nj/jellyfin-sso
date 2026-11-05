using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Microsoft.IdentityModel.Tokens;
using Jellyfin.Plugin.Sso.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// Creates and caches per-provider OIDC discovery documents.
/// </summary>
public sealed class OidcClientFactory : IDisposable
{
    /// <summary>
    /// How long a discovery document — and with it the provider's signing keys — is
    /// reused. A provider that rotates its keys would otherwise fail every login until
    /// Jellyfin restarts.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private sealed record CacheEntry(DiscoveryDocumentResponse Document, DateTimeOffset Expiry);

    private readonly HttpClient _http;
    private readonly ILogger<OidcClientFactory> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public OidcClientFactory(ILogger<OidcClientFactory> logger)
    {
        // A provider that accepts the connection and then says nothing must not hold a
        // login page open for the default hundred seconds.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.Sso", "1.0"));
        _logger = logger;
    }

    public async Task<DiscoveryDocumentResponse> GetDiscoveryDocumentAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        // Keyed by authority as well as id: an administrator who corrects the issuer URL
        // would otherwise keep talking to the old provider until Jellyfin restarts.
        var cacheKey = $"{provider.Id}|{provider.Authority}";

        if (_cache.TryGetValue(cacheKey, out var cached)
            && !cached.Document.IsError
            && cached.Expiry > DateTimeOffset.UtcNow)
        {
            return cached.Document;
        }

        _logger.LogInformation("Fetching OIDC discovery for provider {Name} ({Authority})",
            provider.Name, provider.Authority);

        // Authentik publishes most of its endpoints on a shared path
        // (/application/o/token/, /application/o/authorize/ and so on) while the issuer is
        // per-application (/application/o/<slug>/). The library's endpoint validation
        // demands that every endpoint sit under the authority URL, which that layout fails,
        // and both checks were switched off to get past it.
        //
        // That gave away far more than the paths it was about. Without endpoint validation
        // the document may name a jwks_uri, a token endpoint and an end-session endpoint on
        // any host at all; without issuer validation its issuer need not match the
        // configured authority, and that same unchecked value is then used as the expected
        // issuer when tokens are validated, which makes the check circular. Anyone able to
        // serve the discovery document at the configured address could point the signing
        // keys at themselves and mint tokens for any user.
        //
        // The issuer check is back on, because every provider this is tested against names
        // an issuer that matches. Paths stay unvalidated, because that is the part Authentik
        // genuinely does differently — and the host is checked instead, below, which is
        // what the concern was actually about.
        using var request = new DiscoveryDocumentRequest
        {
            Address = provider.Authority,
            Policy = new DiscoveryPolicy
            {
                ValidateEndpoints = false,
            },
        };

        var doc = await _http.GetDiscoveryDocumentAsync(request, ct).ConfigureAwait(false);

        if (doc.IsError)
            throw new InvalidOperationException(
                $"OIDC discovery failed for {provider.Authority}: {doc.Error}");

        RequireEndpointsOnTheAuthoritysHost(
            provider.Authority,
            ("jwks_uri", doc.JwksUri),
            ("token_endpoint", doc.TokenEndpoint),
            ("userinfo_endpoint", doc.UserInfoEndpoint),
            ("end_session_endpoint", doc.EndSessionEndpoint));

        _cache[cacheKey] = new CacheEntry(doc, DateTimeOffset.UtcNow.Add(CacheTtl));
        return doc;
    }

    /// <summary>
    /// Refuses a discovery document that moves an endpoint to another host.
    ///
    /// The paths are the provider's business — Authentik's differ from its issuer by
    /// design — but the host is not. These four are where the client secret and the
    /// authorization code are sent, where the keys that decide whether a token is genuine
    /// come from, and where a user's ID token is sent as a logout hint. A document that
    /// names another host for any of them is not a layout quirk.
    /// </summary>
    internal static void RequireEndpointsOnTheAuthoritysHost(
        string? authorityUrl, params (string Name, string? Value)[] endpoints)
    {
        if (!Uri.TryCreate(authorityUrl, UriKind.Absolute, out var authority))
        {
            throw new InvalidOperationException(
                $"The authority '{authorityUrl}' is not an absolute URL.");
        }

        foreach (var (name, value) in endpoints)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
                || !string.Equals(endpoint.Host, authority.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The discovery document for {authorityUrl} puts its {name} on another "
                    + "host. Refusing it: that endpoint decides whether a login is genuine.");
            }
        }
    }

    /// <summary>
    /// Exchanges an authorization code + PKCE verifier for tokens.
    /// </summary>
    public async Task<TokenResponse> ExchangeCodeAsync(
        ProviderConfig provider,
        DiscoveryDocumentResponse disco,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        using var req = new AuthorizationCodeTokenRequest
        {
            Address = disco.TokenEndpoint,
            ClientId = provider.ClientId,
            Code = code,
            RedirectUri = redirectUri,
            CodeVerifier = codeVerifier,
        };

        if (provider.ClientType == OidcClientType.Confidential)
        {
            var secret = ResolveSecret(provider.ConfidentialSecretRef);
            req.ClientSecret = secret;
        }

        var response = await _http.RequestAuthorizationCodeTokenAsync(req, ct).ConfigureAwait(false);
        if (response.IsError)
            throw new InvalidOperationException($"Token exchange failed: {response.Error}");

        return response;
    }

    /// <summary>
    /// Fetches the userinfo endpoint — used only when GroupsFromUserInfo is true.
    /// </summary>
    public async Task<UserInfoResponse> GetUserInfoAsync(
        DiscoveryDocumentResponse disco, string accessToken, CancellationToken ct = default)
    {
        using var request = new UserInfoRequest
        {
            Address = disco.UserInfoEndpoint,
            Token = accessToken,
        };

        var response = await _http.GetUserInfoAsync(request, ct).ConfigureAwait(false);

        if (response.IsError)
            throw new InvalidOperationException($"Userinfo request failed: {response.Error}");

        return response;
    }

    /// <summary>
    /// The signing keys out of a provider's JWKS. Both the ID token and the back-channel
    /// logout token are checked against these.
    /// </summary>
    public static List<SecurityKey> SigningKeysFrom(DiscoveryDocumentResponse disco)
        => disco.KeySet?.Keys?
            .Select(k => new JsonWebKey(System.Text.Json.JsonSerializer.Serialize(k)))
            .Cast<SecurityKey>()
            .ToList()
            ?? throw new SecurityTokenException("No signing keys in discovery document.");

    /// <summary>
    /// Reads a confidential client secret from an env var ("env:VAR_NAME") or file ("file:/path").
    /// The literal secret is never stored in plugin config. Internal so the rules can be
    /// tested without a provider: what this accepts decides whether the secret ends up in
    /// the configuration file after all.
    /// </summary>
    internal static string ResolveSecret(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new InvalidOperationException(
                "Confidential client requires ConfidentialSecretRef (env:VAR or file:/path).");

        if (secretRef.StartsWith("env:", StringComparison.Ordinal))
        {
            var varName = secretRef["env:".Length..];
            return Environment.GetEnvironmentVariable(varName)
                ?? throw new InvalidOperationException($"Env var '{varName}' not set.");
        }

        if (secretRef.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = secretRef["file:".Length..];
            return File.ReadAllText(path).Trim();
        }

        // Deliberately without the value. On a server where this was filled in by hand
        // before it was validated, the value is the client secret, and a log is not the
        // place for it either.
        throw new InvalidOperationException(
            "ConfidentialSecretRef must start with 'env:' or 'file:' — it names where the "
            + "secret is, it does not hold it.");
    }

    public void Dispose() => _http.Dispose();
}
