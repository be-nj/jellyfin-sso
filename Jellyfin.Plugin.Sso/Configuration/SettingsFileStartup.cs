using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Configuration;

/// <summary>
/// Applies the settings file at startup. Registered before the login buttons are written,
/// so they are drawn from the file's providers.
/// </summary>
public sealed class SettingsFileStartup : IHostedService
{
    private readonly ILogger<SettingsFileStartup> _logger;

    public SettingsFileStartup(ILogger<SettingsFileStartup> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var file = SettingsFile.FromEnvironment();
        if (plugin is null || file is null)
        {
            return Task.CompletedTask;
        }

        plugin.SettingsFile = file;
        if (file.Error is not null)
        {
            // The configuration stays as it was last saved: a broken file must not take
            // away a working login, nor apply half of what it says.
            _logger.LogError("SSO: the settings file {Path} {Error}; it was not applied.", file.Path, file.Error);
            return Task.CompletedTask;
        }

        lock (Plugin.ConfigurationLock)
        {
            file.ApplyTo(plugin.Configuration);
            plugin.SaveConfiguration();
        }

        _logger.LogInformation("SSO: applied {Settings} from {Path}.", string.Join(", ", file.Managed), file.Path);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
