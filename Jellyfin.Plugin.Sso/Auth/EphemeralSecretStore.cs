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
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(90);

    private readonly TimeSpan _ttl;

    public EphemeralSecretStore()
        : this(DefaultTtl)
    {
    }

    /// <summary>Lets a test watch a secret expire without waiting 90 seconds.</summary>
    internal EphemeralSecretStore(TimeSpan ttl)
    {
        _ttl = LoginLifetime.Covered(ttl);
    }

    internal sealed record Entry(Guid UserId, string Username, LoginStamp Stamp, DateTimeOffset Expiry);

    private readonly ConcurrentDictionary<string, Entry> _store = new();

    public string Issue(Guid userId, string username)
        => Issue(userId, username, LoginStamp.Now());

    /// <summary>
    /// Issues a secret that dies with any revocation of the account or of the identity in
    /// <paramref name="stamp"/> since the login it finishes started.
    /// </summary>
    public string Issue(Guid userId, string username, LoginStamp stamp)
    {
        Purge();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _store[secret] = new Entry(userId, username, stamp, DateTimeOffset.UtcNow.Add(_ttl));
        return secret;
    }

    /// <summary>
    /// Validates and consumes the secret. Returns the bound username on success, null otherwise.
    /// Single-use: the entry is removed whether valid or not.
    /// </summary>
    public string? Consume(string secret)
        => ConsumeEntry(secret)?.Username;

    internal Entry? ConsumeEntry(string secret)
    {
        if (!_store.TryRemove(secret, out var entry))
            return null;
        return entry.Expiry > DateTimeOffset.UtcNow && LoginLifetime.IsCurrent(entry.UserId, entry.Stamp)
            ? entry : null;
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
