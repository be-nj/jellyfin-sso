using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// Holds a finished SSO login that is about to approve a Quick Connect code, until the
/// person says yes.
///
/// Without this step, opening a link that carries somebody else's Quick Connect code and
/// signing in normally would hand that somebody a session: the provider's login page is
/// genuine, so nothing looks wrong. Jellyfin's own Quick Connect asks the user to read a
/// code off the device and type it in — the friction is the check. This restores it in
/// one click, after the login rather than before it.
/// </summary>
public sealed class QuickConnectApprovalStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _ttl;

    public QuickConnectApprovalStore()
        : this(DefaultTtl)
    {
    }

    /// <summary>Lets a test watch an approval expire without waiting two minutes.</summary>
    internal QuickConnectApprovalStore(TimeSpan ttl)
    {
        _ttl = ttl;
    }

    public sealed record Pending(Guid UserId, string Username, string Code, DateTimeOffset Expiry);

    private readonly ConcurrentDictionary<string, Pending> _store = new();

    /// <summary>Remembers the login and hands back the token the approval page posts.</summary>
    public string Issue(Guid userId, string username, string code)
    {
        Purge();

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _store[token] = new Pending(userId, username, code, DateTimeOffset.UtcNow.Add(_ttl));
        return token;
    }

    /// <summary>Takes the pending approval, once. Null when unknown or expired.</summary>
    public Pending? Consume(string? token)
    {
        if (token is null || !_store.TryRemove(token, out var pending))
        {
            return null;
        }

        return pending.Expiry > DateTimeOffset.UtcNow ? pending : null;
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, pending) in _store)
        {
            if (pending.Expiry <= now)
            {
                _store.TryRemove(key, out _);
            }
        }
    }
}
