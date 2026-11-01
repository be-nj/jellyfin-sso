using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Sets Jellyfin's LoginDisclaimer to an "Login with SSO" anchor link.
/// Runs once at plugin start and whenever the configuration changes.
/// DOMPurify allows anchor tags with href, so this is the supported approach.
/// </summary>
public sealed class LoginBranding
{
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<LoginBranding> _logger;

    public LoginBranding(IServerConfigurationManager configManager, ILogger<LoginBranding> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    public void Apply()
    {
        var config = Plugin.Instance?.Configuration;
        var enabled = config is { AddLoginButtonToBranding: true }
            ? config.Providers.Where(p => p.Enabled).ToList()
            : [];

        var branding = (_configManager.GetConfiguration("branding") as BrandingOptions)
            ?? new BrandingOptions();

        var block = LoginButtonMarkup.Build(enabled, BasePath());
        var disclaimer = LoginButtonMarkup.Splice(branding.LoginDisclaimer, block);
        var css = LoginButtonMarkup.SpliceCss(
            branding.CustomCss,
            LoginButtonMarkup.Css(
                enabled.Count > 0, enabled, config is { UseProviderLogos: true }));

        if (Unchanged(branding.LoginDisclaimer, disclaimer) && Unchanged(branding.CustomCss, css))
            return; // no change needed

        branding.LoginDisclaimer = disclaimer;
        branding.CustomCss = css;
        _configManager.SaveConfiguration("branding", branding);

        _logger.LogInformation(
            "SSO: login buttons updated in Jellyfin branding for {Count} enabled provider(s).",
            enabled.Count);
    }

    /// <summary>
    /// Whether what is in the branding already says what the plugin would write. A field
    /// Jellyfin has never written is null, and writing "" over it is a configuration file
    /// rewritten and a log line on every server start, for no change at all.
    /// </summary>
    private static bool Unchanged(string? current, string next)
        => string.Equals(current ?? string.Empty, next, StringComparison.Ordinal);

    /// <summary>
    /// What the buttons' links are prefixed with.
    ///
    /// The sub-path Jellyfin is served under, if any — branding holds plain HTML, so the
    /// links have to carry the prefix themselves. That is enough for every client that
    /// loads the web client from the server, which is most of them.
    ///
    /// It is not enough for a client that ships its own copy of the web client and runs
    /// it from a local origin, as the Samsung app does: the branding still arrives,
    /// because branding is an API call, so the button appears and then submits to a path
    /// on the television. When the operator has set a published server address — the
    /// setting that exists so Jellyfin can tell clients where it is — that address is
    /// used instead, which those clients can follow and everyone else reaches the same
    /// server by.
    /// </summary>
    private string BasePath()
    {
        var network = _configManager.GetNetworkConfiguration();
        var basePath = network.BaseUrl ?? string.Empty;
        var published = PublishedOrigin(network.PublishedServerUriBySubnet);

        return published is null ? basePath : published + basePath;
    }

    /// <summary>
    /// The scheme and host of the address the operator publishes, or null when none is
    /// usable. Jellyfin keeps these as "subnet=address" entries, one per network, so the
    /// one that answers for everybody — "all=" — is preferred and any other is taken in
    /// order. The entries are free text somebody typed, so anything that is not an
    /// absolute http(s) address is ignored rather than spliced into markup.
    /// </summary>
    internal static string? PublishedOrigin(IReadOnlyList<string>? published)
    {
        if (published is null)
        {
            return null;
        }

        var entries = published
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .OrderByDescending(entry => entry.TrimStart().StartsWith("all=", StringComparison.OrdinalIgnoreCase));

        foreach (var entry in entries)
        {
            var address = entry.Contains('=', StringComparison.Ordinal)
                ? entry[(entry.IndexOf('=', StringComparison.Ordinal) + 1)..]
                : entry;

            if (Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }
        }

        return null;
    }
}
