using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>Database operations which IUserManager cannot perform atomically.</summary>
public interface ISsoUserPersistence
{
    Task<User> CreateAsync(string username, CancellationToken ct);
    Task SetProfileImageAsync(Guid userId, string path, CancellationToken ct);
}
