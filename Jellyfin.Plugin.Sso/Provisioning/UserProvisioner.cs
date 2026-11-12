using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Logging;
using Jellyfin.Plugin.Sso.Oidc;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

public sealed class UserProvisioner
{
    private readonly IUserManager _userManager;
    private readonly ILogger<UserProvisioner> _logger;
    private readonly ISsoUserPersistence _persistence;

    public UserProvisioner(IUserManager userManager, ILogger<UserProvisioner> logger,
        ISsoUserPersistence persistence)
    {
        _userManager = userManager;
        _logger = logger;
        _persistence = persistence;
    }

    /// <summary>
    /// Resolves or JIT-creates the Jellyfin user for the given OIDC identity.
    /// Returns (userId, username) on success.
    /// Throws when a username collision with an existing non-linked account is detected.
    /// </summary>
    /// <summary>
    /// The Jellyfin account this identity is already linked to, or null for one that has
    /// never signed in here. Reads the links and changes nothing, so the login gate can
    /// ask before anything is created.
    /// </summary>
    public static Guid? LinkedUserId(ProviderConfig provider, OidcIdentity identity)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return null;
        }

        // Under the lock, for the same reason as the revoker: a plain List being written
        // by another request throws rather than answering.
        lock (Plugin.ConfigurationLock)
        {
            return config.Links
                .FirstOrDefault(l => l.ProviderId == provider.Id
                    && string.Equals(l.Sub, identity.Sub, StringComparison.Ordinal))
                ?.JellyfinUserId;
        }
    }

    public async Task<(Guid UserId, string Username)> ResolveAsync(
        ProviderConfig provider,
        OidcIdentity identity,
        CancellationToken ct = default)
    {
        // One login at a time, for the same reason as the permission sync: everything
        // below is a question about the server's accounts followed by a change to them,
        // with an await in the middle that a lock cannot span. Two first logins of one
        // identity arriving together would each find no link, and each then ask Jellyfin to
        // create the account.
        using var turn = await AccountBookkeeping.EnterAsync(ct).ConfigureAwait(false);

        var config = Plugin.Instance!.Configuration;

        // 1. Check for an existing link — the only way to match, never by username.
        LinkConfig? existing;
        lock (Plugin.ConfigurationLock)
            existing = config.Links.FirstOrDefault(l =>
                l.ProviderId == provider.Id &&
                string.Equals(l.Sub, identity.Sub, StringComparison.Ordinal));

        if (existing != null)
        {
            var user = _userManager.GetUserById(existing.JellyfinUserId);
            if (user is null)
            {
                _logger.LogWarning(
                    "Link exists for sub={Sub} but Jellyfin user {Id} not found — removing stale link.",
                    Loggable.OneLine(identity.Sub), existing.JellyfinUserId);
                lock (Plugin.ConfigurationLock)
                {
                    config.Links.Remove(existing);
                    Plugin.Instance.SaveConfiguration();
                }
            }
            else
            {
                _logger.LogDebug("SSO login: matched existing user {Name} via link.", user.Username);
                return (user.Id, user.Username);
            }
        }

        // 2. No link — JIT create a new user.
        // Reject if a local user with the same name already exists (could be an admin account).
        var collision = _userManager.GetUserByName(identity.PreferredUsername);
        if (collision is not null)
        {
            _logger.LogWarning(
                "SSO JIT blocked: username '{Name}' already exists. " +
                "To link manually: ProviderId={ProviderId} Sub={Sub} JellyfinUserId={UserId}",
                Loggable.OneLine(identity.PreferredUsername), Loggable.KnownProviderId(provider.Id),
                Loggable.OneLine(identity.Sub), collision.Id);
            throw new InvalidOperationException(
                $"Username '{identity.PreferredUsername}' already exists. " +
                "An administrator must manually link this account.");
        }

        _logger.LogInformation(
            "SSO JIT: creating new Jellyfin user '{Name}'.",
            Loggable.OneLine(identity.PreferredUsername));
        var newUser = await _persistence.CreateAsync(identity.PreferredUsername, ct).ConfigureAwait(false);

        try
        {
            // The first persisted state is already disabled and SSO-only. Activate it
            // with the baseline policy; no login credential exists until linking succeeds.
            await _userManager.UpdatePolicyAsync(newUser.Id, new UserPolicy
            {
                AuthenticationProviderId = typeof(Auth.SsoAuthenticationProvider).FullName,
                PasswordResetProviderId = Auth.SsoAuthenticationProvider.DefaultPasswordResetProviderId,
                EnableAllFolders = false,
                EnabledFolders = [],
                IsAdministrator = false,
            }).ConfigureAwait(false);

            lock (Plugin.ConfigurationLock)
            {
                // An administrator or self-service callback may have linked this identity
                // while account creation was awaiting the database. All writers decide
                // uniqueness under this same lock; the provisional account is removed below.
                if (SelfServiceLinks.Decide(config, provider.Id, identity.Sub, newUser.Id).Outcome
                    != SelfServiceLinkOutcome.Write)
                    throw new LoginRaceException("This identity was linked while its first login was creating an account.");

                config.Links.Add(new LinkConfig
                {
                    ProviderId = provider.Id,
                    Sub = identity.Sub,
                    JellyfinUserId = newUser.Id,
                });

                var newGroups = identity.Groups
                    .Except(config.SeenGroups, StringComparer.Ordinal)
                    .Distinct(StringComparer.Ordinal)
                    .Take(Math.Max(0, PluginConfiguration.MaxSeenGroups - config.SeenGroups.Count))
                    .ToList();

                config.SeenGroups.AddRange(newGroups);
                Plugin.Instance.SaveConfiguration();
            }

            return (newUser.Id, newUser.Username);
        }
        catch
        {
            try
            {
                lock (Plugin.ConfigurationLock)
                {
                    config.Links.RemoveAll(l => l.JellyfinUserId == newUser.Id);
                    Plugin.Instance.SaveConfiguration();
                }
            }
            catch (Exception cleanup)
            {
                _logger.LogError(cleanup, "Could not persist removal of provisional SSO link {Id}.", newUser.Id);
            }
            try
            {
                await _userManager.DeleteUserAsync(newUser.Id).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                // Even if cleanup fails, the first persisted state already uses SSO:
                // no local password can authenticate this orphaned account.
                _logger.LogError(cleanup, "Could not remove provisional SSO account {Id}.", newUser.Id);
            }
            throw;
        }
    }
}
