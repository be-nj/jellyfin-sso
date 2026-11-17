using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Configuration;

/// <summary>
/// What a configuration must satisfy before it is used: the configuration page's save and the
/// settings file go through the same rules.
/// </summary>
public static class ConfigurationRules
{
    /// <summary>The first thing wrong with the configuration, or null when there is none.</summary>
    public static string? Problem(PluginConfiguration config)
    {
        // A confidential client's secret is named here, not stored here. The page has no
        // field for it, so this arrives by hand-edited JSON — and a pasted literal secret
        // would be written to the plugin's configuration file, which the documentation
        // promises never happens. Refusing it at the door is what makes that true.
        foreach (var provider in config.Providers.Where(
            p => !string.IsNullOrWhiteSpace(p.ConfidentialSecretRef)))
        {
            if (!provider.ConfidentialSecretRef!.StartsWith("env:", StringComparison.Ordinal)
                && !provider.ConfidentialSecretRef.StartsWith("file:", StringComparison.Ordinal))
            {
                return $"Provider '{provider.Id}': ConfidentialSecretRef must name where the "
                    + "secret is, as 'env:VARIABLE' or 'file:/path', not hold the secret "
                    + "itself.";
            }
        }

        // A name the plugin does not recognise is dropped on the way to the validator, and
        // a list of nothing but unrecognised names falls back to every asymmetric
        // algorithm — which is more than whoever typed it asked for, and silently. This is
        // the only moment it can be said while somebody is looking at the page.
        foreach (var provider in config.Providers)
        {
            var bad = provider.SigningAlgorithms
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .FirstOrDefault(name => OidcClientFactory.Recognised(name.Trim()) is null);

            if (bad is not null)
            {
                return $"Provider '{provider.Id}': '{bad}' is not a signing algorithm this "
                    + "plugin accepts. Use one or more of "
                    + string.Join(", ", OidcClientFactory.AsymmetricSigningAlgorithms)
                    + ", or leave the field empty for all of them.";
            }

            // Refused here for the same reason. An entry that is not a host name is left
            // out when the list is used, so a typo would look saved and then admit
            // nothing — and somebody who wrote https://… or *.example.com meant something
            // this field does not do.
            var badHost = provider.EndpointHosts
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .FirstOrDefault(entry => OidcClientFactory.EndpointHost(entry) is null);

            if (badHost is not null)
            {
                return $"Provider '{provider.Id}': '{badHost}' is not a host name. Further "
                    + "endpoint hosts are names like www.googleapis.com, with a port where "
                    + "one is needed and without https://, a path or a wildcard.";
            }
        }

        // A library is named by its Jellyfin id, and the sync parses those ids on every
        // login. An id that is not a GUID therefore turned every SSO login on the server
        // into a 500 — from a value that only this endpoint can introduce, and only by
        // hand-edited JSON, which is exactly why it is checked here rather than trusted.
        foreach (var mapping in config.GroupMappings)
        {
            var bad = mapping.LibraryIds.FirstOrDefault(id => !Guid.TryParse(id, out _));
            if (bad is not null)
            {
                return $"Group mapping '{mapping.Group}': '{bad}' is not a library id.";
            }
        }

        if (!string.IsNullOrWhiteSpace(config.PublicAddress) && SsoAddress.Configured(config.PublicAddress) is null)
        {
            return $"'{config.PublicAddress}' is not a public address. Use the address people open "
                + "Jellyfin at, like https://jellyfin.example.com or https://example.com/jellyfin.";
        }

        return null;
    }
}
