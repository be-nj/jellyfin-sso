using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>A user the provider knows, with the groups it will report.</summary>
internal sealed record RealIdentityUser(string Username, string[] Groups);

/// <summary>
/// A real identity provider in a container. The fake provider covers the protocol rules;
/// these cover the parts only a real implementation gets to decide — what ends up in the
/// claims, and what its login page looks like.
/// </summary>
internal interface IRealIdentityProvider : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Discovery base URL, reachable from the server container and the browser.</summary>
    string Authority { get; }

    string ClientId { get; }

    string GroupsClaim { get; }

    /// <summary>Scopes to request — providers disagree about which ones exist.</summary>
    string[] Scopes { get; }

    /// <summary>Whether the groups have to be read from the userinfo endpoint.</summary>
    bool GroupsFromUserInfo { get; }

    /// <summary>A user in a group that maps to a library.</summary>
    RealIdentityUser MemberUser { get; }

    /// <summary>A user in the group that maps to Jellyfin administrator.</summary>
    RealIdentityUser AdminUser { get; }

    /// <summary>
    /// The address the browser should use for Jellyfin, when the provider is picky about
    /// the redirect URI. Null means the environment's own base URL is fine.
    /// </summary>
    string? ServerBaseUrl { get; }

    /// <summary>Fills in this provider's own login form.</summary>
    Task SignInAsync(IPage page, RealIdentityUser user);
}
