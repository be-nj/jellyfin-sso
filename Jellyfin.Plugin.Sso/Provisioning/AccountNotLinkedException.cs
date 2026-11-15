namespace Jellyfin.Plugin.Sso.Provisioning;

/// <summary>
/// The provider vouched for somebody this server has no account for, at a provider that
/// is set not to create one. Nothing is misconfigured and nothing was written; the answer
/// is a link, made by an administrator or by the person from an account they already
/// have. Kept apart from InvalidOperationException for the same reason as
/// <see cref="LoginRaceException"/>: the callback reports that one as a configuration
/// problem.
/// </summary>
public sealed class AccountNotLinkedException() : Exception(
    "No Jellyfin account is linked to this identity, and the provider does not create one.");
