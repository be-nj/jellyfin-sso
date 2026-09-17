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
        var provider = config?.Providers.FirstOrDefault(p => p.Enabled);

        var branding = (_configManager.GetConfiguration("branding") as BrandingOptions)
            ?? new BrandingOptions();

        if (provider is null)
        {
            // No enabled provider — remove the button if it was previously set by us.
            if (branding.LoginDisclaimer?.Contains("/sso/oidc/start/") == true)
            {
                branding.LoginDisclaimer = string.Empty;
                _configManager.SaveConfiguration("branding", branding);
            }
            return;
        }

        // Inject a link styled with Jellyfin's own button classes.
        // Using the native Jellyfin web button CSS to blend in.
        var link = $"<a class=\"raised emby-button block\" " +
                   $"href=\"/sso/oidc/start/{provider.Id}\" " +
                   $"style=\"display:block;text-align:center;margin-top:.5em\">" +
                   $"Login with SSO ({provider.Name})</a>";

        if (string.Equals(branding.LoginDisclaimer, link, StringComparison.Ordinal))
            return; // no change needed

        branding.LoginDisclaimer = link;
        _configManager.SaveConfiguration("branding", branding);
        _logger.LogInformation("SSO: login button set in Jellyfin branding for provider {Name}.", provider.Name);
    }
}
