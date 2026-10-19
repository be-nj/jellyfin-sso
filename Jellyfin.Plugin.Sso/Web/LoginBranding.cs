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

        if (string.Equals(branding.LoginDisclaimer, disclaimer, StringComparison.Ordinal))
            return; // no change needed

        branding.LoginDisclaimer = disclaimer;
        _configManager.SaveConfiguration("branding", branding);

        _logger.LogInformation(
            "SSO: login buttons updated in Jellyfin branding for {Count} enabled provider(s).",
            enabled.Count);
    }

    /// <summary>
    /// The sub-path Jellyfin is served under, if any. Branding holds plain HTML, so the
    /// links have to carry the prefix themselves.
    /// </summary>
    private string BasePath() => _configManager.GetNetworkConfiguration().BaseUrl ?? string.Empty;
}
