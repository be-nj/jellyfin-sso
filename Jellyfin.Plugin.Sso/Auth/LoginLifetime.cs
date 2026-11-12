using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// Orders revocation against everything that can still turn into a Jellyfin token: login
/// states, one-time secrets, Quick Connect approvals, and the token minting that happens
/// inside Jellyfin after the plugin's part is done.
///
/// Revocation never waits for a login. It records a version for the account and for
/// every identity linked to it, synchronously, and only then sweeps the session store.
/// Everything a login carries is stamped with the version current when that login
/// started, and is refused once a later revocation names its account or identity. The
/// places that mint a token check again afterwards and take it back themselves when a
/// revocation slipped in between; anything minted after that check is old enough for the
/// sweep to find.
///
/// An earlier version put one process-wide gate around all of this, including Jellyfin's
/// own password logins. That serialised every login on the server, and an anonymous flood
/// of password attempts could keep a back-channel logout waiting until the provider gave
/// up on it.
///
/// State is lost on restart, together with every pending credential it guards. Records
/// expire after <see cref="RevocationLifetime"/>; every store that holds a stamped
/// credential refuses a lifetime that long, so nothing can outlive the record that
/// invalidates it.
/// </summary>
internal static class LoginLifetime
{
    internal static readonly TimeSpan RevocationLifetime = TimeSpan.FromMinutes(15);

    private sealed record Revocation(long Version, DateTimeOffset Until);

    private static readonly ConcurrentDictionary<Guid, Revocation> Users = new();
    private static readonly ConcurrentDictionary<(string Provider, string Sub), Revocation> Identities = new();
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Sweeps = new();
    private static long _version;

    internal static long Version => Interlocked.Read(ref _version);

    internal static bool IsCurrent(Guid userId, long version)
        => !Users.TryGetValue(userId, out var revoked) || version >= revoked.Version;

    internal static bool IsCurrent(string provider, string sub, long version)
        => !Identities.TryGetValue((provider, sub), out var revoked) || version >= revoked.Version;

    /// <summary>Whether a credential stamped with <paramref name="stamp"/> may still be spent.</summary>
    internal static bool IsCurrent(Guid userId, LoginStamp stamp)
        => IsCurrent(userId, stamp.Version)
            && (stamp.ProviderId is null || stamp.Sub is null
                || IsCurrent(stamp.ProviderId, stamp.Sub, stamp.Version));

    internal static void Revoke(Guid userId)
        => Users[userId] = NewRevocation();

    internal static void Revoke(string provider, string sub)
        => Identities[(provider, sub)] = NewRevocation();

    /// <summary>
    /// Held by a revocation while it sweeps one account's sessions, and briefly by a check
    /// that has to know no sweep of that account is half done. Never held by a login while
    /// it authenticates, so nothing a stranger can send makes a revocation wait.
    /// </summary>
    internal static async Task<IDisposable> SweepingAsync(Guid userId)
    {
        var gate = Sweeps.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        return new Release(gate);
    }

    /// <summary>Refuses a credential lifetime the revocation records would not cover.</summary>
    internal static TimeSpan Covered(TimeSpan ttl)
        => ttl < RevocationLifetime
            ? ttl
            : throw new ArgumentOutOfRangeException(
                nameof(ttl), ttl, $"Must be shorter than the revocation lifetime ({RevocationLifetime}).");

    private static Revocation NewRevocation()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in Users.Where(e => e.Value.Until <= now).Select(e => e.Key))
            Users.TryRemove(expired, out _);
        foreach (var expired in Identities.Where(e => e.Value.Until <= now).Select(e => e.Key))
            Identities.TryRemove(expired, out _);
        return new Revocation(Interlocked.Increment(ref _version), now.Add(RevocationLifetime));
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}

/// <summary>
/// What a login carried from its start: the revocation version current then, and the
/// identity it was for once that is known. A credential issued at the end of a login is
/// stamped with this, not with the version current at issue time, so a revocation that
/// landed anywhere in between still reaches it.
/// </summary>
public sealed record LoginStamp(long Version, string? ProviderId = null, string? Sub = null)
{
    internal static LoginStamp Now() => new(LoginLifetime.Version);
}
