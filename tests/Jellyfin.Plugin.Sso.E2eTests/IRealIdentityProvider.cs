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
    /// A second ordinary user, in a different group. Only the recorded demo uses it: two
    /// accounts signing in one after the other is what shows that the groups decide the
    /// shelves, where one account only shows that a login works.
    /// </summary>
    RealIdentityUser KidUser { get; }

    /// <summary>
    /// The address the browser should use for Jellyfin, when the provider is picky about
    /// the redirect URI. Null means the environment's own base URL is fine.
    /// </summary>
    string? ServerBaseUrl { get; }

    /// <summary>
    /// What is on screen once this provider's own login form has arrived. The three look
    /// nothing alike, and the demo recording has to know when the page it is waiting for
    /// is there.
    /// </summary>
    string LoginFormSelector { get; }

    /// <summary>Fills in this provider's own login form.</summary>
    Task SignInAsync(IPage page, RealIdentityUser user);

    /// <summary>
    /// The same login, typed at a pace a viewer can follow, for the recorded demo. The
    /// default is the ordinary fill — instant, which is right for a test and wrong for a
    /// clip — so a provider only overrides it to be watchable.
    /// </summary>
    Task SignInForTheRecordingAsync(IPage page, RealIdentityUser user)
        => SignInAsync(page, user);

    /// <summary>Whatever the provider can say about a failed login; shown when a test fails.</summary>
    Task<string> DiagnosticsAsync() => Task.FromResult(string.Empty);
}
