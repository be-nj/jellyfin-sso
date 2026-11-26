using System.Text.RegularExpressions;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Configuration;

/// <summary>
/// What a configuration must satisfy before it is saved. Kept apart from the endpoint that
/// applies them, so the rules can be tested without a controller.
/// </summary>
public static partial class ConfigurationRules
{
    [GeneratedRegex(@"^[A-Za-z0-9._~-]{1,64}\z")]
    private static partial Regex ProviderId();

    /// <summary>The first thing wrong with the configuration, or null when there is none.</summary>
    public static string? Problem(PluginConfiguration config)
    {
        // First, because everything below walks these lists. A null entry arrives only by
        // hand-written JSON, and used to answer 500 from wherever it was first touched.
        if (config.Providers.Any(p => p is null))
        {
            return "The list of providers has an empty entry.";
        }

        if (config.GroupMappings.Any(m => m is null))
        {
            return "The list of group mappings has an empty entry.";
        }

        // The configuration is an XML file, and Jellyfin throws the whole file away when
        // it cannot be read back: a control character in one provider's name cost every
        // provider, mapping and account link at the next restart. See StorableText.
        if (StorableText.FirstUncarried(config) is { } where)
        {
            return $"{where} contains a control character, which the configuration file cannot hold.";
        }

        // A provider is found by its id — in the login address, in the redirect URI the
        // provider has on file, in every account link. The page makes one up for a new
        // provider and never shows a field for it, so a bad one arrives by hand: an empty
        // id or one with a slash cannot complete a login, and of two providers with one
        // id the second is never reached.
        foreach (var provider in config.Providers)
        {
            if (!ProviderId().IsMatch(provider.Id ?? string.Empty))
            {
                return $"Provider '{provider.Name}': the id '{provider.Id}' cannot be used. An id is 1 to 64 "
                    + "letters, digits, dots, dashes, underscores or tildes, because it is part of "
                    + "the address the provider sends people back to.";
            }

            if (!Enum.IsDefined(provider.ClientType))
            {
                return $"Provider '{provider.Id}': the client type is neither Public nor Confidential.";
            }
        }

        var twice = config.Providers
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
        {
            return $"Two providers have the id '{twice.Key}'. Links and login addresses name a "
                + "provider by its id, so each needs its own.";
        }

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

        var known = config.Providers.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        // A library is named by its Jellyfin id, and the sync parses those ids on every
        // login. An id that is not a GUID therefore turned every SSO login on the server
        // into a 500 — from a value that only this endpoint can introduce, and only by
        // hand-edited JSON, which is exactly why it is checked here rather than trusted.
        for (var i = 0; i < config.GroupMappings.Count; i++)
        {
            var mapping = config.GroupMappings[i];

            // A mapping is matched by its group, so one without a group names nobody. It
            // used to be saved, and a token carrying an empty group matched it (#330).
            if (string.IsNullOrWhiteSpace(mapping.Group))
            {
                return $"Group mapping {i + 1} has no group name. Enter the group it applies to, "
                    + "or remove the mapping.";
            }

            // Any, not FirstOrDefault compared with null: an entry that is null is itself
            // not a library id, and used to pass as "nothing found".
            if (mapping.LibraryIds.Any(id => !Guid.TryParse(id, out _)))
            {
                var bad = mapping.LibraryIds.First(id => !Guid.TryParse(id, out _));
                return $"Group mapping '{mapping.Group}': '{bad}' is not a library id.";
            }

            // A mapping limited to a provider that does not exist matches no login, which
            // is safe, but it is not what anybody meant: a typo in a hand-written id, or a
            // provider removed while a mapping still named it. Saving it would look like a
            // working mapping. Dropping the id instead would be worse — a list emptied that
            // way means every provider, so a mapping meant for one would quietly apply to
            // all of them (#340). A null entry is not a provider either.
            var scope = mapping.ProviderIds ?? [];
            if (scope.Any(id => id is null || !known.Contains(id)))
            {
                var unknown = scope.First(id => id is null || !known.Contains(id));
                return $"Group mapping '{mapping.Group}' is limited to the provider '{unknown}', "
                    + "and there is no provider with that id. Choose the providers it applies "
                    + "to again, or let it apply to all of them.";
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
