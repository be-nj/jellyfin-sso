using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
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

    /// <summary>
    /// <c>Hosts</c> is the list of further endpoint hosts the document was accepted with.
    /// It is part of the entry and not of the key, so that there is one entry per
    /// provider for <see cref="Forget"/> to drop and none left behind by an edited list.
    /// </summary>
    private sealed record CacheEntry(DiscoveryDocumentResponse Document, DateTimeOffset Expiry, string Hosts);

    /// <summary>How long one call to the provider may take, body included.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The most the plugin reads of any one answer from the provider: discovery, keys,
    /// token, userinfo. A real one is a few kilobytes. The large ones are userinfo answers
    /// carrying every group a person is in, and a mebibyte holds some twenty thousand
    /// group names of fifty characters — further than any directory this plugin has met.
    /// The ID token cannot even get near it: the token handler refuses one over 250 KB.
    /// </summary>
    internal const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<OidcClientFactory> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<DiscoveryDocumentResponse>>> _refreshes = new();

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
        //
        // And one that keeps talking must not fill the server's memory: without a cap the
        // client buffers whatever arrives within the timeout, which over a local network
        // is gigabytes (#322). See MaxResponseBytes for the number.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
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

        // A document is good for the hosts it was accepted with. A host an administrator
        // takes off the list must stop being trusted at once, not when the document that
        // was accepted with it expires.
        var hosts = string.Join(' ', provider.EndpointHosts);

        if (_cache.TryGetValue(cacheKey, out var cached)
            && !cached.Document.IsError
            && cached.Expiry > DateTimeOffset.UtcNow
            && string.Equals(cached.Hosts, hosts, StringComparison.Ordinal))
        {
            return cached.Document;
        }

        return await FetchAsync(provider, cacheKey, hosts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the document again although the cached one has not expired, for a token
    /// signed by a key the cached one does not carry — what a key rotation looks like from
    /// here.
    ///
    /// The cached document stays until the new one has arrived and passed every check,
    /// and callers that ask at the same time share one fetch. The back-channel logout
    /// endpoint is anonymous and anyone can post a token naming a key nobody published:
    /// dropping the cache first meant each such post emptied the document logins use, and
    /// one failed or throttled fetch then failed every login and logout (#316).
    /// </summary>
    public async Task<DiscoveryDocumentResponse> RefreshAsync(
        ProviderConfig provider, CancellationToken ct = default)
    {
        var cacheKey = $"{provider.Id}|{provider.Authority}";
        var hosts = string.Join(' ', provider.EndpointHosts);

        // Not cancelled by the first caller's token: the fetch is shared, and one caller
        // giving up must not fail the others that are waiting on it.
        // Lazy, so that a race inside GetOrAdd cannot start a second fetch; removed when
        // it completes, however it completes, so the next unknown key fetches anew.
        var entry = _refreshes.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<DiscoveryDocumentResponse>>(
                () => FetchAsync(provider, cacheKey, hosts, CancellationToken.None)));
        var fetch = entry.Value;
        _ = fetch.ContinueWith(
            _ => _refreshes.TryRemove(
                new KeyValuePair<string, Lazy<Task<DiscoveryDocumentResponse>>>(cacheKey, entry)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await fetch.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<DiscoveryDocumentResponse> FetchAsync(
        ProviderConfig provider, string cacheKey, string hosts, CancellationToken ct)
    {
        _logger.LogInformation("Fetching OIDC discovery for provider {Name} ({Authority})",
            provider.Name, provider.Authority);

        // Duende checks the endpoints before it fetches jwks_uri, which is the point: a
        // document naming signing keys on another host must be refused before anything is
        // fetched from there. Its issuer and HTTPS checks stay on as well.
        //
        // Three allowances. Authentik publishes endpoints on a path shared between
        // applications (/application/o/token/) while the issuer is per application, so the
        // configured origin is allowed, not only the issuer's path; the host check after
        // the fetch still holds the endpoints the plugin uses to that origin. Duende
        // compares host and prefix exactly, so the origin is given both as typed and as
        // normalised. And a provider that really does serve its keys and its token
        // endpoint from other hosts — Google does, from three of them — is allowed the
        // hosts an administrator named for it, and no others.
        //
        // Endpoints the plugin never calls are left out of Duende's check. A provider that
        // puts device authorization or introspection on another host is not handing this
        // plugin anything there, and refusing the whole document for it refused every
        // login and every back-channel logout of that provider.
        var policy = new DiscoveryPolicy { ValidateEndpoints = true };
        foreach (var origin in Origins(provider.Authority))
            policy.AdditionalEndpointBaseAddresses.Add(origin);
        foreach (var origin in FurtherOrigins(provider.Authority, provider.EndpointHosts))
            policy.AdditionalEndpointBaseAddresses.Add(origin);
        foreach (var unused in EndpointsNotCalled)
            policy.EndpointValidationExcludeList.Add(unused);

        using var request = new DiscoveryDocumentRequest
        {
            Address = provider.Authority,
            Policy = policy,
        };

        var doc = await Answered(
            () => _http.GetDiscoveryDocumentAsync(request, ct), "discovery", ct).ConfigureAwait(false);

        // Before the library's own error, because this one can be acted on. Duende stops
        // at the first endpoint it dislikes and names an address; the plugin's check
        // names every host at once together with the setting that would admit them.
        // Duende empties the document it refused, so the endpoints are read from the
        // text it was sent. On a document that never arrived there is nothing to read,
        // and this passes straight through to the library's error.
        var published = PublishedEndpoints(doc.Raw);
        RequireEndpointsOnKnownHosts(
            provider.Authority,
            provider.EndpointHosts,
            ("jwks_uri", published("jwks_uri")),
            ("token_endpoint", published("token_endpoint")),
            ("userinfo_endpoint", published("userinfo_endpoint")),
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
            ("end_session_endpoint", published("end_session_endpoint")));

        if (doc.IsError)
            throw new InvalidOperationException(
                $"OIDC discovery failed for {provider.Authority}: {ProviderSaid(doc.Error)}");

        _cache[cacheKey] = new CacheEntry(doc, DateTimeOffset.UtcNow.Add(CacheTtl), hosts);
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

    /// <summary>
    /// Reads endpoint addresses out of a discovery document as it was sent, whether or
    /// not the library went on to accept it. Anything that is not a JSON object with
    /// string members has no endpoints to speak of.
    /// </summary>
    private static Func<string, string?> PublishedEndpoints(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return _ => null;

        try
        {
            using var parsed = JsonDocument.Parse(raw);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return _ => null;

            var members = parsed.RootElement.EnumerateObject()
                .Where(member => member.Value.ValueKind == JsonValueKind.String)
                .GroupBy(member => member.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value.GetString(), StringComparer.Ordinal);
            return name => members.GetValueOrDefault(name);
        }
        catch (JsonException)
        {
            return _ => null;
        }
    }

    /// <summary>The authority's origin as typed and as normalised, without duplicates.</summary>
    internal static IEnumerable<string> Origins(string authority)
    {
        var normalised = new Uri(authority).GetLeftPart(UriPartial.Authority);
        yield return normalised;

        var typed = TypedOrigin(authority);
        if (typed is not null && !string.Equals(typed, normalised, StringComparison.Ordinal))
            yield return typed;
    }

    /// <summary>Scheme, host and port of an address exactly as it was written.</summary>
    private static string? TypedOrigin(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return null;
        var pathStart = url.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        return pathStart < 0 ? url : url[..pathStart];
    }

    /// <summary>
    /// The further endpoint hosts as origins Duende can compare against: on the
    /// authority's scheme, as typed and as normalised, for the reason given at
    /// <see cref="Origins"/>. An entry that is not a host name is left out, which makes a
    /// hand-edited configuration stricter rather than looser.
    /// </summary>
    internal static IEnumerable<string> FurtherOrigins(string authority, IEnumerable<string> hosts)
    {
        var scheme = new Uri(authority).Scheme;
        return hosts
            .Select(entry => (Typed: entry?.Trim() ?? string.Empty, Host: EndpointHost(entry)))
            .Where(entry => entry.Host is not null)
            .SelectMany(entry => new[] { entry.Host!, entry.Typed })
            .Distinct(StringComparer.Ordinal)
            .Select(host => $"{scheme}://{host}");
    }

    /// <summary>
    /// A further endpoint host in the form it is compared in — a host name, with a port
    /// where the endpoints use one that is not the scheme's own — or null for anything
    /// else. No scheme, no path, and no wildcard: a list that can only name hosts cannot
    /// be made to mean "anywhere", which is what the rule it loosens exists to prevent.
    /// </summary>
    internal static string? EndpointHost(string? entry)
    {
        var typed = entry?.Trim();
        if (string.IsNullOrEmpty(typed)
            || typed.IndexOfAny(['/', '\\', '*', '@', '?', '#', ' ', '\t']) >= 0
            || !Uri.TryCreate("https://" + typed, UriKind.Absolute, out var uri)
            || uri.HostNameType == UriHostNameType.Unknown
            || uri.Host.Length == 0
            || !IsPlainHost(uri)
            || (!string.Equals(uri.Authority, typed, StringComparison.OrdinalIgnoreCase)
                // The scheme's own port written out, which Uri drops from Authority.
                && !string.Equals($"{uri.Host}:{uri.Port}", typed, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return uri.Authority;
    }

    /// <summary>
    /// A host System.Uri recognises as a name or an address, with a port from 1 to 65535, and
    /// that is one in its ASCII form too: a no-break space or a fullwidth slash is kept by
    /// Uri in the host as typed and is no part of any name a document can carry (#327).
    /// </summary>
    private static bool IsPlainHost(Uri uri)
    {
        try
        {
            return uri.Port is >= 1 and <= 65535
                && Uri.CheckHostName(uri.Host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
                && uri.IdnHost.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    or '.' or '-' or '_' or ':' or '[' or ']');
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses a discovery document that moves an endpoint to a host nobody named.
    ///
    /// The paths are the provider's business — Authentik's differ from its issuer by
    /// design — but the host is not. These four are where the client secret and the
    /// authorization code are sent, where the keys that decide whether a token is genuine
    /// come from, and where a user's ID token is sent as a logout hint. A document that
    /// names another host for any of them is not a layout quirk — unless it is the
    /// provider's documented layout, as Google's is, and then the administrator who
    /// configured the provider says so under <em>Further endpoint hosts</em>. The
    /// document cannot say it for itself.
    ///
    /// Every host is named in one message, with the line to paste into that field: the
    /// refusal is read in a log by somebody who wants to sign in, and one host per
    /// attempt would be three attempts for Google.
    /// </summary>
    internal static void RequireEndpointsOnKnownHosts(
        string? authorityUrl,
        IEnumerable<string> furtherHosts,
        params (string Name, string? Value)[] endpoints)
    {
        if (!Uri.TryCreate(authorityUrl, UriKind.Absolute, out var authority))
        {
            throw new InvalidOperationException(
                $"The authority '{authorityUrl}' is not an absolute URL.");
        }

        var known = furtherHosts
            .Select(EndpointHost)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var moved = new List<string>();
        var hosts = new List<string>();
        foreach (var (name, value) in endpoints)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
            {
                moved.Add($"{name} somewhere that is not an address");
                continue;
            }

            if (string.Equals(endpoint.Host, authority.Host, StringComparison.OrdinalIgnoreCase)
                || known.Contains(endpoint.Authority))
            {
                continue;
            }

            moved.Add($"{name} on {endpoint.Authority}");
            if (!hosts.Contains(endpoint.Authority, StringComparer.OrdinalIgnoreCase))
                hosts.Add(endpoint.Authority);
        }

        if (moved.Count == 0)
        {
            return;
        }

        var message = $"The discovery document for {authorityUrl} puts an endpoint on another "
            + $"host: {string.Join(", ", moved)}. Refusing it: these endpoints decide whether "
            + "a login is genuine.";
        if (hosts.Count > 0)
        {
            message += " If the provider documents this layout, as Google does, name the "
                + "hosts under 'Further endpoint hosts' on the provider card: "
                + string.Join(' ', hosts);
        }

        throw new InvalidOperationException(message);
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
            req.ClientCredentialStyle = SecretStyleFor(disco.TokenEndpointAuthenticationMethodsSupported);
        }
        else
        {
            // A public client has nothing to authenticate with, and says who it is with
            // client_id in the form (RFC 6749 §4.1.3). The library's default style put
            // the id into a Basic header with an empty password instead: an attempt to
            // authenticate, which the providers this is tested against forgive and a
            // stricter one answers with invalid_client.
            req.ClientCredentialStyle = ClientCredentialStyle.PostBody;
        }

        var response = await Answered(
            () => _http.RequestAuthorizationCodeTokenAsync(req, ct), "the token request", ct)
            .ConfigureAwait(false);
        if (response.IsError)
            throw new InvalidOperationException($"Token exchange failed: {ProviderSaid(response.Error)}");

        return response;
    }

    /// <summary>
    /// One call to the provider, with the two ways it can fail that the library lets
    /// through turned into the failure every caller already answers in words.
    ///
    /// Duende turns most transport errors into an error response, but rethrows a
    /// cancellation — and the client's own timeout is a cancellation. It escaped the
    /// login page as a 500 after twenty seconds (#321). A cancellation of the request
    /// itself is still let through: nobody is waiting for an answer to that one.
    /// An oversized answer (#322) is refused by the client as an HttpRequestException,
    /// which is handled here too in case the library ever stops catching it.
    /// </summary>
    internal static async Task<T> Answered<T>(Func<Task<T>> call, string what, CancellationToken ct)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"The provider did not finish answering {what} within "
                + $"{RequestTimeout.TotalSeconds:0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"The provider's answer to {what} could not be read: {ProviderSaid(ex.Message)}", ex);
        }
    }

    /// <summary>
    /// What a provider answered a failed request with, made fit for a log line. These
    /// strings come out of the provider's response — an <c>error</c> member, an issuer the
    /// document named — and every one of them ends up in an exception message that is
    /// logged. Left as sent, a newline in one writes a line of its own into the server
    /// log, timestamp and level included. The provider is trusted for who a person is;
    /// that is no reason to let it write the log.
    /// </summary>
    private static string ProviderSaid(string? text) => Logging.Loggable.OneLine(text);

    /// <summary>
    /// How the client secret is sent to the token endpoint. HTTP Basic, which is what
    /// OpenID Connect expects of a client that registered nothing else, and what a
    /// provider that publishes no list is taken to support — unless the provider's own
    /// discovery document says it takes the secret as a form parameter and does not take
    /// Basic. Then the form it is, because the alternative is a login that cannot work
    /// and an operator hunting for a setting that does not exist.
    ///
    /// Read from the document rather than configured: the provider knows which it
    /// accepts, and a field for it would be one more thing to get wrong.
    /// </summary>
    internal static ClientCredentialStyle SecretStyleFor(IEnumerable<string>? supported)
    {
        var methods = supported?.ToList() ?? [];
        return methods.Contains("client_secret_post", StringComparer.Ordinal)
            && !methods.Contains("client_secret_basic", StringComparer.Ordinal)
                ? ClientCredentialStyle.PostBody
                : ClientCredentialStyle.AuthorizationHeader;
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

        var response = await Answered(
            () => _http.GetUserInfoAsync(request, ct), "the userinfo request", ct).ConfigureAwait(false);

        if (response.IsError)
            throw new InvalidOperationException($"Userinfo request failed: {ProviderSaid(response.Error)}");

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
