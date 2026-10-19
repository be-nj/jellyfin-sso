using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Branding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The login button is written into Jellyfin's own branding, which belongs to the
/// administrator and holds their disclaimer and their custom CSS. Writing it therefore has
/// to be exact in both directions: the button appears when it should, nothing of theirs is
/// lost, and nothing is saved when nothing changed — a save here is a configuration file
/// rewritten and a log line on every server start.
/// </summary>
public sealed class LoginBrandingTests : IDisposable
{
    private readonly PluginHarness _harness = new();
    private readonly IServerConfigurationManager _configManager =
        Substitute.For<IServerConfigurationManager>();

    private readonly BrandingOptions _branding = new();
    private readonly NetworkConfiguration _network = new();

    private int _saves;

    public LoginBrandingTests()
    {
        _configManager.GetConfiguration("branding").Returns(_ => _branding);
        _configManager.GetConfiguration("network").Returns(_ => _network);
        _configManager
            .When(m => m.SaveConfiguration("branding", Arg.Any<BrandingOptions>()))
            .Do(call =>
            {
                _saves++;
                var saved = (BrandingOptions)call[1];
                _branding.LoginDisclaimer = saved.LoginDisclaimer;
                _branding.CustomCss = saved.CustomCss;
            });
    }

    private LoginBranding Branding()
        => new(_configManager, NullLogger<LoginBranding>.Instance);

    private ProviderConfig Provider(string id = "p1", string name = "Authentik", bool enabled = true)
    {
        var provider = new ProviderConfig { Id = id, Name = name, Enabled = enabled };
        _harness.Configuration.Providers.Add(provider);
        return provider;
    }

    [Fact]
    public void An_enabled_provider_gets_a_button_and_the_styling_that_places_it()
    {
        Provider();

        Branding().Apply();

        Assert.Contains("/sso/oidc/start/p1", _branding.LoginDisclaimer);
        Assert.Contains(".sso-login-button", _branding.CustomCss);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public void A_provider_that_is_not_enabled_gets_no_button()
    {
        Provider(enabled: false);

        Branding().Apply();

        Assert.DoesNotContain("/sso/oidc/start/p1", _branding.LoginDisclaimer ?? string.Empty);
        // Nothing to write and nothing written: an empty block in an empty disclaimer is
        // the state it is already in.
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void Turning_the_button_off_removes_it_without_touching_the_rest()
    {
        const string House = "<p>Be nice to each other.</p>";
        const string TheirCss = ".myClass{color:red}";
        _branding.LoginDisclaimer = House;
        _branding.CustomCss = TheirCss;
        Provider();

        Branding().Apply();
        Assert.Contains("/sso/oidc/start/p1", _branding.LoginDisclaimer);

        _harness.Configuration.AddLoginButtonToBranding = false;
        Branding().Apply();

        Assert.Equal(House, _branding.LoginDisclaimer);
        Assert.Equal(TheirCss, _branding.CustomCss);
    }

    [Fact]
    public void Applying_the_same_configuration_twice_saves_once()
    {
        Provider();

        Branding().Apply();
        Branding().Apply();
        Branding().Apply();

        Assert.Equal(1, _saves);
    }

    [Fact]
    public void A_change_to_the_providers_is_written_again()
    {
        var provider = Provider();

        Branding().Apply();
        provider.Name = "Keycloak";
        Branding().Apply();

        Assert.Equal(2, _saves);
        Assert.Contains("Login with Keycloak", _branding.LoginDisclaimer);
        Assert.DoesNotContain("Login with Authentik", _branding.LoginDisclaimer);
    }

    [Fact]
    public void A_change_to_the_styling_alone_is_written_too()
    {
        var provider = Provider();
        provider.BrandedLoginButton = true;
        Branding().Apply();
        var painted = _branding.CustomCss;

        provider.BrandedLoginButton = false;
        Branding().Apply();

        Assert.Equal(2, _saves);
        Assert.NotEqual(painted, _branding.CustomCss);
    }

    [Fact]
    public void A_fetched_logo_is_not_recoloured_while_a_stock_mark_is()
    {
        var provider = Provider();
        provider.BrandedLoginButton = true;

        // The plugin's own marks are flat shapes, so they are recoloured to whatever reads
        // on the painted button.
        _harness.Configuration.UseProviderLogos = false;
        Branding().Apply();
        Assert.Contains("filter:brightness(0)", _branding.CustomCss);

        // The provider's own logo is an image of any shape and colour, and flattening it
        // to one colour turns a rounded logo into a black rectangle.
        _harness.Configuration.UseProviderLogos = true;
        Branding().Apply();
        Assert.DoesNotContain("filter:brightness(0)", _branding.CustomCss);
        Assert.Contains("background:#fff", _branding.CustomCss);
    }

    [Fact]
    public void A_server_published_under_a_sub_path_gets_prefixed_links()
    {
        _network.BaseUrl = "/jellyfin";
        Provider();

        Branding().Apply();

        Assert.Contains("/jellyfin/sso/oidc/start/p1", _branding.LoginDisclaimer);
    }

    [Fact]
    public void Branding_Jellyfin_has_never_written_is_created_rather_than_assumed()
    {
        _configManager.GetConfiguration("branding").Returns(_ => null!);
        Provider();

        Branding().Apply();

        _configManager.Received().SaveConfiguration("branding", Arg.Any<BrandingOptions>());
    }

    public void Dispose() => _harness.Dispose();
}
