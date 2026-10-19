using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Writes the login buttons into Jellyfin's branding when the server starts.
///
/// Saving the plugin's configuration does the same thing, but nothing saves it after an
/// update or a fresh install — so without this, a server that was configured weeks ago
/// would come back up with no button on its login page.
/// </summary>
public sealed class LoginBrandingStartup : IHostedService
{
    private readonly LoginBranding _branding;
    private readonly ILogger<LoginBrandingStartup> _logger;

    public LoginBrandingStartup(LoginBranding branding, ILogger<LoginBrandingStartup> logger)
    {
        _branding = branding;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _branding.Apply();
        }
        catch (Exception ex)
        {
            // A login page without a button is a nuisance; a server that will not start
            // is not. Whatever went wrong here must not take Jellyfin down with it.
            _logger.LogWarning(ex, "SSO: could not update the login buttons at startup.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
