using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Sso;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Reapply branding whenever admin saves new configuration.
        ConfigurationChanged += (_, _) =>
        {
            ServiceProvider?.GetService<LoginBranding>()?.Apply();
        };
    }

    /// <summary>Set by Jellyfin's DI container after services are built.</summary>
    public IServiceProvider? ServiceProvider { get; set; }

    public override string Name => "SSO Authentication";
    public override string Description => "OIDC/OAuth2 single sign-on with group-based library access.";
    public override Guid Id => Guid.Parse("f5538c3a-1bce-4b8c-b8a5-bc1a0fffdf27");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = "SSO Authentication",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
        // JS loaded by Jellyfin's SPA via data-controller="__plugin/{Name}.js".
        // Must be an ES module with a default export function(view).
        yield return new PluginPageInfo
        {
            Name = Name + ".js",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.js",
        };
    }
}
