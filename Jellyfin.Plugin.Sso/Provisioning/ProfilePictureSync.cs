using System.Security.Cryptography;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Puts the picture the provider publishes for a person on their Jellyfin account, so a
/// household that already has photos at the identity provider does not set them a second
/// time here.
///
/// The address comes out of a token, which means the server fetches whatever it points at.
/// That is the whole risk of this feature, so the fetch is kept narrow: http(s) only, one
/// redirect-following client with a short timeout, a size cap, and only the image types
/// Jellyfin itself serves. A picture that does not arrive is never worth failing a login
/// over — every path here ends in "leave the account as it was".
/// </summary>
public sealed class ProfilePictureSync
{
    /// <summary>An avatar. Anything larger is not what we came for.</summary>
    internal const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The hash of what was last written for a user. Kept in
    /// memory rather than in the configuration file: the point is to skip the download on
    /// the second login, not to remember it across restarts, and the configuration is not
    /// a place to accumulate one entry per account.
    /// </summary>
    private readonly Dictionary<Guid, string> _applied = [];

    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly RemoteImages _images;
    private readonly ILogger<ProfilePictureSync> _logger;

    public ProfilePictureSync(
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfiguration,
        RemoteImages images,
        ILogger<ProfilePictureSync> logger)
    {
        _userManager = userManager;
        _providerManager = providerManager;
        _serverConfiguration = serverConfiguration;
        _images = images;
        _logger = logger;
    }

    /// <summary>
    /// Fetches <paramref name="pictureUrl"/> and makes it the user's profile picture.
    /// Does nothing at all when the setting is off, when the user is excluded from sync,
    /// when the address is not one we will fetch, or when the same picture is already on
    /// the account.
    /// </summary>
    public async Task SyncAsync(Guid userId, string? pictureUrl, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is not { SyncProfilePictures: true })
        {
            return;
        }

        if (config.SyncExcludedUserIds.Contains(userId))
        {
            _logger.LogDebug("SSO: user {Id} is excluded from sync; leaving their picture alone.", userId);
            return;
        }

        if (!RemoteImages.Fetchable(pictureUrl, out var uri))
        {
            return;
        }

        try
        {
            await ApplyAsync(userId, uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The request went away. That is not a picture failing; saying so in the log
            // every time somebody closes a tab would be noise.
            throw;
        }
        catch (Exception ex)
        {
            // Every other exception, deliberately. The promise this feature makes is that a
            // login which worked is not undone by a picture, and the list this used to
            // name covered the fetch but not Jellyfin's own image pipeline, which is
            // free to throw whatever it likes at a file it does not care for.
            _logger.LogWarning(
                ex, "SSO: could not apply the profile picture for user {Id}; leaving it as it was.", userId);
        }
    }

    private async Task ApplyAsync(Guid userId, Uri uri, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        var picture = await _images.GetAsync(uri, MaxBytes, ct).ConfigureAwait(false);
        if (picture is null)
        {
            return;
        }

        var bytes = picture.Bytes;
        var extension = RemoteImages.ExtensionFor(picture.ContentType)!;

        // Providers hand out the same picture on every login. Writing it again would
        // churn the image cache and the tag the clients hold. The bytes decide that, not
        // the address: a provider that moves its avatars to a new host has not changed
        // anybody's picture.
        var stamp = Convert.ToHexString(SHA256.HashData(bytes));
        lock (_applied)
        {
            if (_applied.TryGetValue(userId, out var last)
                && string.Equals(last, stamp, StringComparison.Ordinal)
                && user.ProfileImage is not null)
            {
                return;
            }
        }

        var path = PicturePath(user, extension);

        // Write the file first. Clearing the old picture and pointing the account at a
        // path before anything is there leaves the account worse than it was if the write
        // then fails — and the write is the step most likely to.
        using (var source = new MemoryStream(bytes))
        {
            await _providerManager.SaveImage(source, picture.ContentType, path).ConfigureAwait(false);
        }

        if (user.ProfileImage is not null && user.ProfileImage.Path != path)
        {
            await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
        }

        user.ProfileImage = new ImageInfo(path);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        lock (_applied)
        {
            _applied[userId] = stamp;
        }

        _logger.LogInformation(
            "SSO: profile picture for user {Id} taken from the provider ({Bytes} bytes).",
            userId, bytes.Length);
    }

    /// <summary>
    /// Where the picture goes: Jellyfin keeps a user's own files in a folder named after
    /// them. The name reaches us from an identity provider, so the finished path is
    /// checked to be under the root it was built from, and a name that leaves it gets a
    /// folder named after the user's id instead.
    ///
    /// Built with <see cref="Path.Join(string?, string?)"/> rather than
    /// <c>Path.Combine</c>: Combine hands back its later argument whole when that
    /// argument is rooted, so a path's safety would rest on remembering which of the two
    /// discards what.
    /// </summary>
    internal string PicturePath(User user, string extension)
    {
        var root = Path.GetFullPath(_serverConfiguration.ApplicationPaths.UserConfigurationDirectoryPath);
        var file = "profile" + extension;
        var own = Path.Join(root, user.Id.ToString("N"), file);

        var name = Path.GetFileName(user.Username);
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return own;
        }

        var inside = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var candidate = Path.GetFullPath(Path.Join(root, name, file));
        return candidate.StartsWith(inside, StringComparison.Ordinal) ? candidate : own;
    }
}
