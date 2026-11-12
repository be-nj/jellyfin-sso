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
    ///
    /// Half an hour is still half an hour of failed logins when a rotation happens inside
    /// the window, which is why <see cref="Forget"/> exists: a token signed by a key this
    /// document does not contain is the one signal that says the document is stale, and
    /// the caller uses it to fetch once more rather than wait out the clock.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private sealed record CacheEntry(DiscoveryDocumentResponse Document, DateTimeOffset Expiry);

    private readonly HttpClient _http;
    private readonly ILogger<OidcClientFactory> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public OidcClientFactory(ILogger<OidcClientFactory> logger)
    {
        // Redirects are not followed. Every address this client calls was either
        // configured by an administrator or published by the provider and checked against
        // the authority's host below, and a redirect is a way to move the call after that
        // check has happened: the authorization code and the code verifier are in the body
        // of the token request, and a 307 to another host resends the body there. Nothing
        // this plugin talks to needs one — the three providers it is tested against answer
        // their published endpoints directly.
        //
        // A provider that accepts the connection and then says nothing must not hold a
        // login page open for the default hundred seconds.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
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

        // Duende checks the endpoints before it fetches jwks_uri, which is the point: a
        // document naming signing keys on another host must be refused before anything is
        // fetched from there. Its issuer and HTTPS checks stay on as well.
        //
        // Two allowances. Authentik publishes endpoints on a path shared between
        // applications (/application/o/token/) while the issuer is per application, so the
        // configured origin is allowed, not only the issuer's path; the host check after
        // the fetch still holds the endpoints the plugin uses to that origin. And Duende
        // compares host and prefix exactly, so the origin is given both as typed and as
        // normalised.
        //
        // Endpoints the plugin never calls are left out of Duende's check. A provider that
        // puts device authorization or introspection on another host is not handing this
        // plugin anything there, and refusing the whole document for it refused every
        // login and every back-channel logout of that provider.
        var policy = new DiscoveryPolicy { ValidateEndpoints = true };
        foreach (var origin in Origins(provider.Authority))
            policy.AdditionalEndpointBaseAddresses.Add(origin);
        foreach (var unused in EndpointsNotCalled)
            policy.EndpointValidationExcludeList.Add(unused);

        using var request = new DiscoveryDocumentRequest
        {
            Address = provider.Authority,
            Policy = policy,
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
            // The one that was written down, tested, and then not passed in. A logout
            // sends the user's own ID token to this address as an id_token_hint, so it
            // belongs with the three above: these are the addresses the plugin hands
            // something to.
            //
            // The authorize endpoint is deliberately not among them. Nothing is handed to
            // it — the browser goes there, with a state and a nonce that are useless to
            // anyone else — and where a provider puts the form people type into is its own
            // arrangement: a login UI on its own hostname is a layout several products
            // offer. There is nothing to win either, since whoever can write this document
            // can serve a form on the authority's own host just as easily.
            ("end_session_endpoint", doc.EndSessionEndpoint));

        _cache[cacheKey] = new CacheEntry(doc, DateTimeOffset.UtcNow.Add(CacheTtl));
        return doc;
    }

    /// <summary>
    /// Discovery entries Duende would hold to the authority's host that the plugin never
    /// sends anything to. The authorize endpoint is among them for the reason given at the
    /// host check below; the rest are features this client does not use.
    /// </summary>
    internal static readonly IReadOnlyList<string> EndpointsNotCalled =
    [
        "authorization_endpoint",
        "device_authorization_endpoint",
        "revocation_endpoint",
        "introspection_endpoint",
        "pushed_authorization_request_endpoint",
        "registration_endpoint",
        "backchannel_authentication_endpoint",
        "check_session_iframe",
    ];

    /// <summary>The authority's origin as typed and as normalised, without duplicates.</summary>
    internal static IEnumerable<string> Origins(string authority)
    {
        var normalised = new Uri(authority).GetLeftPart(UriPartial.Authority);
        yield return normalised;

        var schemeEnd = authority.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            yield break;
        var pathStart = authority.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        var typed = pathStart < 0 ? authority : authority[..pathStart];
        if (!string.Equals(typed, normalised, StringComparison.Ordinal))
            yield return typed;
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
    /// Drops the cached document for one provider, so the next call fetches it again.
    /// Called when a token arrives signed by a key the cached document does not know,
    /// which is what a key rotation looks like from here.
    /// </summary>
    public void Forget(ProviderConfig provider)
    {
        if (_cache.TryRemove($"{provider.Id}|{provider.Authority}", out _))
        {
            _logger.LogInformation(
                "Dropped the cached discovery document for {Name}; it will be fetched again.",
                provider.Name);
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
    /// The algorithms a token from a provider may be signed with: the asymmetric ones, and
    /// nothing else.
    ///
    /// Left unpinned, the set of acceptable algorithms is whatever the library will take
    /// against the keys it was handed. A JWKS that carries a symmetric key — an <c>oct</c>
    /// entry, which a provider has no reason to publish and every reason not to — would
    /// make that key's bytes an HMAC secret, and a JWKS is a public document. Naming the
    /// algorithms means a token has to be signed by somebody holding a private key, which
    /// is the whole assumption underneath every other check.
    /// </summary>
    internal static readonly string[] AsymmetricSigningAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384,
        SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384,
        SecurityAlgorithms.EcdsaSha512,
    ];

    /// <summary>
    /// The algorithms a token from one provider may be signed with: the ones that provider
    /// is configured for, or all of the asymmetric ones when it is configured for none.
    ///
    /// Two rules here are not the operator's to override, and both of them are about where
    /// this value goes — straight into <c>TokenValidationParameters.ValidAlgorithms</c>.
    ///
    /// It is intersected with <see cref="AsymmetricSigningAlgorithms"/> rather than used as
    /// typed. A configuration naming <c>HS256</c>, or <c>none</c>, would otherwise undo the
    /// pinning this setting exists to tighten and hand a public JWKS the job of an HMAC
    /// secret. A setting that can only narrow what is accepted cannot be used to widen it.
    ///
    /// And the result is never empty. The library skips the algorithm check altogether when
    /// <c>ValidAlgorithms</c> has no entries, so a list of names the plugin does not
    /// recognise would accept <em>more</em> than the default does — the opposite of what
    /// whoever typed it wanted. Such a list falls back to the default set; the save
    /// endpoint refuses one at the door, so it should never reach here at all.
    /// </summary>
    internal static string[] SigningAlgorithmsFor(ProviderConfig provider)
    {
        var pinned = provider.SigningAlgorithms
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Recognised(name.Trim()))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return pinned.Length > 0 ? pinned : AsymmetricSigningAlgorithms;
    }

    /// <summary>
    /// One configured name as the library spells it, or null for a name that is not an
    /// asymmetric signing algorithm. Compared without case so that <c>rs256</c> in the
    /// configuration still matches the <c>RS256</c> a provider puts in the header, which
    /// is compared exactly.
    /// </summary>
    internal static string? Recognised(string name)
        => Array.Find(
            AsymmetricSigningAlgorithms,
            known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The signing keys out of a provider's JWKS. Both the ID token and the back-channel
    /// logout token are checked against these.
    /// </summary>
    public static List<SecurityKey> SigningKeysFrom(DiscoveryDocumentResponse disco)
        => SigningKeysFrom(
            disco.KeySet?.Keys?.Select(k => System.Text.Json.JsonSerializer.Serialize(k)));

    /// <summary>
    /// The same selection, over the published keys as JSON. Separate so the rule can be
    /// tested without standing up a discovery response, which this library will not let a
    /// test construct.
    /// </summary>
    internal static List<SecurityKey> SigningKeysFrom(IEnumerable<string>? publishedKeys)
    {
        var published = publishedKeys
            ?? throw new SecurityTokenException("No signing keys in discovery document.");

        var forSignatures = published
            .Select(json => new JsonWebKey(json))
            .Where(IsForVerifyingSignatures)
            .Cast<SecurityKey>()
            .ToList();

        // A key set with keys in it, none of them for signatures, is not the same thing as
        // a key set with no keys — but there is nothing to check a token against either
        // way, and the caller has one question.
        if (forSignatures.Count == 0)
            throw new SecurityTokenException("No signing keys in discovery document.");

        return forSignatures;
    }

    /// <summary>
    /// Whether a published key may be used to verify a signature.
    ///
    /// RFC 7517 §4.2: "use" says whether a public key is for encrypting data or for
    /// verifying signatures. Every key in the set used to be handed to the validator,
    /// encryption keys included — the conformance suite publishes an RSA-OAEP key and an
    /// ECDH-ES key alongside its signing keys, and both were accepted as things a token
    /// could be signed with. The provider holds those private keys too, so this was not a
    /// forgery path; it was wider than what the provider declared, and an encryption key
    /// is the one that tends to live somewhere else — a different slot, a different
    /// rotation, sometimes shared with whoever is meant to encrypt to it.
    ///
    /// A key with no "use" at all is still accepted: the parameter is optional, and
    /// providers that publish one key and omit it are common.
    /// </summary>
    private static bool IsForVerifyingSignatures(JsonWebKey key)
        => string.IsNullOrEmpty(key.Use)
            || string.Equals(key.Use, "sig", StringComparison.Ordinal);

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
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                // Said without the path and without the reason. Everything above this is
                // reached from the anonymous callback, where an exception type the callback
                // does not expect becomes a 500 with its message in the body — and that
                // message named a file inside the container. An InvalidOperationException
                // is what the callback already turns into "ask the operator to read the
                // log", and the log is where the detail goes.
                throw new InvalidOperationException(
                    "The confidential client secret could not be read from the file named "
                    + "in ConfidentialSecretRef: " + ex.GetType().Name, ex);
            }
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
