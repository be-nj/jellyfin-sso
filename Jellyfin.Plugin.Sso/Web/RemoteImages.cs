using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>A picture fetched from somewhere else, and what it says it is.</summary>
public sealed record RemoteImage(byte[] Bytes, string ContentType);

/// <summary>
/// The one place this plugin fetches a picture from an address it did not choose.
///
/// Both callers are in that position: the login button shows the provider's own logo,
/// found at an address read out of the provider's branding, and a profile picture comes
/// from a claim in a token. Neither address is trustworthy, both were guarded separately,
/// and the two sets of guards had already drifted apart — one counted bytes while reading
/// and followed three redirects, the other trusted a header and followed fifty.
///
/// So the rules live here, once:
///
///   * `http` and `https` only, so a `file://` or `data:` address is refused before any
///     request is made;
///   * three redirects, because a chain is the cheapest way to turn one allowed address
///     into a walk around the server's network;
///   * the image types Jellyfin's own pipeline reads, and no others — SVG in particular
///     is a document that can carry script, not a picture;
///   * a byte count kept while the body arrives, because what a response claims about its
///     length, or declines to say, is the sender's choice;
///   * a short timeout, and no exception: a picture that does not arrive is never worth
///     failing anything over.
/// </summary>
public sealed class RemoteImages : IDisposable
{
    /// <summary>What Jellyfin's image pipeline reads, and what a browser will show.</summary>
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/x-icon"] = ".ico",
        ["image/vnd.microsoft.icon"] = ".ico",
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public RemoteImages(ILogger<RemoteImages> logger)
        : this(logger, handler: null)
    {
    }

    /// <summary>Takes a handler so the rules can be tested without a server.</summary>
    internal RemoteImages(ILogger logger, HttpMessageHandler? handler, TimeSpan? timeout = null)
    {
        _logger = logger;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };

        // Six seconds. A profile picture is fetched while somebody is waiting to be let
        // in, so this is time added to a login — and a host that has not answered in six
        // seconds is not going to make the login feel better by answering in ten.
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(6) };
    }

    /// <summary>
    /// Whether this is an address the plugin will fetch at all. Absolute http(s) only: a
    /// claim or a brand document is somebody else's text, and `file:///etc/shadow` is a
    /// perfectly well-formed URI.
    /// </summary>
    public static bool Fetchable(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
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

    /// <summary>The file extension for a type this plugin accepts, or null for one it does not.</summary>
    public static string? ExtensionFor(string? contentType)
        => contentType is not null && Extensions.TryGetValue(contentType, out var extension)
            ? extension
            : null;

    /// <summary>
    /// The picture at <paramref name="uri"/>, or null for anything this plugin will not
    /// use — the wrong type, too large, an error, a host that is not there. Never throws
    /// except when <paramref name="ct"/> is cancelled, which is the caller going away
    /// rather than the picture failing.
    /// </summary>
    public async Task<RemoteImage?> GetAsync(Uri uri, int maxBytes, CancellationToken ct = default)
    {
        try
        {
            // Headers first. Reading the response whole — the default — buffers whatever
            // the host sends before anything looks at its size.
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode
                || ExtensionFor(mediaType) is null
                || response.Content.Headers.ContentLength > maxBytes)
            {
                _logger.LogDebug(
                    "SSO: {Url} is not a picture this plugin will use ({Status}, {Type}).",
                    uri, (int)response.StatusCode, mediaType ?? "no content type");
                return null;
            }

            var bytes = await ReadCappedAsync(response, maxBytes, ct).ConfigureAwait(false);
            return bytes is { Length: > 0 } ? new RemoteImage(bytes, mediaType!) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSO: could not fetch the picture at {Url}.", uri);
            return null;
        }
    }

    /// <summary>
    /// The body, or null once it grows past what was asked for. Counted while it is read:
    /// a response that declares no length — every chunked one — would otherwise arrive in
    /// full before any limit applied to it.
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
