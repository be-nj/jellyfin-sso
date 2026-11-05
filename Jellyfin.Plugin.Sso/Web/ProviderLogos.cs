using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Sso.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Fetches the logo an identity provider shows on its own login page, so the button in
/// Jellyfin carries the household's branding rather than a stock mark. A provider that
/// has been rebranded — a family's own logo in Authentik — then looks like itself here
/// too.
///
/// The fetch happens on the server, once per provider and then from memory, so a visitor
/// to the login page never reaches out to the identity provider before they have chosen
/// to sign in.
/// </summary>
public sealed partial class ProviderLogos : IDisposable
{
    /// <summary>Refetched after this, so a rebrand arrives without a restart.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    /// <summary>An icon is small. Anything larger is not what we came for.</summary>
    private const int MaxBytes = 256 * 1024;

    private sealed record Entry(RemoteImage? Logo, DateTimeOffset Expiry);

    [GeneratedRegex(@"<link[^>]+rel=[""']?[^""'>]*icon[^""'>]*[""']?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex IconLink { get; }

    [GeneratedRegex(@"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHref { get; }

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly HttpClient _http;
    private readonly RemoteImages _images;
    private readonly ILogger<ProviderLogos> _logger;

    public ProviderLogos(RemoteImages images, ILogger<ProviderLogos> logger)
    {
        // Text only — the brand document and the login page. Pictures go through
        // RemoteImages, which is where the rules for fetching one live.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _images = images;
        _logger = logger;
    }

    /// <summary>
    /// The provider's own logo, or null when it has none the plugin can find. Never
    /// throws: a login page without the right picture is a small thing.
    /// </summary>
    public async Task<RemoteImage?> GetAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.Authority)
            || !Uri.TryCreate(provider.Authority, UriKind.Absolute, out var authority))
        {
            return null;
        }

        var key = $"{provider.Id}|{provider.Authority}";
        if (_cache.TryGetValue(key, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
        {
            return cached.Logo;
        }

        RemoteImage? logo = null;
        try
        {
            logo = await FindAsync(authority, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException or UriFormatException)
        {
            _logger.LogDebug(ex, "No logo found at {Authority}; using the built-in mark.", authority);
        }

        _cache[key] = new Entry(logo, DateTimeOffset.UtcNow.Add(CacheTtl));
        return logo;
    }

    /// <summary>
    /// The brand fields Authentik publishes, in the order they suit a button. Both are the
    /// household's own branding, but only the favicon is meant to be a square mark:
    /// `branding_logo` is the wide lockup from the top of the login page — Authentik's own
    /// default is an icon with the wordmark beside it — and in a mark-sized box that
    /// becomes a smudge.
    /// </summary>
    private static readonly string[] BrandFields = ["branding_favicon", "branding_logo"];

    /// <summary>Three places a provider keeps its logo, in order of how specific they are.</summary>
    private async Task<RemoteImage?> FindAsync(Uri authority, CancellationToken ct)
    {
        var origin = new Uri(authority.GetLeftPart(UriPartial.Authority));

        // Authentik publishes the brand its users see, including a custom logo.
        var brand = await TextAsync(new Uri(origin, "/api/v3/core/brands/current/"), authority, ct)
            .ConfigureAwait(false);
        if (brand is not null)
        {
            using var document = JsonDocument.Parse(brand);

            foreach (var logoUri in BrandLogos(document.RootElement, origin))
            {
                var branded = await ImageAsync(logoUri, authority, ct).ConfigureAwait(false);
                if (branded is not null)
                {
                    return branded;
                }
            }
        }

        // Anything else: the icon its own login page declares, then the conventional one.
        var html = await TextAsync(origin, authority, ct).ConfigureAwait(false);
        if (html is not null && IconLink.Match(html) is { Success: true } link
            && LinkHref.Match(link.Value) is { Success: true } href
            && Uri.TryCreate(origin, href.Groups[1].Value, out var declared))
        {
            var fromLink = await ImageAsync(declared, authority, ct).ConfigureAwait(false);
            if (fromLink is not null)
            {
                return fromLink;
            }
        }

        return await ImageAsync(new Uri(origin, "/favicon.ico"), authority, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The brand images <paramref name="brand"/> actually points at, in the order of
    /// <see cref="BrandFields"/>. A field that is missing, is not text, is empty, or does
    /// not read as an address is not a logo, so it never reaches the caller.
    /// </summary>
    private static IEnumerable<Uri> BrandLogos(JsonElement brand, Uri origin)
        // Anything but an object has no properties to ask for, and asking anyway throws.
        => brand.ValueKind != JsonValueKind.Object
            ? []
            : BrandFields.Select(field => BrandLogo(brand, origin, field)).OfType<Uri>();

    /// <summary>The address <paramref name="field"/> holds, or null if it holds no usable one.</summary>
    private static Uri? BrandLogo(JsonElement brand, Uri origin, string field)
        => brand.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } path
            && Uri.TryCreate(origin, path, out var logoUri)
                ? logoUri
                : null;

    /// <summary>
    /// Fetches a document from the provider. The threat model claims one fetcher for
    /// everything, with three redirects and a body counted as it arrives; this used to be
    /// a bare HttpClient instead — fifty redirects by default, and a Content-Length header
    /// taken on trust. The redirect chain was a second way to leave the configured host.
    /// </summary>
    private async Task<string?> TextAsync(Uri uri, Uri authority, CancellationToken ct)
    {
        // The provider's own address, not the target as its own authority — that made the
        // first-hop check a tautology and waived the rules for the rest of the chain.
        var bytes = await _images
            .GetBytesAsync(uri, MaxBytes, authority, allowPrivateNetworks: false, ct: ct)
            .ConfigureAwait(false);

        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Fetches a logo, but only from the provider's own host. The address comes out of the
    /// provider's brand document or its login page, where an absolute URL used to be taken
    /// at face value — Uri.TryCreate leaves one alone instead of resolving it against the
    /// authority — so whoever controlled that document chose which host the server asked
    /// next, and the answer came back out through an endpoint anybody can load.
    /// </summary>
    private Task<RemoteImage?> ImageAsync(Uri uri, Uri authority, CancellationToken ct)
    {
        if (!string.Equals(uri.Host, authority.Host, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "SSO: ignoring a logo at {Url}: it is not on {Host}, the provider's own host.",
                uri, authority.Host);
            return Task.FromResult<RemoteImage?>(null);
        }

        // Not allowPrivateNetworks. That flag short-circuits the address rules before
        // anything is resolved, so it waived them for every redirect hop as well — and
        // the host check above only ever looks at the first URL. A provider on the LAN is
        // covered by the authority exemption instead, which is its exact address.
        return _images.GetAsync(uri, MaxBytes, authority, allowPrivateNetworks: false, ct);
    }

    public void Dispose() => _http.Dispose();
}
