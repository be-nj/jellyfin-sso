using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Logging;
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
        // One login at a time. The administrator decision below reads every account on the
        // server and the policy write that acts on it happens afterwards; two logins
        // overlapping there could demote the last two administrators at once, each having
        // seen the other as the one that would be left. AccountBookkeeping says why this
        // is a semaphore and not the configuration lock.
        using var turn = await AccountBookkeeping.EnterAsync(ct).ConfigureAwait(false);

        var config = Plugin.Instance!.Configuration;

        RecordSeenGroups(config, identity);

        if (config.SyncExcludedUserIds.Contains(userId))
        {
            _logger.LogInformation(
                "SSO sync skipped for user {Id}: excluded from group sync, permissions left as configured.",
                userId);
            return;
        }

        var matched = GroupMatching.Matching(config, identity);

        WarnAboutNearMisses(config, identity, matched);

        var isAdmin = ResolveAdmin(matched, userId, config);

        // Parsed defensively, not because the admin endpoint lets a bad id through — it
        // refuses one — but because a configuration file is a file: an id edited by hand,
        // or left behind by an older version, would otherwise throw here, and a
        // FormatException from this line is not a type the callback catches. Every login
        // on the server answered 500. A bad id now grants nothing and says so.
        var libraryIds = matched
            .SelectMany(m => m.LibraryIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ParsedLibraryId)
            .OfType<Guid>()
            .ToArray();

        var enableAll = isAdmin; // admins see everything

        _logger.LogInformation(
            "SSO sync for user {Id}: admin={Admin}, libraries={Count}, groups={Groups}",
            userId, isAdmin, libraryIds.Length, Loggable.OneLine(identity.Groups));

        // Only the fields this plugin claims are touched. A policy built from scratch here
        // would quietly reset everything an administrator set elsewhere — blocked tags,
        // remote access, session limits, SyncPlay — on every single login.
        var policy = CurrentPolicy(userId);
        if (policy is null)
        {
            // Nothing is written rather than a blank policy. A fresh UserPolicy() resets
            // every field this plugin does not own to the type's default — IsDisabled,
            // IsHidden, BlockedTags, remote access, session limits — which would mean an
            // SSO login could silently re-enable an account an administrator disabled.
            //
            // The cost of skipping is the other direction, and it is deliberate: a user
            // whose administrator group was taken away at the provider keeps the flag
            // through this login, because the line that clears it is one of the lines that
            // does not run. Of the two, re-enabling a disabled account undoes an explicit
            // decision an administrator made and nothing brings it back, while a retained
            // flag is corrected by the next login that reads a policy. So the sync stops,
            // and the log says both halves rather than only the reassuring one.
            _logger.LogWarning(
                "Skipping the SSO permission sync for user {Id}: the current policy could "
                + "not be read. No libraries, groups or administrator rights were applied "
                + "this login — including any that should have been taken away. The next "
                + "login applies them if the policy can be read by then.",
                userId);
            return;
        }

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
    private UserPolicy? CurrentPolicy(Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            // A user Jellyfin does not know has no settings to preserve, so a fresh policy
            // is the whole truth about it rather than a loss of one.
            return new UserPolicy();
        }

        try
        {
            // Null for an account that exists: its settings are real and unknown, and
            // replacing them with defaults is the failure this guards against.
            return _userManager.GetUserDto(user).Policy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not read the current policy for user {Id}.", userId);
            return null;
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
    /// A mapping that almost matched says nothing about itself in the symptom — the user
    /// simply sees nothing — so it is said here, once per login. The rules for what counts
    /// as "almost" live in <see cref="GroupMatching"/>, because the login gate needs the
    /// same ones.
    /// </summary>
    private void WarnAboutNearMisses(
        PluginConfiguration config, OidcIdentity identity, List<GroupMappingConfig> matched)
    {
        foreach (var (mapping, arrived, reason) in GroupMatching.NearMisses(config, identity, matched))
        {
            _logger.LogWarning(
                "SSO group mapping '{Mapping}' does not match the group '{Arrived}' the "
                + "provider sent: {Reason}, and matching is exact.",
                mapping,
                Loggable.OneLine(arrived),
                reason);
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

    /// <summary>
    /// A configured library id as a Guid, or null with a line saying which entry grants
    /// nothing. A method rather than a conditional in the query above, where it was a
    /// ternary whose second branch was called for its side effect — and needed a cast to
    /// Guid? that the compiler then pointed out was redundant.
    /// </summary>
    private Guid? ParsedLibraryId(string libraryId)
    {
        if (Guid.TryParse(libraryId, out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "A group mapping names '{LibraryId}', which is not a Jellyfin library id. That "
            + "entry grants nothing; correct it on the plugin's configuration page.",
            Loggable.OneLine(libraryId));
        return null;
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
