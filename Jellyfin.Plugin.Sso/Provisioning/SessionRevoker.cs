using Jellyfin.Plugin.Sso.Auth;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Ends the Jellyfin sessions of the account behind an OIDC identity. Until this existed,
/// a session the identity provider had ended lived on until the access token was used
/// against a Jellyfin that no longer trusted it — which is to say, until the next login.
/// </summary>
public sealed class SessionRevoker
{
    private readonly ISessionManager _sessions;
    private readonly ILogger<SessionRevoker> _logger;

    public SessionRevoker(ISessionManager sessions, ILogger<SessionRevoker> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Revokes every token of one Jellyfin account. Used where the account is already
    /// known and no identity has to be matched — unlinking, where leaving the sessions
    /// running would leave tokens nobody can end.
    ///
    /// The account and every identity linked to it are marked revoked before anything is
    /// awaited, so a cancelled request still leaves every pending credential dead. The
    /// sweep itself waits only for another sweep of the same account, never for a login.
    /// </summary>
    public async Task RevokeUserAsync(Guid userId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        LoginLifetime.Revoke(userId);
        lock (Plugin.ConfigurationLock)
        {
            foreach (var link in Plugin.Instance?.Configuration.Links
                .Where(l => l.JellyfinUserId == userId) ?? [])
                LoginLifetime.Revoke(link.ProviderId, link.Sub);
        }

        // Not cancellable from here on: the versions above already refuse what is pending,
        // and a sweep abandoned halfway would leave tokens minted before them running.
        using (await LoginLifetime.SweepingAsync(userId).ConfigureAwait(false))
            await _sessions.RevokeUserTokens(userId, null).ConfigureAwait(false);

        _logger.LogInformation("SSO ended every session of user {UserId}.", userId);
    }

    /// <summary>
    /// Sweeps one account's tokens again without recording a new revocation. For a place
    /// that minted a token, then found a revocation it could not have seen in time.
    /// </summary>
    internal async Task SweepAsync(Guid userId)
    {
        using (await LoginLifetime.SweepingAsync(userId).ConfigureAwait(false))
            await _sessions.RevokeUserTokens(userId, null).ConfigureAwait(false);

        _logger.LogInformation(
            "SSO ended the sessions of user {UserId} again: a token was minted while a "
            + "revocation of that account was under way.", userId);
    }

    /// <summary>
    /// Revokes every token of every Jellyfin account linked to <paramref name="sub"/> on
    /// the given provider, and returns how many accounts that was. Normally one; zero when
    /// the identity was never linked here, which is not an error — a provider broadcasts a
    /// logout to every application the person was signed into, including the ones that
    /// never saw them. The identity is marked revoked first either way, so a first login
    /// still on its way to creating the link is refused too.
    /// </summary>
    public async Task<int> RevokeAsync(string providerId, string sub, CancellationToken ct = default)
    {
        LoginLifetime.Revoke(providerId, sub);
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return 0;

        // Under the lock and materialised: Links is a plain List, writers add and remove
        // under this lock, and enumerating it while that happens throws "Collection was
        // modified" — which the callback above turns into a misleading refusal for a
        // login that was fine.
        List<Guid> userIds;
        lock (Plugin.ConfigurationLock)
        {
            userIds = config.Links
                .Where(l => string.Equals(l.ProviderId, providerId, StringComparison.Ordinal)
                    && string.Equals(l.Sub, sub, StringComparison.Ordinal))
                .Select(l => l.JellyfinUserId)
                .Distinct()
                .ToList();
        }

        foreach (var userId in userIds)
        {
            // No current token to spare: the request carrying this came from the provider,
            // not from one of the user's own devices. And no cancellation: a provider that
            // stops waiting for the answer has still told us the session is over.
            await RevokeUserAsync(userId, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Back-channel logout: revoked the tokens of Jellyfin user {UserId}.", userId);
        }

        return userIds.Count;
    }
}
