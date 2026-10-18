using Jellyfin.Plugin.Sso.Controllers;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Provider ids and IdP errors come straight from the query string and end up in the
/// server log, where newlines would let a caller forge log entries.
/// </summary>
public sealed class LoggableTests
{
    [Fact]
    public void A_plain_value_is_kept()
    {
        Assert.Equal("provider-1", SsoController.Loggable("provider-1"));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\tb")]
    [InlineData("a\u0000b")]
    public void Control_characters_are_removed(string value)
    {
        Assert.Equal("ab", SsoController.Loggable(value));
    }

    [Fact]
    public void A_forged_log_line_cannot_be_injected()
    {
        var forged = "p\n[12:00:00] [INF] Fake: an administrator logged in";

        var safe = SsoController.Loggable(forged);

        Assert.DoesNotContain('\n', safe);
        Assert.DoesNotContain('\r', safe);
    }

    [Fact]
    public void An_oversized_value_is_truncated()
    {
        var safe = SsoController.Loggable(new string('x', 5000));

        Assert.Equal(201, safe.Length);
        Assert.EndsWith("…", safe);
    }

    [Fact]
    public void Null_and_empty_stay_empty()
    {
        Assert.Equal(string.Empty, SsoController.Loggable(null));
        Assert.Equal(string.Empty, SsoController.Loggable(string.Empty));
    }
}
