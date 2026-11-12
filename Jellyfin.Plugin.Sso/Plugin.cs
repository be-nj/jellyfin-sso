using Jellyfin.Plugin.Sso.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Sso;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Held while the configuration's lists are changed. Logins, linking and the admin
    /// page all write the same object from different requests, and List&lt;T&gt; does not
    /// survive that on its own.
    /// </summary>
    public static object ConfigurationLock { get; } = new();

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Links never arrive with a configuration. The plugin's own admin endpoint already
    /// keeps the live ones; this covers Jellyfin's generic
    /// <c>POST /Plugins/{id}/Configuration</c>, which would otherwise replace them
    /// wholesale. A link removed that way skipped the revocation that every other way of
    /// removing one performs, and left the sessions it had opened running.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            lock (ConfigurationLock)
            {
                incoming.Links = Configuration.Links;
                base.UpdateConfiguration(incoming);
            }

            return;
        }

        base.UpdateConfiguration(configuration);
    }

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
