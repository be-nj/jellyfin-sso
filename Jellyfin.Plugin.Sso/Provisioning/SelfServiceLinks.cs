using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>What attaching an identity to the signed-in account would do.</summary>
internal enum SelfServiceLinkOutcome
{
    /// <summary>Nothing held this identity or this account; the link can be written.</summary>
    Write,

    /// <summary>This identity is already attached to this very account.</summary>
    AlreadyThisAccount,

    /// <summary>
    /// This account already has a link for the provider, under a different identity. The
    /// plugin keeps one identity per account per provider, so there is nothing to add.
    /// </summary>
    AccountAlreadyLinked,

    /// <summary>
    /// Somebody else's account holds this identity. Writing the link would hand that
    /// account's libraries to whoever completed this login.
    /// </summary>
    HeldByAnotherAccount,
}

/// <summary>
/// Which of the four things a self-service link request is, decided before anything is
/// written.
/// </summary>
/// <param name="Outcome">What the request amounts to.</param>
/// <param name="Owner">
/// The account that already holds the identity, when <see cref="Outcome"/> is
/// <see cref="SelfServiceLinkOutcome.HeldByAnotherAccount"/>; otherwise null.
/// </param>
internal sealed record SelfServiceLinkDecision(SelfServiceLinkOutcome Outcome, Guid? Owner = null);

/// <summary>
/// The rule behind "link my account": a user who has signed in to Jellyfin by any means
/// completes an OIDC login and the identity is attached to the account they were already
/// in. It grants nothing by itself — the libraries follow from the group mappings on the
/// next SSO login — but it does decide which account an identity provider's user is, and
/// the one answer that must never be yes is attaching an identity another account holds.
///
/// Separate from the endpoint that calls it so the four cases can be read back by a test
/// rather than inferred from four pages of HTML.
/// </summary>
internal static class SelfServiceLinks
{
    /// <summary>
    /// Decides and, where the answer is to write, writes — both under the configuration
    /// lock, which is what makes the decision worth anything.
    ///
    /// <see cref="Decide"/> on its own is a reading of a moment that has passed by the time
    /// anything acts on it. Two callbacks completing together both read "nothing holds this
    /// identity and nothing holds this account", and both then added a link: one account
    /// with two identities for one provider, or two accounts holding one identity, after
    /// which which account an identity reaches depends on the order of a list. The
    /// administrator's own linking endpoint has always done its checks inside this lock;
    /// this is the same rule for the callback.
    /// </summary>
    internal static SelfServiceLinkDecision Attach(
        PluginConfiguration config, string providerId, string sub, Guid userId)
    {
        lock (Plugin.ConfigurationLock)
        {
            var decision = Decide(config, providerId, sub, userId);
            if (decision.Outcome != SelfServiceLinkOutcome.Write)
            {
                return decision;
            }

            config.Links.Add(new LinkConfig
            {
                ProviderId = providerId,
                Sub = sub,
                JellyfinUserId = userId,
            });
            Plugin.Instance!.SaveConfiguration();

            return decision;
        }
    }

    internal static SelfServiceLinkDecision Decide(
        PluginConfiguration config, string providerId, string sub, Guid userId)
    {
        var existing = config.Links.FirstOrDefault(l =>
            string.Equals(l.ProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(l.Sub, sub, StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing.JellyfinUserId == userId
                ? new SelfServiceLinkDecision(SelfServiceLinkOutcome.AlreadyThisAccount)
                : new SelfServiceLinkDecision(
                    SelfServiceLinkOutcome.HeldByAnotherAccount, existing.JellyfinUserId);
        }

        var alreadyLinked = config.Links.Any(l =>
            string.Equals(l.ProviderId, providerId, StringComparison.Ordinal)
            && l.JellyfinUserId == userId);

        return alreadyLinked
            ? new SelfServiceLinkDecision(SelfServiceLinkOutcome.AccountAlreadyLinked)
            : new SelfServiceLinkDecision(SelfServiceLinkOutcome.Write);
    }
}
