using Jellyfin.Plugin.Sso.Controllers;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The page somebody without an account lands on. It is the only place that says where
/// the self-service linking page is, so it has to say it — and only while that page
/// would let them in, because an address that answers "linking is switched off" is a
/// dead end handed to somebody who has just hit one.
/// </summary>
public sealed class NoAccountMessageTests : IDisposable
{
    private readonly PluginHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void The_linking_page_is_named_by_the_address_the_person_came_in_on()
    {
        _harness.Configuration.AllowSelfServiceLinking = true;

        var message = SsoCallbackController.NoAccountMessage("https://media.example/jellyfin");

        Assert.Contains("does not create one at this provider", message, StringComparison.Ordinal);
        Assert.Contains("open https://media.example/jellyfin/sso/link to link", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_linking_page_is_not_offered_while_self_service_linking_is_off()
    {
        _harness.Configuration.AllowSelfServiceLinking = false;

        var message = SsoCallbackController.NoAccountMessage("https://media.example");

        Assert.Contains("Ask whoever runs it to link your account.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("/sso/link", message, StringComparison.Ordinal);
    }
}
