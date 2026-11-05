using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// The logout tokens already acted on, for as long as one could still be presented again.
///
/// Freshness alone was the replay control: a logout token is accepted while its
/// <c>iat</c> is inside <see cref="LogoutTokenValidator.MaxAge"/>, and nothing recorded
/// which ones had been seen. Anyone able to read one in flight — a proxy that terminates
/// TLS, a log that records request bodies — could post it again for the rest of that
/// window and end the subject's sessions each time, including a session started after the
/// real logout. The stores beside this one are all single-use; this is that rule for
/// logout tokens.
/// </summary>
public sealed class LogoutTokenLedger
{
    /// <summary>
    /// How long a token is remembered: its whole acceptable life, so a record can never
    /// expire while the token it describes would still be accepted.
    /// </summary>
    private static readonly TimeSpan Retention =
        LogoutTokenValidator.MaxAge + TimeSpan.FromSeconds(120);

    /// <summary>
    /// A provider sending more logout tokens than this inside the window is not a
    /// household signing out; the cap keeps the dictionary bounded either way.
    /// </summary>
    internal const int MaxRemembered = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private readonly TimeSpan _retention;

    public LogoutTokenLedger()
        : this(Retention)
    {
    }

    /// <summary>Lets a test watch a record expire without waiting seven minutes.</summary>
    internal LogoutTokenLedger(TimeSpan retention)
    {
        _retention = retention;
    }

    /// <summary>
    /// Whether this token has already been acted on. Asking does not record: a caller
    /// checks first, does the work, and only then remembers — so a failure in between
    /// leaves the provider's retry a retry rather than a refused replay.
    /// </summary>
    public bool AlreadyActedOn(
        string providerId, string? jti, string logoutToken, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        Purge(at);

        return _seen.ContainsKey(Key(providerId, jti, logoutToken));
    }

    /// <summary>
    /// Records a token as acted on. True the first time, false if something else got
    /// there first — two simultaneous posts of the same token both do the work, which is
    /// idempotent, and only one records it.
    /// </summary>
    public bool Remember(
        string providerId, string? jti, string logoutToken, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        Purge(at);

        if (_seen.Count >= MaxRemembered)
        {
            // Drop the oldest. Refusing to remember a new one would be refusing to notice
            // a replay, which is the wrong way round to fail.
            foreach (var oldest in _seen.OrderBy(e => e.Value).Take(_seen.Count - MaxRemembered + 1))
            {
                _seen.TryRemove(oldest.Key, out _);
            }
        }

        return _seen.TryAdd(Key(providerId, jti, logoutToken), at.Add(_retention));
    }

    /// <summary>
    /// The token's own <c>jti</c> where it has one, and a hash of the whole token where it
    /// does not, so two tokens that differ only in signature are two records. Scoped per
    /// provider, because two providers may both mint a token called "1".
    /// </summary>
    private static string Key(string providerId, string? jti, string logoutToken)
        => jti is { Length: > 0 }
            ? $"{providerId}\njti\n{jti}"
            : $"{providerId}\nhash\n{Hash(logoutToken)}";

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void Purge(DateTimeOffset now)
    {
        foreach (var (key, expiry) in _seen)
        {
            if (expiry <= now)
            {
                _seen.TryRemove(key, out _);
            }
        }
    }
}
