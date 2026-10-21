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

        WarnAboutCaseOnlyMisses(config, identity, matched);

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

        // Only the fields this plugin claims are touched. A policy built from scratch here
        // would quietly reset everything an administrator set elsewhere — blocked tags,
        // remote access, session limits, SyncPlay — on every single login.
        var policy = CurrentPolicy(userId);

        policy.IsAdministrator = isAdmin;
        policy.EnableAllFolders = enableAll;
        policy.EnabledFolders = enableAll ? [] : libraryIds;
        policy.AuthenticationProviderId = typeof(Auth.SsoAuthenticationProvider).FullName!;
        policy.PasswordResetProviderId =
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider";

        // Playback — always allowed for SSO users.
        policy.EnableMediaPlayback = true;
        policy.EnableAudioPlaybackTranscoding = true;
        policy.EnableVideoPlaybackTranscoding = true;
        policy.EnablePlaybackRemuxing = true;

        // Group-derived, most permissive match wins.
        policy.EnableLiveTvAccess = isAdmin || matched.Any(m => m.EnableLiveTvAccess);
        policy.EnableLiveTvManagement = isAdmin || matched.Any(m => m.EnableLiveTvManagement);
        policy.EnableContentDownloading = isAdmin || matched.Any(m => m.EnableContentDownloading);
        // Deletion is the one that removes files, so an administrator does not get it
        // handed to them here — Jellyfin's own screen decides that, and an operator who
        // left it off meant it. A group that grants it still grants it to anybody.
        var deletedBefore = policy.EnableContentDeletion;
        policy.EnableContentDeletion = matched.Any(m => m.EnableContentDeletion);
        policy.EnableCollectionManagement = isAdmin || matched.Any(m => m.EnableCollectionManagement);
        policy.EnableSubtitleManagement = isAdmin || matched.Any(m => m.EnableSubtitleManagement);
        policy.EnableLyricManagement = isAdmin || matched.Any(m => m.EnableLyricManagement);
        policy.MaxParentalRating = isAdmin ? null : ResolveParentalRating(matched);

        // Only when this sync is the thing that took deletion away: a folder list left
        // behind would keep granting in those folders exactly what the switch just
        // removed everywhere. A user who never had deletion keeps whatever an
        // administrator put in that list, because the plugin does not own it.
        if (deletedBefore && !policy.EnableContentDeletion)
        {
            policy.EnableContentDeletionFromFolders = [];
        }

        await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
    }

    /// <summary>
    /// The user's policy as it stands, so a sync edits it rather than replacing it. A user
    /// Jellyfin no longer knows, or a server that cannot produce the DTO, falls back to a
    /// fresh policy — the login still has to end with a usable account.
    /// </summary>
    private UserPolicy CurrentPolicy(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return new UserPolicy();
        }

        try
        {
            return _userManager.GetUserDto(user).Policy ?? new UserPolicy();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not read the current policy for user {Id}; writing a fresh one.", userId);
            return new UserPolicy();
        }
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

    /// <summary>
    /// Matching is exact, because "Media" and "media" can be two different groups at the
    /// provider and a mapping that silently covered both would hand out access nobody
    /// granted. A mapping that differs from an arriving group only in case is almost
    /// always a typo, though, and the symptom — the user sees nothing — says nothing about
    /// the cause. So it is said here, once per login.
    /// </summary>
    private void WarnAboutCaseOnlyMisses(
        PluginConfiguration config, OidcIdentity identity, List<GroupMappingConfig> matched)
    {
        foreach (var mapping in config.GroupMappings.Except(matched))
        {
            var nearMiss = identity.Groups.FirstOrDefault(
                group => string.Equals(group, mapping.Group, StringComparison.OrdinalIgnoreCase));

            if (nearMiss is not null)
            {
                _logger.LogWarning(
                    "SSO group mapping '{Mapping}' does not match the group '{Arrived}' the "
                    + "provider sent: the names differ only in case, and matching is exact.",
                    mapping.Group,
                    nearMiss);
            }
        }
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

    /// <summary>
    /// Remembers group names for the admin UI's autocomplete. Capped, because the list
    /// comes from whatever the provider sends and is written to the configuration file.
    /// </summary>
    private static void RecordSeenGroups(PluginConfiguration config, OidcIdentity identity)
    {
        lock (Plugin.ConfigurationLock)
        {
            var newGroups = identity.Groups
                .Except(config.SeenGroups, StringComparer.Ordinal)
                .Distinct(StringComparer.Ordinal)
                .Take(Math.Max(0, PluginConfiguration.MaxSeenGroups - config.SeenGroups.Count))
                .ToList();

            if (newGroups.Count == 0)
            {
                return;
            }

            config.SeenGroups.AddRange(newGroups);
            Plugin.Instance!.SaveConfiguration();
        }
    }
}
