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

    /// <summary>
    /// jellyfin-web writes its stored credentials to the console, and failure messages
    /// print the console. What a token looks like in each place it appears is masked; the
    /// rest of the line stays readable.
    /// </summary>
    [Theory]
    [InlineData(
        "log: Stored JSON credentials: {\"Servers\":[{\"AccessToken\":\"0123456789abcdef0123456789abcdef\",\"UserName\":\"ben\"}]}",
        "log: Stored JSON credentials: {\"Servers\":[{\"AccessToken\":\"***\",\"UserName\":\"ben\"}]}")]
    [InlineData(
        "debug: header MediaBrowser Token=\"0123456789abcdef0123456789abcdef\", Client=\"web\"",
        "debug: header MediaBrowser Token=\"***\", Client=\"web\"")]
    [InlineData(
        "debug: requesting url: http://jf/Videos/1/stream?api_key=0123456789abcdef&Static=true",
        "debug: requesting url: http://jf/Videos/1/stream?api_key=***&Static=true")]
    [InlineData("debug: nothing secret here", "debug: nothing secret here")]
    public void Tokens_in_the_console_are_masked_as_they_are_recorded(string logged, string kept)
        => Assert.Equal(kept, PageConsole.Masked(logged));

    [Fact]
    public void An_empty_console_is_not_a_crash()
    {
        Assert.False(PageConsole.ClientCrashedWhileRendering([]));
    }
}

/// <summary>
/// A failure message the harness raises is printed in CI logs anybody can read, so the
/// access token jellyfin-web puts into its websocket address must not reach it (#351).
/// </summary>
public class FailureMessageTests
{
    [Fact]
    public void An_api_key_in_an_address_is_not_printed()
    {
        var message = BrowserSupport.WithoutApiKeys(
            "refused: GET http://localhost:32769/socket?ApiKey=43899bdcc1454d9c84787002cacf1ceb&deviceId=x -> 401"
            + " | console: ws://h/socket?api_key=0123abcd failed");

        Assert.DoesNotContain("43899bdcc1454d9c84787002cacf1ceb", message);
        Assert.DoesNotContain("0123abcd", message);
        Assert.Contains("ApiKey=***&deviceId=x", message);
    }
}
