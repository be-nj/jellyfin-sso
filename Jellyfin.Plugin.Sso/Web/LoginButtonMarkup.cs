using System.Net;
using System.Text;
using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Builds the login buttons and splices them into Jellyfin's login disclaimer.
///
/// The disclaimer belongs to the administrator — it may hold a house rule or a contact
/// address — so the block is fenced with markers and only the fenced part is ever
/// replaced or removed.
/// </summary>
public static class LoginButtonMarkup
{
    internal const string Start = "<!-- sso-login-buttons:start -->";
    internal const string End = "<!-- sso-login-buttons:end -->";

    internal const string CssStart = "/* sso-login-buttons:start */";
    internal const string CssEnd = "/* sso-login-buttons:end */";

    /// <summary>
    /// The styling for those buttons, for Jellyfin's custom CSS. The disclaimer that
    /// holds them shrink-wraps its content and sits in a centred flex row, so without
    /// this the button is a small grey pill under the full-width buttons above it.
    /// </summary>
    public static string Css(bool anyProvider)
        => Css(anyProvider, []);

    /// <summary>
    /// The styling, plus one rule per provider so each button carries its own mark's
    /// colour on its edge rather than all of them looking alike.
    /// </summary>
    public static string Css(bool anyProvider, IReadOnlyList<ProviderConfig> providers)
        => Css(anyProvider, providers, providerLogos: false);

    /// <summary>
    /// The styling, told whether the marks on those buttons are the providers' own logos.
    /// It matters for a branded button: the plugin's own marks are flat shapes that can be
    /// recoloured to whatever reads on the button, while a logo fetched from the provider
    /// is an image of any shape and colour, and flattening that to one colour turns a
    /// rounded logo into a black rectangle.
    /// </summary>
    public static string Css(
        bool anyProvider, IReadOnlyList<ProviderConfig> providers, bool providerLogos)
        => anyProvider
            ? CssStart
              // The disclaimer is a centred flex row that shrink-wraps whatever is in it,
              // so the button only fills the form once the container is a block.
              // Jellyfin centres the disclaimer in a flex row and gives it an auto
              // margin, which left the button a few pixels off the column the buttons
              // above it stand in.
              + ".loginDisclaimerContainer{display:block;margin:1.2em 0 0;padding:0;}"
              + ".loginDisclaimer{margin:0;padding:0;width:100%;}"
              // Spaced away from Jellyfin's own buttons, because it does something else:
              // it leaves this server. The margin is !important because jellyfin-web's
              // own .emby-button{margin:.3em} otherwise wins it and the button stands
              // 3.7px right of the column of buttons above it; the branding stylesheet
              // is loaded before the app's, so equal specificity is not enough.
              // LoginButtonLayoutTests measures that.
              + ".loginDisclaimer .sso-login-form{margin:0;padding:0;width:100%;}"
              + ".loginDisclaimer .sso-login-button{display:flex;align-items:center;"
              + "justify-content:center;gap:.7em;width:100%;box-sizing:border-box;"
              + "margin:.4em 0 0 !important;"
              + "font-weight:600;letter-spacing:.01em;"
              + "border-left:3px solid var(--sso-mark, #00a4dc);"
              + "transition:background .15s ease;}"
              + ".loginDisclaimer .sso-login-button:hover{background:#4a4a4a;}"
              // The mark may be a logo fetched from the provider, and that image is
              // whatever shape the household gave it. A fixed square box squeezed a wide
              // lockup into a couple of coloured pixels, so the height is fixed and the
              // width follows the picture: square marks stay square, a wordmark reads as
              // a wordmark, and min-width keeps the label from jumping while it loads.
              + ".sso-login-mark{flex:0 0 auto;display:block;height:22px;width:auto;"
              + "min-width:22px;max-width:96px;object-fit:contain;}"
              + ".sso-login-label{display:block;font-weight:600;}"
              + string.Concat(providers.Select(p => PerProvider(p, providerLogos)))
              + CssEnd
            : string.Empty;

    /// <summary>
    /// One provider's own rules: the colour on its edge, and — when the administrator
    /// asked for it — the whole button in the provider's colour.
    ///
    /// The mark is turned black or white with a filter rather than a second icon, because
    /// the mark may be the provider's own logo, fetched from the provider itself, and
    /// that image can be any shape or colour. brightness(0) flattens whatever arrives to
    /// black; invert(1) after it makes that white.
    /// </summary>
    private static string PerProvider(ProviderConfig provider, bool providerLogos)
    {
        var selector = $".loginDisclaimer .sso-login-button[data-provider=\"{WebUtility.HtmlEncode(provider.Id)}\"]";
        var mark = ProviderIcons.ColourFor(provider.Name, provider.Authority);

        if (!provider.BrandedLoginButton)
        {
            return selector + "{--sso-mark:" + mark + ";}";
        }

        var brand = ProviderIcons.SanitiseColour(provider.ButtonColour)
            ?? ProviderIcons.BrandFor(provider.Name, provider.Authority);
        var ink = ProviderIcons.ReadableOn(brand);

        // The provider's own logo keeps its colours and gets a light chip to sit on, so
        // that a dark logo stays visible on a dark button. The plugin's own mark is a flat
        // shape, so it is simply turned to whichever of black and white reads here.
        var markRule = providerLogos
            ? selector + " .sso-login-mark{background:#fff;border-radius:4px;padding:2px;"
              + "box-sizing:content-box;}"
            : selector + " .sso-login-mark{filter:brightness(0)"
              + (ink == "#ffffff" ? " invert(1)" : string.Empty) + ";}";

        return selector + "{--sso-mark:" + brand + ";background:" + brand + " !important;"
            + "color:" + ink + " !important;}"
            + selector + ":hover{background:" + ProviderIcons.HoverShadeOf(brand) + " !important;}"
            + selector + " .sso-login-label{color:" + ink + ";}"
            + markRule;
    }

    /// <summary>Puts the styling into the administrator's own custom CSS, fenced.</summary>
    public static string SpliceCss(string? customCss, string block)
        => SpliceBetween(customCss, block, CssStart, CssEnd);

    /// <summary>
    /// One button per enabled provider, or nothing when none is enabled. Jellyfin's own
    /// button classes are used so themes style these like any other button.
    /// </summary>
    public static string Build(IReadOnlyList<ProviderConfig> enabledProviders, string basePath)
    {
        if (enabledProviders.Count == 0)
        {
            return string.Empty;
        }

        var prefix = NormaliseBasePath(basePath);
        var markup = new StringBuilder(Start);

        foreach (var provider in enabledProviders)
        {
            var label = string.IsNullOrWhiteSpace(provider.Name)
                ? "Login with SSO"
                : $"Login with {provider.Name}";

            // A form, not a link. jellyfin-web walks every <a> in the disclaimer and
            // forces target="_blank" and is="emby-linkbutton" on it, and that element's
            // click handler hands a target-less href to its own router — which does not
            // know /sso/oidc/start and quietly re-renders the login page. So a link here
            // either does nothing or opens the login in a second tab. A submitted form is
            // an ordinary navigation that no router sees.
            //
            // Inline styles are stripped from the disclaimer before it is rendered, so the
            // look has to come from Jellyfin's own classes: "emby-button block" makes a
            // full-width button and "raised" gives it the secondary surface. The classes
            // only carry the styling; the behaviour that breaks links is attached to
            // anchors alone.
            markup
                .Append("<form class=\"sso-login-form\" method=\"get\" action=\"")
                .Append(prefix)
                .Append("/sso/oidc/start/")
                .Append(WebUtility.UrlEncode(provider.Id))
                .Append("\"><button type=\"submit\" data-provider=\"")
                .Append(WebUtility.HtmlEncode(provider.Id))
                .Append("\" class=\"raised block emby-button sso-login-button\">")
                .Append("<img src=\"")
                .Append(prefix)
                .Append("/sso/icon/provider/")
                .Append(WebUtility.UrlEncode(provider.Id))
                // Height only: the width comes from the picture, so a logo that is not
                // square is not squashed into one. See the .sso-login-mark rule.
                .Append("\" alt=\"\" height=\"22\" class=\"sso-login-mark\"/>")
                .Append("<span class=\"sso-login-label\">")
                .Append(WebUtility.HtmlEncode(label))
                .Append("</span>")
                .Append("</button></form>");
        }

        return markup.Append(End).ToString();
    }

    /// <summary>
    /// Puts <paramref name="block"/> into <paramref name="disclaimer"/>, replacing an
    /// earlier block and leaving everything the administrator wrote untouched. An empty
    /// block removes the fenced part.
    /// </summary>
    public static string Splice(string? disclaimer, string block)
        => SpliceBetween(disclaimer, block, Start, End);

    /// <summary>
    /// The fences are passed in rather than read from the class, because the same splice
    /// serves the disclaimer's HTML comments and the stylesheet's CSS comments.
    /// </summary>
    private static string SpliceBetween(string? text, string block, string opening, string closing)
    {
        var existing = text ?? string.Empty;

        var start = existing.IndexOf(opening, StringComparison.Ordinal);
        var end = existing.IndexOf(closing, StringComparison.Ordinal);

        if (start >= 0 && end > start)
        {
            var before = existing[..start];
            var after = existing[(end + closing.Length)..];
            return (before + block + after).Trim();
        }

        return string.IsNullOrEmpty(block) ? existing : (existing + block).Trim();
    }

    /// <summary>
    /// Jellyfin can be served under a sub-path; links in the branding are plain strings,
    /// so the base URL from the server's network configuration has to be prepended.
    /// </summary>
    private static string NormaliseBasePath(string? basePath)
    {
        var trimmed = (basePath ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
