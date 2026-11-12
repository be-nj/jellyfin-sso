using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Auth;
using MediaBrowser.Controller.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Uses tracked, narrowly scoped database writes. Creating through IUserManager first
/// would commit a passwordless local account; updating a detached User for an avatar
/// would overwrite unrelated permissions. Neither operation is safe for these callers.
/// </summary>
public sealed class SsoUserPersistence(
    IDbContextFactory<JellyfinDbContext> contexts,
    IEventManager events,
    ILogger<SsoUserPersistence> logger) : ISsoUserPersistence
{
    public async Task<User> CreateAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 255
            || username is "." or ".."
            || !Regex.IsMatch(username, @"^(?!\s)[\w \-'._@+]+(?<!\s)$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid Jellyfin username.", nameof(username));

        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var normalized = username.ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct).ConfigureAwait(false))
            throw new LoginRaceException("That Jellyfin username was taken while the account was being created.");

        var user = new User(username, typeof(SsoAuthenticationProvider).FullName!,
            SsoAuthenticationProvider.DefaultPasswordResetProviderId)
        {
            InternalId = (await db.Users.Select(u => (long?)u.InternalId)
                .MaxAsync(ct).ConfigureAwait(false) ?? 0) + 1,
        };
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        user.SetPermission(PermissionKind.IsDisabled, true);
        user.SetPermission(PermissionKind.IsAdministrator, false);
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            // The unique name index, or the internal id, taken by an account created
            // between the check above and this write.
            throw new LoginRaceException("A Jellyfin account was created at the same moment.", ex);
        }

        // A subscriber failure must not hide a successfully persisted account from the
        // caller, which needs its ID to finish provisioning or clean it up.
        try
        {
            await events.PublishAsync(new UserCreatedEventArgs(user)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A user-created subscriber failed for SSO account {Id}.", user.Id);
        }
        return user;
    }

    public async Task SetProfileImageAsync(Guid userId, string path, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var user = await db.Users.Include(u => u.ProfileImage)
            .SingleOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null)
            return;

        if (user.ProfileImage is { } image)
        {
            image.Path = path;
            image.LastModified = DateTime.UtcNow;
        }
        else
        {
            user.ProfileImage = new ImageInfo(path);
        }

        // Only changed image fields/the new image are persisted. No detached account,
        // permissions, preferences or authentication settings are written back.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
