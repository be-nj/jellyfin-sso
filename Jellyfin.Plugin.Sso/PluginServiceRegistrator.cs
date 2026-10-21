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
        serviceCollection.AddSingleton<LoginRateLimiter>();

        // Resolved per request by [ServiceFilter] on the anonymous login endpoints.
        serviceCollection.AddSingleton<RateLimitFilter>();
        serviceCollection.AddSingleton<OidcClientFactory>();
        serviceCollection.AddSingleton<ClaimsExtractor>();
        serviceCollection.AddSingleton<UserProvisioner>();
        serviceCollection.AddSingleton<PermissionSync>();
        serviceCollection.AddSingleton<ProfilePictureSync>();
        serviceCollection.AddSingleton<SessionRevoker>();
        serviceCollection.AddSingleton<LoginBranding>();

        // One fetcher for every picture whose address came from somewhere else: the
        // provider's logo and the profile picture both go through it.
        serviceCollection.AddSingleton<RemoteImages>();
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
