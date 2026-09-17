using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// In-memory store for single-use ephemeral secrets produced after a successful OIDC login.
/// The client presents the secret on Jellyfin's native auth path; the bridge validates it here.
/// Secrets are cryptographically random, single-use, and expire after a short TTL.
/// Lost on restart (fine — the pending login just retries).
/// </summary>
public sealed class EphemeralSecretStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    private sealed record Entry(string Username, DateTimeOffset Expiry);

    private readonly ConcurrentDictionary<string, Entry> _store = new();

    public string Issue(string username)
    {
        Purge();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _store[secret] = new Entry(username, DateTimeOffset.UtcNow.Add(Ttl));
        return secret;
    }

    /// <summary>
    /// Validates and consumes the secret. Returns the bound username on success, null otherwise.
    /// Single-use: the entry is removed whether valid or not.
    /// </summary>
    public string? Consume(string secret)
    {
        if (!_store.TryRemove(secret, out var entry))
            return null;
        return entry.Expiry > DateTimeOffset.UtcNow ? entry.Username : null;
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _store)
        {
            if (entry.Expiry <= now)
                _store.TryRemove(key, out _);
        }
    }
}
