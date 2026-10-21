namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Which container image a provider runs.
///
/// Every day-to-day run uses the pinned tag below the call, so a suite that goes red is
/// about the change under review and nothing else — an identity provider that ships a
/// breaking release on a Tuesday must not turn every pull request that week into a
/// mystery.
///
/// Pinning alone, though, means the suite only ever tests the versions it was written
/// against, and the first person to find out that Keycloak changed something under the
/// plugin would be whoever upgraded their Keycloak. So the scheduled "Upstream" workflow
/// sets these variables to each provider's current release and runs the same tests
/// again: same code, newest software, once a week, in a place where a failure is the
/// plugin's problem to look at rather than a user's.
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// The image named by <paramref name="variable"/>, or <paramref name="pinned"/> when
    /// that variable is unset — which is every run but the scheduled one.
    /// </summary>
    internal static string Resolve(string variable, string pinned)
    {
        var requested = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(requested) ? pinned : requested;
    }
}
