using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// Holds per-request OIDC state (CSRF token, nonce, PKCE verifier) until the callback
/// arrives. In-memory only — a lost entry means the user retries login (acceptable).
/// </summary>
public sealed class PendingStateStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public sealed record PendingState(
        string ProviderId,
        string CodeVerifier,
        string Nonce,
        string RedirectUri,
        DateTimeOffset Expiry,
        // non-null only for Quick Connect path
        string? QuickConnectCode = null);

    private readonly ConcurrentDictionary<string, PendingState> _store = new();

    public (string State, PendingState Pending) Issue(
        string providerId,
        string redirectUri,
        string? quickConnectCode = null)
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
            DateTimeOffset.UtcNow.Add(Ttl),
            quickConnectCode);

        _store[state] = pending;
        return (state, pending);
    }

    public PendingState? Consume(string state)
    {
        if (!_store.TryRemove(state, out var pending))
            return null;
        return pending.Expiry > DateTimeOffset.UtcNow ? pending : null;
    }

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
}
