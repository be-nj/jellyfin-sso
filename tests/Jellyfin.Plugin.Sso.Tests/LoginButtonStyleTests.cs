using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The two looks a login button can have. Off, it is one of Jellyfin's buttons with the
/// provider's colour on its edge; on, it is the provider's colour, and then the mark and
/// the label have to stay readable on it — which is the part worth testing, because the
/// provider picks the colour and some of them are light.
/// </summary>
public sealed class LoginButtonStyleTests
{
    private static ProviderConfig Provider(string name, bool branded)
        => new() { Id = "p1", Name = name, Enabled = true, BrandedLoginButton = branded };

    [Fact]
    public void A_plain_button_carries_the_colour_on_its_edge_only()
    {
        var css = LoginButtonMarkup.Css(anyProvider: true, [Provider("Authentik", branded: false)]);

        Assert.Contains("--sso-mark:#FD4B2D", css);
        Assert.DoesNotContain("background:#FD4B2D", css);
        Assert.DoesNotContain("filter:brightness(0)", css);
    }

    [Fact]
    public void A_branded_button_is_painted_in_the_providers_colour()
    {
        var css = LoginButtonMarkup.Css(anyProvider: true, [Provider("Authentik", branded: true)]);

        Assert.Contains("background:#FD4B2D !important", css);
        Assert.Contains("[data-provider=\"p1\"]:hover{background:#", css);
    }

    [Theory]
    // Keycloak's grey is dark; its white mark stays white, and so does the label.
    [InlineData("Keycloak", "#ffffff", " invert(1)")]
    // Authentik's orange and Authelia's blue are both light enough that black reads
    // better on them than white does — which is why this is measured rather than chosen.
    [InlineData("Authentik", "#101418", "")]
    [InlineData("Authelia", "#101418", "")]
    public void The_mark_and_the_label_are_turned_to_whichever_reads(
        string provider, string ink, string inversion)
    {
        var css = LoginButtonMarkup.Css(anyProvider: true, [Provider(provider, branded: true)]);

        Assert.Contains("color:" + ink + " !important", css);
        Assert.Contains(".sso-login-label{color:" + ink + ";}", css);
        Assert.Contains(".sso-login-mark{filter:brightness(0)" + inversion + ";}", css);
    }

    [Fact]
    public void A_provider_nobody_recognises_is_painted_in_the_plugins_own_colour()
    {
        var css = LoginButtonMarkup.Css(anyProvider: true, [Provider("Our own IdP", branded: true)]);

        Assert.Contains("background:#00A4DC !important", css);
    }

    [Fact]
    public void Two_providers_can_disagree_about_the_look()
    {
        var css = LoginButtonMarkup.Css(
            anyProvider: true,
            [
                new ProviderConfig { Id = "plain", Name = "Keycloak", Enabled = true },
                new ProviderConfig
                {
                    Id = "loud", Name = "Authentik", Enabled = true, BrandedLoginButton = true,
                },
            ]);

        Assert.Contains("[data-provider=\"plain\"]{--sso-mark:#FFFFFF;}", css);
        Assert.Contains("[data-provider=\"loud\"]{--sso-mark:#FD4B2D;background:#FD4B2D", css);
    }

    [Fact]
    public void The_hover_colour_is_a_shade_of_the_brand_rather_than_the_default_grey()
    {
        var css = LoginButtonMarkup.Css(anyProvider: true, [Provider("Authentik", branded: true)]);

        // #FD4B2D darkened: still recognisably the same colour, visibly pressed.
        Assert.Contains(":hover{background:#da4027 !important;}", css);
    }

    [Theory]
    [InlineData("#000000", "#ffffff")]
    [InlineData("#ffffff", "#101418")]
    [InlineData("#666666", "#ffffff")]
    [InlineData("#999999", "#101418")]
    public void Readability_follows_the_luminance_of_the_surface(string background, string ink)
        => Assert.Equal(ink, ProviderIcons.ReadableOn(background));

    [Fact]
    public void A_colour_that_is_already_black_is_lightened_instead_of_darkened()
    {
        Assert.Equal("#1c1c1c", ProviderIcons.HoverShadeOf("#000000"));
        Assert.Equal("#282828", ProviderIcons.HoverShadeOf("#0c0c0c"));
    }

    [Fact]
    public void A_fetched_logo_keeps_its_colours_and_gets_something_to_sit_on()
    {
        var css = LoginButtonMarkup.Css(
            anyProvider: true, [Provider("Authentik", branded: true)], providerLogos: true);

        // brightness(0) turns a logo with an opaque background into a black rectangle,
        // which is what a household saw on its own Authentik's logo.
        Assert.DoesNotContain("filter:brightness(0)", css);
        Assert.Contains(".sso-login-mark{background:#fff;", css);
    }

    [Fact]
    public void The_plugins_own_mark_is_still_recoloured()
    {
        var css = LoginButtonMarkup.Css(
            anyProvider: true, [Provider("Keycloak", branded: true)], providerLogos: false);

        Assert.Contains(".sso-login-mark{filter:brightness(0) invert(1);}", css);
    }

    [Fact]
    public void An_administrator_can_name_the_colour_themselves()
    {
        var provider = Provider("Our own IdP", branded: true);
        provider.ButtonColour = "#7B2D8E";

        var css = LoginButtonMarkup.Css(anyProvider: true, [provider]);

        Assert.Contains("background:#7B2D8E !important", css);
        Assert.Contains("color:#ffffff !important", css);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#ff0000;}body{display:none")]
    public void A_colour_that_is_not_a_colour_is_ignored_rather_than_written_into_the_css(
        string typed)
    {
        var provider = Provider("Authentik", branded: true);
        provider.ButtonColour = typed;

        var css = LoginButtonMarkup.Css(anyProvider: true, [provider]);

        Assert.DoesNotContain(typed.Trim(), css, StringComparison.Ordinal);
        Assert.Contains("background:#FD4B2D !important", css);
    }

    [Theory]
    [InlineData("#abc", "#abc")]
    [InlineData(" #AABBCC ", "#AABBCC")]
    [InlineData("#abcd", null)]
    [InlineData("rgb(1,2,3)", null)]
    [InlineData(null, null)]
    public void A_typed_colour_is_accepted_only_as_hex(string? typed, string? expected)
        => Assert.Equal(expected, ProviderIcons.SanitiseColour(typed));

    [Fact]
    public void Nonsense_instead_of_a_colour_never_throws()
    {
        Assert.Equal("#ffffff", ProviderIcons.ReadableOn("not a colour"));
        Assert.Equal("#1c1c1c", ProviderIcons.HoverShadeOf(string.Empty));
    }
}
