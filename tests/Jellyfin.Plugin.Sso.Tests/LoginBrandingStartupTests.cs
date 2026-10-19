using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The buttons are written when the configuration is saved — and a plugin update saves
/// nothing, so a server that was set up weeks ago would come back from an update with no
/// button on its login page. This covers the second occasion: server start.
/// </summary>
public sealed class LoginBrandingStartupTests : IDisposable
{
    private readonly PluginHarness _harness = new();
    private readonly IServerConfigurationManager _configManager = Substitute.For<IServerConfigurationManager>();
    private readonly BrandingOptions _branding = new();

    public LoginBrandingStartupTests()
    {
        _configManager.GetConfiguration("branding").Returns(_branding);
        _configManager.GetConfiguration("network")
            .Returns(new MediaBrowser.Common.Net.NetworkConfiguration());
        _configManager
            .When(m => m.SaveConfiguration("branding", Arg.Any<BrandingOptions>()))
            .Do(call => _branding.LoginDisclaimer = ((BrandingOptions)call[1]).LoginDisclaimer);
    }

    private LoginBrandingStartup Startup()
        => new(
            new LoginBranding(_configManager, NullLogger<LoginBranding>.Instance),
            NullLogger<LoginBrandingStartup>.Instance);

    [Fact]
    public async Task Starting_the_server_writes_the_buttons()
    {
        _harness.Configuration.Providers.Add(new ProviderConfig
        {
            Id = "p1",
            Name = "Authentik",
            Enabled = true,
        });

        await Startup().StartAsync(CancellationToken.None);

        Assert.Contains("/sso/oidc/start/p1", _branding.LoginDisclaimer);
    }

    [Fact]
    public async Task A_failure_while_writing_them_does_not_keep_the_server_down()
    {
        _configManager.GetConfiguration("branding").Returns(_ => throw new InvalidOperationException("no branding"));

        await Startup().StartAsync(CancellationToken.None);
    }

    public void Dispose() => _harness.Dispose();
}
