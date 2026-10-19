using System.Globalization;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// The mark that goes on a provider's login button. A plain button says "Login with
/// Authentik" and looks like every other button; the mark is what people recognise before
/// they read anything.
///
/// The paths come from Simple Icons (CC0). They are served from this plugin rather than a
/// CDN so that a login page on a LAN with no internet still renders them, and so that the
/// login page makes no request to anyone else.
/// </summary>
public static class ProviderIcons
{
    /// <summary>
    /// A provider's mark. <paramref name="Colour"/> fills the mark itself; several of
    /// these marks are white, so <paramref name="Brand"/> carries the colour the provider
    /// is recognised by, which is what a branded button is painted in.
    /// </summary>
    private readonly record struct Icon(string Colour, string Brand, string Path);

    /// <summary>The mark used when nothing matches: the plugin's own.</summary>
    private static readonly Icon Fallback = new(
        "#00A4DC",
        "#00A4DC",
        "M2.4 12h6.4M2.4 6.6c3 0 2.6 5.4 5.6 5.4M2.4 17.4c3 0 2.6-5.4 5.6-5.4");

    private static readonly Dictionary<string, Icon> Known = new(StringComparer.Ordinal)
    {
        ["authentik"] = new Icon("#FD4B2D", "#FD4B2D", "M13.96 9.01h-.84V7.492h-1.234v3.663H5.722c.34.517.538.982.538 1.152 0 .46-1.445 3.059-3.197 3.059C.8 15.427-.745 12.8.372 10.855a3.062 3.062 0 0 1 2.691-1.606c1.04 0 1.971.915 2.557 1.755V6.577a3.773 3.773 0 0 1 3.77-3.769h10.84C22.31 2.808 24 4.5 24 6.577v10.845a3.773 3.773 0 0 1-3.77 3.769h-1.6V17.5h-7.64v3.692h-1.6a3.773 3.773 0 0 1-3.77-3.769v-3.41h12.114v-6.52h-1.59v.893h-.84v-.893H13.96v1.516Zm-9.956 1.845c-.662-.703-1.578-.544-2.209 0-2.105 2.054 1.338 5.553 3.302 1.447a5.395 5.395 0 0 0-1.093-1.447Z"),
        ["keycloak"] = new Icon("#FFFFFF", "#4D4D4D", "m18.742 1.182-12.493.002C4.155 4.784 2.079 8.393 0 12.002c2.071 3.612 4.162 7.214 6.252 10.816l12.49-.004 3.089-5.404h2.158v-.002H24L23.996 6.59h-2.168zM8.327 4.792h2.081l1.04 1.8-3.12 5.413 3.117 5.403-1.035 1.81H8.327a2047.566 2047.566 0 0 0-4.168-7.204C5.547 9.606 6.937 7.2 8.327 4.792Zm6.241 0 2.086.003c1.393 2.405 2.78 4.813 4.166 7.222l-4.167 7.2h-2.08c-.382-.562-1.038-1.808-1.038-1.808l3.123-5.405-3.124-5.413z"),
        ["authelia"] = new Icon("#5C9CE6", "#5C9CE6", "M10.624.191c-.324.023-.655.011-.97.073-2.883.568-5.244 2.01-7.074 4.3C1.42 6.014.624 7.652.243 9.48c-.25 1.2-.294 2.402-.191 3.627.237 2.832 1.377 5.233 3.345 7.26.332.341.775.348 1.1.055.39-.35.442-.735.124-1.149-.271-.353-.588-.671-.85-1.03-1.625-2.208-2.343-4.674-2.04-7.411.305-2.773 1.6-5.022 3.723-6.795 1.517-1.267 3.288-1.983 5.248-2.24.272-.037.552-.083.803-.186.32-.132.478-.564.4-.923-.085-.384-.355-.483-.702-.477-.194.004-.387 0-.58 0v-.02zm4.639 3.41c-.312-.008-.582.197-.722.512-.176.4-.027.854.367 1.096.123.075.256.134.385.199 1.485.737 2.619 1.834 3.319 3.342.257.554.423 1.15.636 1.726.162.441.509.656.92.579.425-.08.67-.392.668-.952-.016-.081-.035-.261-.082-.435-.535-1.97-1.579-3.616-3.21-4.847-.598-.453-1.28-.812-1.958-1.14a.782.782 0 0 0-.323-.08zm-6.939.23a.903.903 0 0 0-.492.144c-.648.388-1.325.764-1.88 1.266-2.323 2.098-3.262 4.755-2.905 7.84.55 4.74 4.6 8.021 8.9 7.904l.868-.001a.99.99 0 0 0 .109-.007c2.491-.312 4.58-1.387 6.146-3.36.488-.615.86-1.336 1.2-2.049.248-.523-.032-.982-.56-1.12-.38-.1-.72.112-.974.509-.473.737-.89 1.54-1.491 2.16-1.479 1.52-3.322 2.28-5.466 2.199-2.405-.091-4.337-1.127-5.758-3.056-1.088-1.475-1.49-3.168-1.32-4.984.184-1.987 1.038-3.644 2.57-4.927.423-.354.908-.633 1.365-.946.458-.314.568-.715.314-1.15-.153-.262-.371-.413-.626-.422zm3.417 2.167C8.724 6.056 6.02 8.625 6 12 5.98 15.29 8.712 18.017 12 18.02c3.339.005 6.078-2.618 6.062-6.025C18.048 8.45 15.192 5.911 12.034 6a5.726 5.726 0 0 0-.293-.002zm10.752 1.164a.91.91 0 0 0-.137.002c-.404.033-.642.295-.74.646-.053.187-.01.432.064.62a9.546 9.546 0 0 1 .648 3.753 9.994 9.994 0 0 1-1.32 4.76c-1.617 2.84-4.04 4.53-7.243 5.12-.343.063-.694.095-1.033.174-.434.103-.688.473-.641.882.044.38.34.646.769.684.132.012.265.002.398.002l-.001-.003c.145 0 .29.005.434-.002.084-.004.167-.028.25-.04 1.847-.282 3.518-.99 5.037-2.068 1.708-1.213 3.001-2.78 3.897-4.665 1.1-2.317 1.375-4.752.906-7.264-.13-.694-.354-1.376-.584-2.046-.125-.362-.389-.537-.704-.555zm-10.449.862c.13 0 .261.016.392.05.708.18 1.16.787 1.158 1.553.027.504-.211.888-.588 1.183-.207.163-.236.32-.176.558.264 1.052.52 2.107.763 3.164.13.568-.019.872-.522 1.174-.675.404-1.666.354-2.29-.13-.315-.244-.427-.573-.331-.966.252-1.031.494-2.064.762-3.09.086-.33.041-.554-.225-.794-.581-.523-.682-1.253-.317-1.903a1.57 1.57 0 0 1 1.374-.8z"),
        ["google"] = new Icon("#FFFFFF", "#4285F4", "M12.48 10.92v3.28h7.84c-.24 1.84-.853 3.187-1.787 4.133-1.147 1.147-2.933 2.4-6.053 2.4-4.827 0-8.6-3.893-8.6-8.72s3.773-8.72 8.6-8.72c2.6 0 4.507 1.027 5.907 2.347l2.307-2.307C18.747 1.44 16.133 0 12.48 0 5.867 0 .307 5.387.307 12s5.56 12 12.173 12c3.573 0 6.267-1.173 8.373-3.36 2.16-2.16 2.84-5.213 2.84-7.667 0-.76-.053-1.467-.173-2.053H12.48z"),
        ["okta"] = new Icon("#FFFFFF", "#007DC1", "M12 0C5.389 0 0 5.35 0 12s5.35 12 12 12 12-5.35 12-12S18.611 0 12 0zm0 18c-3.325 0-6-2.675-6-6s2.675-6 6-6 6 2.675 6 6-2.675 6-6 6z"),
        ["auth0"] = new Icon("#FFFFFF", "#EB5424", "M21.98 7.448L19.62 0H4.347L2.02 7.448c-1.352 4.312.03 9.206 3.815 12.015L12.007 24l6.157-4.552c3.755-2.81 5.182-7.688 3.815-12.015l-6.16 4.58 2.343 7.45-6.157-4.597-6.158 4.58 2.358-7.433-6.188-4.55 7.63-.045L12.008 0l2.356 7.404 7.615.044z"),
    };

    /// <summary>
    /// Which mark a provider gets, from its display name and issuer. Nothing is
    /// configured: a provider called "Authentik", or one issuing from auth.example.com
    /// behind Authentik, is recognised either way.
    /// </summary>
    public static string SlugFor(string? name, string? authority)
    {
        var haystack = ((name ?? string.Empty) + " " + (authority ?? string.Empty)).ToLowerInvariant();

        if (haystack.Contains("authentik", StringComparison.Ordinal)) return "authentik";
        if (haystack.Contains("keycloak", StringComparison.Ordinal)) return "keycloak";
        if (haystack.Contains("authelia", StringComparison.Ordinal)) return "authelia";
        if (haystack.Contains("okta", StringComparison.Ordinal)) return "okta";
        if (haystack.Contains("auth0", StringComparison.Ordinal)) return "auth0";
        if (haystack.Contains("google", StringComparison.Ordinal)
            || haystack.Contains("accounts.google.com", StringComparison.Ordinal)) return "google";

        return "generic";
    }

    /// <summary>The colour of a provider's mark, for styling the button beside it.</summary>
    public static string ColourFor(string? name, string? authority)
        => Known.TryGetValue(SlugFor(name, authority), out var icon) ? icon.Colour : Fallback.Colour;

    /// <summary>
    /// The colour a provider is recognised by, for a button painted in it. Not the same
    /// as the mark's colour: Keycloak, Google, Okta and Auth0 all draw their mark white.
    /// </summary>
    public static string BrandFor(string? name, string? authority)
        => Known.TryGetValue(SlugFor(name, authority), out var icon) ? icon.Brand : Fallback.Brand;

    /// <summary>
    /// Black or white, whichever can be read on <paramref name="background"/>. The
    /// threshold is the one WCAG's contrast ratio puts between the two: a surface lighter
    /// than roughly 18% relative luminance carries black better than white.
    /// </summary>
    public static string ReadableOn(string background)
        => Luminance(background) > 0.179 ? "#101418" : "#ffffff";

    /// <summary>
    /// The same colour, darkened or — for a colour already close to black — lightened, for
    /// the state a button takes on under the pointer.
    /// </summary>
    public static string HoverShadeOf(string colour)
    {
        var (r, g, b) = Rgb(colour);

        // Scaling a near-black colour moves it nowhere — 0 times anything is still black —
        // so below that point the shade is lifted by a fixed step instead.
        var dark = Luminance(colour) <= 0.06;

        return "#" + Channel(r) + Channel(g) + Channel(b);

        string Channel(int value)
            => Math.Clamp(dark ? value + 28 : (int)Math.Round(value * 0.86), 0, 255)
                .ToString("x2", CultureInfo.InvariantCulture);
    }

    /// <summary>Relative luminance as WCAG defines it, 0 for black and 1 for white.</summary>
    private static double Luminance(string colour)
    {
        var (r, g, b) = Rgb(colour);
        return (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));

        static double Linear(int channel)
        {
            var value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>A #rrggbb colour as its three channels; anything else reads as black.</summary>
    private static (int R, int G, int B) Rgb(string colour)
    {
        var hex = colour.AsSpan().TrimStart('#');
        if (hex.Length != 6
            || !int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !int.TryParse(hex[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return (0, 0, 0);
        }

        return (r, g, b);
    }

    /// <summary>The icon as an SVG document, ready to serve.</summary>
    public static string Svg(string slug)
    {
        if (Known.TryGetValue(slug, out var icon))
        {
            return Document($"<path fill=\"{icon.Colour}\" d=\"{icon.Path}\"/>");
        }

        // The plugin's own mark: three strands meeting at a keyhole, the same shape as
        // the icon Jellyfin shows for the plugin itself.
        var c = Fallback.Colour;
        return Document(
            $"<g fill=\"none\" stroke=\"{c}\" stroke-width=\"2.6\" stroke-linecap=\"round\">"
            + $"<path d=\"{Fallback.Path}\"/></g>"
            + $"<circle cx=\"16\" cy=\"12\" r=\"5.6\" fill=\"{c}\"/>"
            + "<circle cx=\"16\" cy=\"11\" r=\"1.9\" fill=\"#101418\"/>"
            + "<path d=\"M16 12.2v2.6\" stroke=\"#101418\" stroke-width=\"2.2\" stroke-linecap=\"round\"/>");
    }

    private static string Document(string body)
        => "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" "
            + "width=\"24\" height=\"24\" role=\"img\">" + body + "</svg>";
}
