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

    public sealed record Logo(byte[] Bytes, string ContentType);

    private sealed record Entry(Logo? Logo, DateTimeOffset Expiry);

    [GeneratedRegex(@"<link[^>]+rel=[""']?[^""'>]*icon[^""'>]*[""']?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex IconLink { get; }

    [GeneratedRegex(@"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHref { get; }

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly HttpClient _http;
    private readonly ILogger<ProviderLogos> _logger;

    public ProviderLogos(ILogger<ProviderLogos> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _logger = logger;
    }

    /// <summary>
    /// The provider's own logo, or null when it has none the plugin can find. Never
    /// throws: a login page without the right picture is a small thing.
    /// </summary>
    public async Task<Logo?> GetAsync(ProviderConfig provider, CancellationToken ct = default)
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

        Logo? logo = null;
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
    private async Task<Logo?> FindAsync(Uri authority, CancellationToken ct)
    {
        var origin = new Uri(authority.GetLeftPart(UriPartial.Authority));

        // Authentik publishes the brand its users see, including a custom logo.
        var brand = await TextAsync(new Uri(origin, "/api/v3/core/brands/current/"), ct)
            .ConfigureAwait(false);
        if (brand is not null)
        {
            using var document = JsonDocument.Parse(brand);

            foreach (var logoUri in BrandLogos(document.RootElement, origin))
            {
                var branded = await ImageAsync(logoUri, ct).ConfigureAwait(false);
                if (branded is not null)
                {
                    return branded;
                }
            }
        }

        // Anything else: the icon its own login page declares, then the conventional one.
        var html = await TextAsync(origin, ct).ConfigureAwait(false);
        if (html is not null && IconLink.Match(html) is { Success: true } link
            && LinkHref.Match(link.Value) is { Success: true } href
            && Uri.TryCreate(origin, href.Groups[1].Value, out var declared))
        {
            var fromLink = await ImageAsync(declared, ct).ConfigureAwait(false);
            if (fromLink is not null)
            {
                return fromLink;
            }
        }

        return await ImageAsync(new Uri(origin, "/favicon.ico"), ct).ConfigureAwait(false);
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

    private async Task<string?> TextAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<Logo?> ImageAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        var type = response.Content.Headers.ContentType?.MediaType;

        if (!response.IsSuccessStatusCode
            || type is null
            || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > MaxBytes)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return bytes.Length is > 0 and <= MaxBytes ? new Logo(bytes, type) : null;
    }

    public void Dispose() => _http.Dispose();
}
