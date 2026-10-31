using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Which group mappings a login matches, and — when none do — what nearly matched.
///
/// Two places need the same answer: the permission sync, which turns matches into library
/// access, and the login gate, which can refuse a login that matches nothing. If they
/// disagreed about what "matched" means, a login could be let in and then granted nothing,
/// or refused although the sync would have given it something.
/// </summary>
public static class GroupMatching
{
    /// <summary>
    /// The mappings whose group the provider actually sent. Matching is exact, because
    /// "Media" and "media" can be two different groups at the provider and a mapping that
    /// silently covered both would hand out access nobody granted.
    /// </summary>
    public static List<GroupMappingConfig> Matching(PluginConfiguration config, OidcIdentity identity)
        => config.GroupMappings
            .Where(m => identity.Groups.Contains(m.Group, StringComparer.Ordinal))
            .ToList();

    /// <summary>
    /// The mapping-and-group pairs that almost matched, with the reason each one did not.
    /// A near miss is almost always a typo, and the symptom — the user sees nothing, or is
    /// turned away — says nothing about the cause, so callers log these.
    /// </summary>
    public static IEnumerable<(string Mapping, string Arrived, string Reason)> NearMisses(
        PluginConfiguration config, OidcIdentity identity, List<GroupMappingConfig> matched)
    {
        foreach (var mapping in config.GroupMappings.Except(matched))
        {
            foreach (var arrived in identity.Groups)
            {
                if (MissReason(mapping.Group, arrived) is { } reason)
                {
                    yield return (mapping.Group, arrived, reason);
                }
            }
        }
    }

    /// <summary>
    /// Why a mapping almost matched a group, or null when the two have nothing to do with
    /// each other. Two near misses are worth saying out loud:
    ///
    ///   * the names differ only in case;
    ///   * the group is a path and the mapping is its last segment, which is what Keycloak
    ///     sends with "Full group path" left on — "/media" against a mapping of "media".
    /// </summary>
    public static string? MissReason(string mapping, string arrived)
    {
        if (string.Equals(mapping, arrived, StringComparison.OrdinalIgnoreCase))
        {
            return "the names differ only in case";
        }

        var lastSegment = arrived[(arrived.LastIndexOf('/') + 1)..];
        if (arrived.Contains('/', StringComparison.Ordinal)
            && string.Equals(mapping, lastSegment, StringComparison.OrdinalIgnoreCase))
        {
            return "the provider sent a path and the mapping names only its last part "
                + "(Keycloak does this with \"Full group path\" left on)";
        }

        return null;
    }
}
