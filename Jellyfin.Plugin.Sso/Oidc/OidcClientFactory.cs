using System.Collections.Concurrent;
using System.Net.Http.Headers;
using IdentityModel.Client;
using Jellyfin.Plugin.Sso.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// Creates and caches per-provider OIDC discovery documents.
/// </summary>
public sealed class OidcClientFactory : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<OidcClientFactory> _logger;
    private readonly ConcurrentDictionary<string, DiscoveryDocumentResponse> _cache = new();

    public OidcClientFactory(ILogger<OidcClientFactory> logger)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Jellyfin.Plugin.Sso", "1.0"));
        _logger = logger;
    }

    public async Task<DiscoveryDocumentResponse> GetDiscoveryDocumentAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(provider.Id, out var cached) && !cached.IsError)
            return cached;

        _logger.LogInformation("Fetching OIDC discovery for provider {Name} ({Authority})",
            provider.Name, provider.Authority);

        // Authentik (and some other IdPs) use a shared authorize endpoint
        // (e.g. /application/o/authorize/) that differs from the per-app issuer path.
        // IdentityModel's strict endpoint validation rejects this — disable it.
        var doc = await _http.GetDiscoveryDocumentAsync(
            new DiscoveryDocumentRequest
            {
                Address = provider.Authority,
                Policy = new DiscoveryPolicy
                {
                    ValidateEndpoints = false,
                    ValidateIssuerName = false,
                },
            }, ct)
            .ConfigureAwait(false);

        if (doc.IsError)
            throw new InvalidOperationException(
                $"OIDC discovery failed for {provider.Authority}: {doc.Error}");

        _cache[provider.Id] = doc;
        return doc;
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
        var req = new AuthorizationCodeTokenRequest
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
        var response = await _http.GetUserInfoAsync(new UserInfoRequest
        {
            Address = disco.UserInfoEndpoint,
            Token = accessToken,
        }, ct).ConfigureAwait(false);

        if (response.IsError)
            throw new InvalidOperationException($"Userinfo request failed: {response.Error}");

        return response;
    }

    /// <summary>
    /// Reads a confidential client secret from an env var ("env:VAR_NAME") or file ("file:/path").
    /// The literal secret is never stored in plugin config.
    /// </summary>
    private static string ResolveSecret(string? secretRef)
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

        throw new InvalidOperationException(
            $"ConfidentialSecretRef must start with 'env:' or 'file:'. Got: {secretRef}");
    }

    public void Dispose() => _http.Dispose();
}
