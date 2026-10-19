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
        => anyProvider
            ? CssStart
              + ".loginDisclaimerContainer{display:block;}"
              + ".loginDisclaimer{margin:0;}"
              + ".sso-login-button{display:flex;align-items:center;justify-content:center;"
              + "gap:.6em;width:100%;margin:.25em 0;}"
              + ".sso-login-mark{flex:0 0 auto;}"
              + CssEnd
            : string.Empty;

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

            // Inline styles are stripped from the disclaimer before it is rendered, so the
            // look has to come from Jellyfin's own classes: "emby-button block" makes a
            // full-width button, "raised" gives it the secondary surface, and the themes
            // style an anchor's text only under a[data-role=button] — without it the label
            // keeps the link colour and disappears on a coloured button.
            markup
                .Append("<a data-role=\"button\" class=\"raised block emby-button sso-login-button\" href=\"")
                .Append(prefix)
                .Append("/sso/oidc/start/")
                .Append(WebUtility.UrlEncode(provider.Id))
                .Append("\"><img src=\"")
                .Append(prefix)
                .Append("/sso/icon/")
                .Append(ProviderIcons.SlugFor(provider.Name, provider.Authority))
                .Append("\" alt=\"\" width=\"22\" height=\"22\" class=\"sso-login-mark\"/>")
                .Append(WebUtility.HtmlEncode(label))
                .Append("</a>");
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

    private static string SpliceBetween(string? text, string block, string Start, string End)
    {
        var existing = text ?? string.Empty;

        var start = existing.IndexOf(Start, StringComparison.Ordinal);
        var end = existing.IndexOf(End, StringComparison.Ordinal);

        if (start >= 0 && end > start)
        {
            var before = existing[..start];
            var after = existing[(end + End.Length)..];
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
