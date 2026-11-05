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
    /// True the first time this token is presented, false every time after. The key is the
    /// token's own <c>jti</c> where it has one, and a hash of the token where it does not —
    /// the whole token, so two tokens that differ only in signature are two records.
    /// </summary>
    public bool Accept(string providerId, string? jti, string logoutToken, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        Purge(at);

        var key = jti is { Length: > 0 }
            ? $"{providerId}\njti\n{jti}"
            : $"{providerId}\nhash\n{Hash(logoutToken)}";

        if (_seen.Count >= MaxRemembered)
        {
            // Drop the oldest. Refusing to remember a new one would be refusing to notice
            // a replay, which is the wrong way round to fail.
            foreach (var oldest in _seen.OrderBy(e => e.Value).Take(_seen.Count - MaxRemembered + 1))
            {
                _seen.TryRemove(oldest.Key, out _);
            }
        }

        return _seen.TryAdd(key, at.Add(_retention));
    }

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
