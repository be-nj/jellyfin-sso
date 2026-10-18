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
        var enabled = Plugin.Instance?.Configuration.Providers.Where(p => p.Enabled).ToList() ?? [];

        var branding = (_configManager.GetConfiguration("branding") as BrandingOptions)
            ?? new BrandingOptions();

        if (enabled.Count == 0)
        {
            // No enabled provider — remove the button if it was previously set by us.
            if (branding.LoginDisclaimer?.Contains("/sso/oidc/start") == true)
            {
                branding.LoginDisclaimer = string.Empty;
                _configManager.SaveConfiguration("branding", branding);
            }
            return;
        }

        // With several providers the button goes to the stable entry point, which asks
        // which one; with a single provider it skips that step.
        var href = enabled.Count == 1 ? $"/sso/oidc/start/{enabled[0].Id}" : "/sso/oidc/start";
        var label = enabled.Count == 1 ? $"Login with SSO ({enabled[0].Name})" : "Login with SSO";

        // Inject a link styled with Jellyfin's own button classes.
        // Using the native Jellyfin web button CSS to blend in.
        var link = $"<a class=\"raised emby-button block\" " +
                   $"href=\"{href}\" " +
                   $"style=\"display:block;text-align:center;margin-top:.5em\">" +
                   $"{label}</a>";

        if (string.Equals(branding.LoginDisclaimer, link, StringComparison.Ordinal))
            return; // no change needed

        branding.LoginDisclaimer = link;
        _configManager.SaveConfiguration("branding", branding);
        _logger.LogInformation(
            "SSO: login button set in Jellyfin branding for {Count} enabled provider(s).", enabled.Count);
    }
}
