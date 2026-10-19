using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The login button lives in Jellyfin's login disclaimer, which belongs to the
/// administrator. The plugin may only own the part between its markers.
/// </summary>
public sealed class LoginButtonMarkupTests
{
    private static ProviderConfig Provider(string id, string name)
        => new() { Id = id, Name = name, Enabled = true };

    [Fact]
    public void Each_enabled_provider_gets_its_own_button()
    {
        var block = LoginButtonMarkup.Build(
            [Provider("p1", "Authentik"), Provider("p2", "Keycloak")], basePath: string.Empty);

        Assert.Contains("href=\"/sso/oidc/start/p1\"", block);
        Assert.Contains("href=\"/sso/oidc/start/p2\"", block);
        Assert.Contains("Login with Authentik", block);
        Assert.Contains("Login with Keycloak", block);
    }

    [Fact]
    public void A_server_under_a_sub_path_gets_prefixed_links()
    {
        var block = LoginButtonMarkup.Build([Provider("p1", "Authentik")], basePath: "/jellyfin");

        Assert.Contains("href=\"/jellyfin/sso/oidc/start/p1\"", block);
    }

    [Theory]
    [InlineData("jellyfin")]
    [InlineData("/jellyfin/")]
    public void The_sub_path_is_normalised(string basePath)
    {
        Assert.Contains(
            "href=\"/jellyfin/sso/oidc/start/p1\"",
            LoginButtonMarkup.Build([Provider("p1", "Authentik")], basePath));
    }

    [Fact]
    public void A_provider_name_cannot_inject_markup()
    {
        var block = LoginButtonMarkup.Build(
            [Provider("p1", "<script>alert(1)</script>")], basePath: string.Empty);

        Assert.DoesNotContain("<script>", block);
        Assert.Contains("&lt;script&gt;", block);
    }

    [Fact]
    public void The_administrators_own_disclaimer_survives()
    {
        const string House = "<p>Be nice to each other.</p>";
        var block = LoginButtonMarkup.Build([Provider("p1", "Authentik")], basePath: string.Empty);

        var spliced = LoginButtonMarkup.Splice(House, block);

        Assert.StartsWith(House, spliced);
        Assert.Contains("/sso/oidc/start/p1", spliced);
    }

    [Fact]
    public void A_second_run_replaces_the_block_instead_of_stacking_buttons()
    {
        const string House = "<p>Be nice to each other.</p>";
        var first = LoginButtonMarkup.Splice(
            House, LoginButtonMarkup.Build([Provider("p1", "Authentik")], string.Empty));

        var second = LoginButtonMarkup.Splice(
            first, LoginButtonMarkup.Build([Provider("p2", "Keycloak")], string.Empty));

        Assert.StartsWith(House, second);
        Assert.DoesNotContain("/sso/oidc/start/p1", second);
        Assert.Contains("/sso/oidc/start/p2", second);
        Assert.Equal(1, Occurrences(second, LoginButtonMarkup.Start));
    }

    [Fact]
    public void Disabling_every_provider_removes_only_the_block()
    {
        const string House = "<p>Be nice to each other.</p>";
        var withButton = LoginButtonMarkup.Splice(
            House, LoginButtonMarkup.Build([Provider("p1", "Authentik")], string.Empty));

        var cleared = LoginButtonMarkup.Splice(withButton, LoginButtonMarkup.Build([], string.Empty));

        Assert.Equal(House, cleared);
    }

    [Fact]
    public void Nothing_is_written_when_there_is_nothing_to_write()
    {
        Assert.Equal(string.Empty, LoginButtonMarkup.Splice(null, LoginButtonMarkup.Build([], string.Empty)));
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
