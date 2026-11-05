using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// Holds per-request OIDC state (CSRF token, nonce, PKCE verifier) until the callback
/// arrives. In-memory only — a lost entry means the user retries login (acceptable).
/// </summary>
public sealed class PendingStateStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many logins may be in flight. Anyone can start one without signing in, and
    /// each entry lives for ten minutes, so the store needs an end as well as a clock.
    /// Ten thousand is far beyond a household and still only a few megabytes.
    /// </summary>
    internal const int MaxPending = 10_000;

    private readonly TimeSpan _ttl;
    private readonly ILogger<PendingStateStore> _logger;

    public PendingStateStore(ILogger<PendingStateStore> logger)
        : this(DefaultTtl, logger)
    {
    }

    /// <summary>Lets a test watch a login expire without waiting ten minutes.</summary>
    internal PendingStateStore(TimeSpan ttl, ILogger<PendingStateStore>? logger = null)
    {
        _ttl = ttl;
        _logger = logger ?? NullLogger<PendingStateStore>.Instance;
    }

    public sealed record PendingState(
        string ProviderId,
        string CodeVerifier,
        string Nonce,
        string RedirectUri,
        DateTimeOffset Expiry,
        // The browser that started this login, from the cookie minted at the same time.
        // Without it the state alone is the whole authority, and a state is something an
        // attacker can hold and hand to somebody else — which is how a link flow could be
        // finished in a stranger's browser.
        string BrowserId,
        // non-null only for Quick Connect path
        string? QuickConnectCode = null,
        // non-null only when an existing user is linking their own account
        Guid? LinkUserId = null);

    private readonly ConcurrentDictionary<string, PendingState> _store = new();

    public (string State, PendingState Pending) Issue(
        string providerId,
        string redirectUri,
        string browserId,
        string? quickConnectCode = null,
        Guid? linkUserId = null)
    {
        Purge();

        var state = GenerateUrlSafe(32);
        var nonce = GenerateUrlSafe(32);
        var verifier = GenerateUrlSafe(64);

        var pending = new PendingState(
            providerId,
            verifier,
            nonce,
            redirectUri,
            DateTimeOffset.UtcNow.Add(_ttl),
            browserId,
            quickConnectCode,
            linkUserId);

        if (_store.Count >= MaxPending)
        {
            // Refuse the newest rather than drop the oldest, which is the other way round
            // from how this started. Every entry has the same ten-minute life, so expiry
            // order is arrival order, and dropping the oldest dropped the login that had
            // been in flight longest — the person still typing their password at the
            // provider. Anyone can start a login, so that was a way to throw a chosen
            // person out of a flow they were in the middle of, at a chosen moment, and
            // have it look to them like their own mistake.
            //
            // Refusing instead makes a flood deny only logins that have not started yet,
            // and the flood has to be sustained: entries expire. The caller gets a 503
            // with words rather than a login that dies later at the callback.
            _logger.LogWarning(
                "Refusing to start a login: {Count} are already in flight, which is the "
                + "cap. In-flight logins are kept; this one has to start again.",
                _store.Count);

            throw new TooManyPendingLoginsException();
        }

        _store[state] = pending;
        return (state, pending);
    }

    /// <summary>
    /// The pending login for this state, or null when there is none, it has expired, or it
    /// belongs to a different browser. Single-use either way: a state that is presented
    /// with the wrong browser id is spent, not left for a second try.
    /// </summary>
    public PendingState? Consume(string state, string? browserId)
    {
        if (!_store.TryRemove(state, out var pending))
            return null;

        if (pending.Expiry <= DateTimeOffset.UtcNow)
            return null;

        // Ordinal, not constant-time: the value is 256 bits of randomness looked up in a
        // hash table, so there is no guessing game for a timing signal to help with.
        return string.Equals(pending.BrowserId, browserId, StringComparison.Ordinal)
            ? pending
            : null;
    }

    /// <summary>
    /// A fresh browser id, or the one this browser already has. Reused on purpose: two
    /// logins started in two tabs share the cookie, and generating a new id per login
    /// would mean the second tab silently invalidated the first.
    /// </summary>
    public static string BrowserId(string? fromCookie)
        => WellFormedBrowserId(fromCookie) ? fromCookie! : GenerateUrlSafe(32);

    /// <summary>Shape only — the value's authority comes from matching a stored one.</summary>
    internal static bool WellFormedBrowserId(string? value)
        => value is { Length: 43 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>Derives the PKCE code_challenge (S256) from the code_verifier.</summary>
    public static string CodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string GenerateUrlSafe(int byteLength)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, val) in _store)
            if (val.Expiry <= now)
                _store.TryRemove(key, out _);
    }

    /// <summary>
    /// Thrown when the store is full. The caller answers "try again" rather than taking
    /// somebody else's place in the queue.
    /// </summary>
    public sealed class TooManyPendingLoginsException : Exception
    {
        public TooManyPendingLoginsException()
            : base("Too many logins are in flight.")
        {
        }
    }
}
