namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The one piece of <see cref="PageConsole"/> that is a decision rather than plumbing:
/// which console output means the web client gave up rather than being slow. It needs no
/// browser, so it runs with the unit tier's speed inside the end-to-end project, next to
/// the code it is about.
///
/// The three lines below are the ones jellyfin-web 12.2 logged on main, and the ordering
/// matters: the first appeared in the runs that passed on 12.0 and 12.1 as well, so
/// treating it as the signal would call a healthy run a crash.
/// </summary>
public class PageConsoleTests
{
    [Fact]
    public void The_routers_own_line_is_what_counts_as_a_crash()
    {
        Assert.True(PageConsole.ClientCrashedWhileRendering(
        [
            "error: React Router caught the following error during render TypeError: "
            + "t.value.pause is not a function",
        ]));
    }

    [Fact]
    public void A_null_page_reference_on_its_own_is_not_a_crash()
    {
        // Logged by every run of the profile-page test, including the two that passed.
        Assert.False(PageConsole.ClientCrashedWhileRendering(
            ["error: [userprofile] Unexpected null page reference"]));
    }

    [Fact]
    public void A_type_error_on_its_own_is_not_a_crash()
    {
        // A page can log one of these and carry on rendering. What the test waits for
        // being absent is the symptom; the router having given up is the cause.
        Assert.False(PageConsole.ClientCrashedWhileRendering(
            ["error: TypeError: t.value.pause is not a function"]));
    }

    [Fact]
    public void An_empty_console_is_not_a_crash()
    {
        Assert.False(PageConsole.ClientCrashedWhileRendering([]));
    }
}
