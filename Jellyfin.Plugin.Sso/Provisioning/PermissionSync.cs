using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Declarative sync: overwrites the Jellyfin user's library access, playback permissions
/// and admin flag from the current IdP groups every login. IdP groups are the single
/// source of truth, which means this also revokes what the groups no longer grant.
///
/// Two deliberate exceptions keep an administrator from locking themselves out of their
/// own server: users listed in SyncExcludedUserIds are left alone entirely, and the last
/// remaining administrator is never demoted.
/// </summary>
public sealed class PermissionSync
{
    private readonly IUserManager _userManager;
    private readonly ILogger<PermissionSync> _logger;

    public PermissionSync(IUserManager userManager, ILogger<PermissionSync> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public async Task SyncAsync(Guid userId, OidcIdentity identity, CancellationToken ct = default)
    {
        var config = Plugin.Instance!.Configuration;

        RecordSeenGroups(config, identity);

        if (config.SyncExcludedUserIds.Contains(userId))
        {
            _logger.LogInformation(
                "SSO sync skipped for user {Id}: excluded from group sync, permissions left as configured.",
                userId);
            return;
        }

        var matched = config.GroupMappings
            .Where(m => identity.Groups.Contains(m.Group, StringComparer.Ordinal))
            .ToList();

        var isAdmin = ResolveAdmin(matched, userId, config);

        var libraryIds = matched
            .SelectMany(m => m.LibraryIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => Guid.ParseExact(id, "N"))
            .ToArray();

        var enableAll = isAdmin; // admins see everything

        _logger.LogInformation(
            "SSO sync for user {Id}: admin={Admin}, libraries={Count}, groups={Groups}",
            userId, isAdmin, libraryIds.Length, string.Join(", ", identity.Groups));

        await _userManager.UpdatePolicyAsync(userId, new UserPolicy
        {
            IsAdministrator = isAdmin,
            EnableAllFolders = enableAll,
            EnabledFolders = enableAll ? [] : libraryIds,
            AuthenticationProviderId = typeof(Auth.SsoAuthenticationProvider).FullName,
            PasswordResetProviderId = "Emby.Server.Implementations.Library.DefaultPasswordResetProvider",
            // Playback — always allowed for SSO users.
            EnableMediaPlayback = true,
            EnableAudioPlaybackTranscoding = true,
            EnableVideoPlaybackTranscoding = true,
            EnablePlaybackRemuxing = true,
            // Group-derived, most permissive match wins.
            EnableLiveTvAccess = isAdmin || matched.Any(m => m.EnableLiveTvAccess),
            EnableLiveTvManagement = isAdmin || matched.Any(m => m.EnableLiveTvManagement),
            EnableContentDownloading = isAdmin || matched.Any(m => m.EnableContentDownloading),
            MaxParentalRating = isAdmin ? null : ResolveParentalRating(matched),
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Admin comes from the groups, unless that would leave the server without an
    /// administrator or the operator asked for admins to be kept.
    /// </summary>
    private bool ResolveAdmin(List<GroupMappingConfig> matched, Guid userId, PluginConfiguration config)
    {
        if (matched.Any(m => m.IsAdministrator))
        {
            return true;
        }

        if (!IsAdministrator(_userManager.GetUserById(userId)))
        {
            return false;
        }

        if (config.PreserveAdminPermissions)
        {
            _logger.LogInformation(
                "Keeping administrator for user {Id}: no admin group matched, but PreserveAdminPermissions is on.",
                userId);
            return true;
        }

        if (OtherAdministratorsExist(userId))
        {
            return false;
        }

        _logger.LogWarning(
            "Keeping administrator for user {Id}: no admin group matched, but this is the only "
            + "administrator left. Check the group mapping — demoting them would lock everyone out.",
            userId);
        return true;
    }

    /// <summary>Null means no limit; a group without a limit lifts it for the whole user.</summary>
    private static int? ResolveParentalRating(List<GroupMappingConfig> matched)
    {
        if (matched.Count == 0 || matched.Any(m => m.MaxParentalRating is null))
        {
            return null;
        }

        return matched.Max(m => m.MaxParentalRating);
    }

    private bool OtherAdministratorsExist(Guid userId)
        => (_userManager.GetUsers() ?? [])
            .Any(u => u.Id != userId && IsAdministrator(u));

    private static bool IsAdministrator(User? user)
        => user is not null && user.HasPermission(PermissionKind.IsAdministrator);

    /// <summary>Remembers group names for the admin UI's autocomplete.</summary>
    private static void RecordSeenGroups(PluginConfiguration config, OidcIdentity identity)
    {
        var newGroups = identity.Groups.Except(config.SeenGroups, StringComparer.Ordinal).ToList();
        if (newGroups.Count > 0)
        {
            config.SeenGroups.AddRange(newGroups);
            Plugin.Instance!.SaveConfiguration();
        }
    }
}
