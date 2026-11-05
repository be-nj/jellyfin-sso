using Jellyfin.Plugin.Sso.Logging;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Removing an account's identity links, and ending its sessions with them.
///
/// Two endpoints do this — a user unlinking themselves and an administrator doing it on
/// the Links tab — and the order of the two steps is the whole of the rule, so it lives
/// here rather than twice.
///
/// Back-channel logout finds an account through its link and nothing else. Remove the link
/// first and the sessions become unreachable: the provider can no longer end them, the
/// plugin can no longer find them, and Jellyfin does not expire its tokens. A revocation
/// that fails after the link is gone therefore leaves a session that nothing can close,
/// and the retry answers "no link found" — which is how the first version of this read.
/// Both endpoints had the revocation call; neither could survive its failure.
///
/// So: ask whether there is anything to unlink, end the sessions, remove the links, end
/// them again. Nothing is removed until a revocation has succeeded, so a failure or a
/// cancelled request leaves a state the caller can retry. The second revocation closes the
/// gap between the two — a login that completed in that moment would otherwise keep a
/// token nobody can reach — and it runs without the request's cancellation token, because
/// by then abandoning the work halfway is the only outcome worth avoiding.
/// </summary>
internal sealed class Unlinking
{
    private readonly SessionRevoker _revoker;
    private readonly ILogger _logger;

    internal Unlinking(SessionRevoker revoker, ILogger logger)
    {
        _revoker = revoker;
        _logger = logger;
    }

    /// <summary>
    /// Unlinks one account, from one provider or from all of them, and ends its sessions.
    /// False when the account had no such link, in which case nothing was changed and no
    /// session was ended.
    /// </summary>
    internal async Task<bool> RemoveAsync(
        Guid userId, string? providerId, CancellationToken ct)
    {
        if (!HasLink(userId, providerId))
        {
            return false;
        }

        // Before anything is removed, so that a failure here changes nothing.
        await _revoker.RevokeUserAsync(userId, ct).ConfigureAwait(false);

        var removed = Remove(userId, providerId);
        if (removed == 0)
        {
            // Another request got there first. Its own revocation has run or will run; the
            // sessions this one ended were ended either way.
            return false;
        }

        // And again, for a session that began while the link was still there.
        await _revoker.RevokeUserAsync(userId, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Unlinked {Count} identity link(s) of user {UserId}{Provider}; their sessions "
            + "were ended.",
            removed,
            userId,
            providerId is null ? string.Empty : $" for provider {Loggable.KnownProviderId(providerId)}");

        return true;
    }

    private static bool HasLink(Guid userId, string? providerId)
    {
        var config = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigurationLock)
        {
            return config.Links.Any(l => Matches(l.JellyfinUserId, l.ProviderId, userId, providerId));
        }
    }

    private static int Remove(Guid userId, string? providerId)
    {
        var config = Plugin.Instance!.Configuration;
        lock (Plugin.ConfigurationLock)
        {
            var removed = config.Links.RemoveAll(
                l => Matches(l.JellyfinUserId, l.ProviderId, userId, providerId));

            if (removed > 0)
            {
                Plugin.Instance.SaveConfiguration();
            }

            return removed;
        }
    }

    private static bool Matches(
        Guid linkUserId, string linkProviderId, Guid userId, string? providerId)
        => linkUserId == userId
            && (providerId is null
                || string.Equals(linkProviderId, providerId, StringComparison.Ordinal));
}
