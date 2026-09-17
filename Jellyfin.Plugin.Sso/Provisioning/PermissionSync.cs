using Jellyfin.Plugin.Sso.Oidc;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Declarative sync: overwrites the Jellyfin user's library access + admin flag
/// from the current IdP groups every login. IdP groups are the single source of truth.
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

        var matchedMappings = config.GroupMappings
            .Where(m => identity.Groups.Contains(m.Group, StringComparer.Ordinal))
            .ToList();

        var isAdmin = matchedMappings.Any(m => m.IsAdministrator);

        var libraryIds = matchedMappings
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
            // Live TV + download — disabled by default; Phase 2 will derive from group mappings.
            EnableLiveTvAccess = false,
            EnableLiveTvManagement = false,
            EnableContentDownloading = false,
        }).ConfigureAwait(false);

        // Record any new groups seen for autocomplete.
        var newGroups = identity.Groups.Except(config.SeenGroups, StringComparer.Ordinal).ToList();
        if (newGroups.Count > 0)
        {
            config.SeenGroups.AddRange(newGroups);
            Plugin.Instance.SaveConfiguration();
        }
    }
}
