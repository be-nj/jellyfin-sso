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
    /// The mappings whose group the provider actually sent, among those that apply to the
    /// provider the login came through. Matching is exact, because "Media" and "media" can
    /// be two different groups at the provider and a mapping that silently covered both
    /// would hand out access nobody granted.
    /// </summary>
    public static List<GroupMappingConfig> Matching(PluginConfiguration config, OidcIdentity identity)
        => config.GroupMappings
            // A mapping without a group names nobody, and an empty group is not one the
            // provider sent (#330).
            .Where(m => !string.IsNullOrWhiteSpace(m.Group)
                && AppliesTo(m, identity.ProviderId)
                && identity.Groups.Contains(m.Group, StringComparer.Ordinal))
            .ToList();

    /// <summary>
    /// Whether a mapping applies to logins through this provider. A mapping that names no
    /// provider applies to all of them, which is what every mapping did before the list
    /// existed. One that names some applies to those alone (#340): a group name is decided
    /// by whoever runs the provider, and a second provider on the same server can be one
    /// where somebody else picks the names.
    ///
    /// A login whose provider is not known matches only the mappings that name none. That
    /// does not happen on the login path, where the identity always carries its provider;
    /// it is the answer that grants least if it ever does.
    /// </summary>
    public static bool AppliesTo(GroupMappingConfig mapping, string? providerId)
        => mapping.ProviderIds is not { Count: > 0 } scope
            || (providerId is not null && scope.Contains(providerId, StringComparer.Ordinal));

    /// <summary>
    /// The mapping-and-group pairs that almost matched, with the reason each one did not.
    /// A near miss is almost always a typo, and the symptom — the user sees nothing, or is
    /// turned away — says nothing about the cause, so callers log these.
    ///
    /// A mapping limited to other providers is a near miss only when the provider sent
    /// exactly its group and nothing else matched that group: that is the case somebody
    /// will ask about, and it is also what a second provider claiming the first one's
    /// administrator group looks like (#340). A typo in a mapping that does not apply here
    /// would not apply after it was fixed either, so it is not mentioned. Neither is a
    /// group that another mapping, one for this provider, did match: two providers each
    /// with their own mapping of one name is an ordinary setup, not a mistake.
    /// </summary>
    public static IEnumerable<(string Mapping, string Arrived, string Reason)> NearMisses(
        PluginConfiguration config, OidcIdentity identity, List<GroupMappingConfig> matched)
        => from mapping in config.GroupMappings.Except(matched)
           from arrived in identity.Groups
           let reason = AppliesTo(mapping, identity.ProviderId)
               ? MissReason(mapping.Group, arrived)
               : OtherProvidersReason(mapping.Group, arrived, matched)
           where reason is not null
           select (mapping.Group, arrived, reason);

    /// <summary>
    /// Why a mapping limited to other providers did not match a group of the same name, or
    /// null when there is nothing worth saying; see <see cref="NearMisses"/>.
    /// </summary>
    private static string? OtherProvidersReason(
        string mapping, string arrived, List<GroupMappingConfig> matched)
        => string.Equals(mapping, arrived, StringComparison.Ordinal)
            && !matched.Any(m => string.Equals(m.Group, arrived, StringComparison.Ordinal))
                ? "the mapping applies only to logins through other providers"
                : null;

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
            return "the names differ only in case, and matching is exact";
        }

        var lastSegment = arrived[(arrived.LastIndexOf('/') + 1)..];
        if (arrived.Contains('/', StringComparison.Ordinal)
            && string.Equals(mapping, lastSegment, StringComparison.OrdinalIgnoreCase))
        {
            return "the provider sent a path and the mapping names only its last part "
                + "(Keycloak does this with \"Full group path\" left on), and matching is exact";
        }

        return null;
    }
}
