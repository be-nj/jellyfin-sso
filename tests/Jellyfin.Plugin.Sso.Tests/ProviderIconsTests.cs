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
    [InlineData("Our SSO", "https://auth.example.com/application/o/jellyfin/", "generic")]
    [InlineData("", "https://authentik.example.com/application/o/jf/", "authentik")]
    [InlineData("Keycloak", "", "keycloak")]
    [InlineData("", "https://kc.example.com/realms/home", "generic")]
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
    }

    [Fact]
    public void The_button_carries_the_mark_of_its_provider()
    {
        var block = LoginButtonMarkup.Build(
            [new ProviderConfig { Id = "p1", Name = "Keycloak", Enabled = true }],
            basePath: string.Empty);

        Assert.Contains("<img src=\"/sso/icon/keycloak\"", block);
        Assert.Contains("width=\"20\" height=\"20\"", block);
    }

    [Fact]
    public void Under_a_sub_path_the_mark_is_fetched_from_there_too()
    {
        var block = LoginButtonMarkup.Build(
            [new ProviderConfig { Id = "p1", Name = "Authentik", Enabled = true }],
            basePath: "/jellyfin");

        Assert.Contains("<img src=\"/jellyfin/sso/icon/authentik\"", block);
    }
}
