using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Says at startup whether every Jellyfin action <see cref="NativeLoginLifetime"/> guards
/// by name still exists on this server. A rename in a Jellyfin minor would otherwise drop
/// the guard without a single failing request.
/// </summary>
public sealed class NativeLoginActionsCheck(
    IActionDescriptorCollectionProvider actions,
    ILogger<NativeLoginActionsCheck> logger) : IHostedService
{
    internal const string AllPresent = "Every Jellyfin login action the SSO revocation guard watches is present";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var present = actions.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(a => (a.ControllerName, a.ActionName))
            .ToHashSet();

        var missing = NativeLoginLifetime.Guarded
            .Where(g => !present.Contains((g.Controller, g.Action)))
            .Select(g => $"{g.Controller}.{g.Action}")
            .ToList();

        if (missing.Count == 0)
        {
            logger.LogInformation("{Message} ({Count}).", AllPresent, NativeLoginLifetime.Guarded.Count);
        }
        else
        {
            logger.LogWarning(
                "Jellyfin no longer has the action(s) {Missing}. Tokens minted there are not "
                + "checked against SSO revocations; a session ended at the provider can "
                + "come back through them. This plugin needs an update for this server.",
                string.Join(", ", missing));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
