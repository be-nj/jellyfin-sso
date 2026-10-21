using System.Security.Cryptography;
using Jellyfin.Database.Implementations.Entities;
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
public sealed class ProfilePictureSync : IDisposable
{
    /// <summary>An avatar. Anything larger is not what we came for.</summary>
    internal const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>What Jellyfin's own image pipeline reads, and nothing else.</summary>
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
    };

    /// <summary>
    /// What was last written for a user, as address plus the hash of the bytes. Kept in
    /// memory rather than in the configuration file: the point is to skip the download on
    /// the second login, not to remember it across restarts, and the configuration is not
    /// a place to accumulate one entry per account.
    /// </summary>
    private readonly Dictionary<Guid, string> _applied = [];

    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly ILogger<ProfilePictureSync> _logger;
    private readonly HttpClient _http;

    public ProfilePictureSync(
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfiguration,
        ILogger<ProfilePictureSync> logger)
        : this(userManager, providerManager, serverConfiguration, logger, handler: null)
    {
    }

    /// <summary>Takes a handler so the fetching rules can be tested without a server.</summary>
    internal ProfilePictureSync(
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfiguration,
        ILogger<ProfilePictureSync> logger,
        HttpMessageHandler? handler)
    {
        _userManager = userManager;
        _providerManager = providerManager;
        _serverConfiguration = serverConfiguration;
        _logger = logger;
        // Three redirects, not the fifty a default HttpClient follows: the address comes
        // from a token, and a chain of redirects is the cheapest way to turn one allowed
        // address into a walk around somebody's network.
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
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

        if (!Fetchable(pictureUrl, out var uri))
        {
            return;
        }

        try
        {
            await ApplyAsync(userId, uri, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Every exception, deliberately. The promise this feature makes is that a
            // login which worked is not undone by a picture, and the list this used to
            // name covered the fetch but not Jellyfin's own image pipeline, which is
            // free to throw whatever it likes at a file it does not care for.
            _logger.LogWarning(
                ex, "SSO: could not apply the profile picture for user {Id}; leaving it as it was.", userId);
        }
    }

    /// <summary>
    /// Only absolute http(s) addresses are fetched. A `file://` or `data:` picture claim
    /// would otherwise have the server read its own disk on behalf of whoever can set
    /// that claim.
    /// </summary>
    internal static bool Fetchable(string? pictureUrl, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(pictureUrl)
            || !Uri.TryCreate(pictureUrl, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private async Task ApplyAsync(Guid userId, Uri uri, CancellationToken ct)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return;
        }

        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode
            || mediaType is null
            || !Extensions.TryGetValue(mediaType, out var extension)
            || response.Content.Headers.ContentLength > MaxBytes)
        {
            _logger.LogDebug(
                "SSO: the picture at {Url} is not one this plugin will use ({Status}, {Type}).",
                uri, (int)response.StatusCode, mediaType ?? "no content type");
            return;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return;
        }

        // Providers hand out the same picture at the same address on every login. Writing
        // it again would churn the image cache and the tag the clients use for it.
        var stamp = $"{uri}|{Convert.ToHexString(SHA256.HashData(bytes))}";
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

        if (user.ProfileImage is not null)
        {
            await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
        }

        user.ProfileImage = new ImageInfo(path);

        using (var source = new MemoryStream(bytes))
        {
            await _providerManager.SaveImage(source, mediaType, path).ConfigureAwait(false);
        }

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

        var candidate = Path.GetFullPath(Path.Join(root, name, file));
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : own;
    }

    public void Dispose() => _http.Dispose();
}
