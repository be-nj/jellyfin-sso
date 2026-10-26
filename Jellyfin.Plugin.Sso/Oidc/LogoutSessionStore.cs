using System.Collections.Concurrent;
using System.Security.Cryptography;
using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// Keeps validated ID tokens server-side for browser logout. Only an opaque, single-use
/// handle goes into the browser's HttpOnly session cookie; nothing is persisted to disk.
/// </summary>
public sealed class LogoutSessionStore
{
    internal const string CookieName = "SsoLogoutSession";
    internal const int MaxSessions = 10_000;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public LogoutSessionStore()
        : this(TimeSpan.FromDays(30))
    {
    }

    internal LogoutSessionStore(TimeSpan ttl) => _ttl = ttl;

    public sealed record Session(
        string ProviderId, string Authority, string ClientId, string IdentityToken,
        DateTimeOffset Expiry)
    {
        public bool Matches(ProviderConfig provider)
            => ProviderId == provider.Id && Authority == provider.Authority
                && ClientId == provider.ClientId;
    }

    public string Issue(ProviderConfig provider, string identityToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, session) in _sessions)
            if (session.Expiry <= now)
                _sessions.TryRemove(key, out _);

        if (_sessions.Count >= MaxSessions)
            foreach (var oldest in _sessions.OrderBy(e => e.Value.Expiry)
                         .Take(_sessions.Count - MaxSessions + 1))
                _sessions.TryRemove(oldest.Key, out _);

        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[handle] = new Session(
            provider.Id, provider.Authority, provider.ClientId, identityToken, now.Add(_ttl));
        return handle;
    }

    public Session? Consume(string? handle)
    {
        if (handle is null || !_sessions.TryRemove(handle, out var session))
            return null;

        // An expired ID token can still identify the OP session at logout. This TTL
        // limits retention independently of the token's authentication lifetime.
        return session.Expiry > DateTimeOffset.UtcNow ? session : null;
    }
}
