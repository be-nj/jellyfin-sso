using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The mark on the login button is picked from what the administrator typed, so a
/// household never configures it. These pin which spellings are recognised, and that an
/// unknown provider still gets a button with something on it.
/// </summary>
public sealed class ProviderIconsTests
{
    [Theory]
    [InlineData("Authentik", "", "authentik")]
    // A renamed provider is still recognised by the path it issues from: only authentik
    // serves /application/o/, only Keycloak serves /realms/. A household that calls its
    // provider "Our SSO" on auth.example.com otherwise gets the plugin's own mark and,
    // on a branded button, the plugin's own colour.
    [InlineData("Our SSO", "https://auth.example.com/application/o/jellyfin/", "authentik")]
    [InlineData("", "https://authentik.example.com/application/o/jf/", "authentik")]
    [InlineData("Keycloak", "", "keycloak")]
    [InlineData("", "https://kc.example.com/realms/home", "keycloak")]
    [InlineData("Authelia", "", "authelia")]
    [InlineData("Work account", "https://accounts.google.com", "google")]
    [InlineData("Okta", "", "okta")]
    [InlineData("auth0", "", "auth0")]
    [InlineData("Something else", "https://sso.example.com", "generic")]
    [InlineData(null, null, "generic")]
    public void The_mark_follows_the_name_or_the_issuer(string? name, string? authority, string expected)
        => Assert.Equal(expected, ProviderIcons.SlugFor(name, authority));

    [Theory]
    [InlineData("authentik")]
    [InlineData("keycloak")]
    [InlineData("authelia")]
    [InlineData("google")]
    [InlineData("okta")]
    [InlineData("auth0")]
    [InlineData("generic")]
    [InlineData("nothing-like-it")]
    public void Every_slug_answers_with_a_drawable_svg(string slug)
    {
        var svg = ProviderIcons.Svg(slug);

        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.EndsWith("</svg>", svg);
        Assert.Contains("viewBox=\"0 0 24 24\"", svg);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);

        // A mark with no colour or no path data renders as nothing, or as a stray dot —
        // which is exactly what shipped once, because the wrapper was all that was
        // checked here.
        Assert.Matches("(fill|stroke)=\"#[0-9A-Fa-f]{6}\"", svg);
        Assert.Matches("(d|cx)=\"[A-Za-z0-9][^\"]{3,}\"", svg);
        Assert.DoesNotContain("=\"0\"", svg);
        Assert.DoesNotContain("=\"1\"", svg);
    }

    [Fact]
    public void The_button_asks_the_server_for_its_provider_s_mark()
    {
        var block = LoginButtonMarkup.Build(
            [new ProviderConfig { Id = "p1", Name = "Keycloak", Enabled = true }],
            basePath: string.Empty);

        Assert.Contains("<img src=\"/sso/icon/provider/p1\"", block);
        Assert.Contains("height=\"22\"", block);
    }

    [Fact]
    public void The_mark_is_not_given_a_width_it_has_to_fit()
    {
        var block = LoginButtonMarkup.Build(
            [new ProviderConfig { Id = "p1", Name = "Authentik", Enabled = true }],
            basePath: string.Empty);

        // The picture behind that URL is whatever the provider serves — Authentik's own
        // default brand image is a wordmark four times as wide as it is tall. Forced into
        // a square it collapsed into a coloured dash, so the width is left to the image.
        Assert.DoesNotContain("width=\"22\"", block);
    }

    [Fact]
    public void A_mark_that_is_not_square_keeps_its_shape()
    {
        var css = LoginButtonMarkup.Css(anyProvider: true);

        Assert.Contains(".sso-login-mark{", css, StringComparison.Ordinal);
        Assert.Contains("height:22px", css, StringComparison.Ordinal);
        Assert.Contains("width:auto", css, StringComparison.Ordinal);
        Assert.Contains("object-fit:contain", css, StringComparison.Ordinal);
        // A square mark still occupies its place before the picture arrives, so the
        // label does not jump sideways when it does.
        Assert.Contains("min-width:22px", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Under_a_sub_path_the_mark_is_fetched_from_there_too()
    {
        var block = LoginButtonMarkup.Build(
            [new ProviderConfig { Id = "p1", Name = "Authentik", Enabled = true }],
            basePath: "/jellyfin");

        Assert.Contains("<img src=\"/jellyfin/sso/icon/provider/p1\"", block);
    }
}
