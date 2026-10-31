using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// Why a login was turned away, in parts a caller can log separately. The reason is one of
/// a fixed set of phrases, and anything the provider chose — group names — stays in
/// <see cref="NearMisses"/>, so a log line never has token text spliced into its message.
/// </summary>
public sealed record LoginRefusal(
    string Reason,
    IReadOnlyList<(string Mapping, string Arrived, string Reason)> NearMisses);

/// <summary>
/// Whether a login that matches no group mapping may proceed.
///
/// By default it may: the provider decides who reaches Jellyfin, the groups decide what
/// they see, and somebody in no mapped group gets an account with no libraries. That is
/// defensible, and it is also how an account quietly appears for whoever the provider let
/// through — so an operator can ask for the stricter rule instead, where a login has to
/// match a mapping to happen at all.
///
/// The decision lives here, away from the controller, because it is the kind of rule that
/// has to be readable and tested on its own: every branch of it either lets somebody into
/// a media server or keeps them out.
/// </summary>
public static class LoginGate
{
    /// <summary>What to tell the log, or null when the login may go ahead.</summary>
    public static LoginRefusal? Refusal(
        PluginConfiguration config, OidcIdentity identity, Guid? existingUserId)
    {
        if (!config.RequireMappedGroup)
        {
            return null;
        }

        // The same exemption the group sync honours: an account an administrator took out
        // of the plugin's hands keeps what it has, including the ability to log in.
        if (existingUserId is { } userId && config.SyncExcludedUserIds.Contains(userId))
        {
            return null;
        }

        var matched = GroupMatching.Matching(config, identity);
        if (matched.Count > 0)
        {
            return null;
        }

        // An operator who turns this on without any mapping has asked for a server nobody
        // can sign into through SSO. Saying so is more use than guessing they meant the
        // opposite: a switch that quietly lets everybody in while its list is empty is the
        // kind of thing somebody relies on at exactly the wrong moment.
        if (config.GroupMappings.Count == 0)
        {
            return new LoginRefusal("no group mappings are configured, so no login can match one", []);
        }

        if (identity.Groups.Count == 0)
        {
            return new LoginRefusal("the provider sent no groups", []);
        }

        return new LoginRefusal(
            "none of the groups the provider sent matches a group mapping",
            GroupMatching.NearMisses(config, identity, matched).ToList());
    }
}
