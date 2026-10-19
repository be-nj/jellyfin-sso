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
        serviceCollection.AddSingleton<QuickConnectApprovalStore>();
        serviceCollection.AddSingleton<PendingStateStore>();
        serviceCollection.AddSingleton<OidcClientFactory>();
        serviceCollection.AddSingleton<ClaimsExtractor>();
        serviceCollection.AddSingleton<UserProvisioner>();
        serviceCollection.AddSingleton<PermissionSync>();
        serviceCollection.AddSingleton<LoginBranding>();
        serviceCollection.AddSingleton<ProviderLogos>();
        serviceCollection.AddScoped<IAuthenticationProvider, SsoAuthenticationProvider>();

        // The login buttons are written into Jellyfin's branding. Without this they would
        // only appear after an administrator saves the configuration, which a plugin
        // update does not do.
        serviceCollection.AddHostedService<LoginBrandingStartup>();

        // Optional: puts the login button inside Jellyfin's login form by adding a
        // script tag to index.html as it is served.
        serviceCollection.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter,
            LoginFormScriptInjection>();
    }
}
