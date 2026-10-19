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
    /// Revokes every token of every Jellyfin account linked to <paramref name="sub"/> on
    /// the given provider, and returns how many accounts that was. Normally one; zero when
    /// the identity was never linked here, which is not an error — a provider broadcasts a
    /// logout to every application the person was signed into, including the ones that
    /// never saw them.
    /// </summary>
    public async Task<int> RevokeAsync(string providerId, string sub, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return 0;

        var userIds = config.Links
            .Where(l => string.Equals(l.ProviderId, providerId, StringComparison.Ordinal)
                && string.Equals(l.Sub, sub, StringComparison.Ordinal))
            .Select(l => l.JellyfinUserId)
            .Distinct()
            .ToList();

        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();

            // No current token to spare: the request carrying this came from the provider,
            // not from one of the user's own devices.
            await _sessions.RevokeUserTokens(userId, null).ConfigureAwait(false);

            _logger.LogInformation(
                "Back-channel logout: revoked the tokens of Jellyfin user {UserId}.", userId);
        }

        return userIds.Count;
    }
}
