using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Sso;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<EphemeralSecretStore>();
        serviceCollection.AddSingleton<PendingStateStore>();
        serviceCollection.AddSingleton<OidcClientFactory>();
        serviceCollection.AddSingleton<ClaimsExtractor>();
        serviceCollection.AddSingleton<UserProvisioner>();
        serviceCollection.AddSingleton<PermissionSync>();
        serviceCollection.AddSingleton<LoginBranding>();
        serviceCollection.AddScoped<IAuthenticationProvider, SsoAuthenticationProvider>();
    }
}
