using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The pages the login flow serves run outside jellyfin-web, which means nothing else
/// escapes what they print. Every value on them comes from either the provider
/// configuration or the identity provider, so the tests pin down that a name, a code or a
/// username cannot leave the place it was printed into.
/// </summary>
public sealed class SsoPagesTests
{
    private static ProviderConfig Provider(string id = "p1", string name = "Authentik")
        => new() { Id = id, Name = name, Enabled = true, Authority = "https://idp.example.com" };

    [Fact]
    public void Every_page_carries_the_shared_frame()
    {
        foreach (var page in new[]
                 {
                     SsoPages.ProviderPicker(string.Empty, [Provider()], string.Empty),
                     SsoPages.QuickConnectApproved(),
                     SsoPages.LinkResult(string.Empty, "Linked.", success: true),
                     SsoPages.SelfServiceLink(string.Empty, [Provider()]),
                     SsoPages.ApproveQuickConnect(string.Empty, "ben", "123456", "token"),
                     SsoPages.CompleteWebLogin(string.Empty, "ben", "secret"),
                 })
        {
            Assert.StartsWith("<!DOCTYPE html>", page, StringComparison.Ordinal);
            Assert.Contains("name=\"viewport\"", page, StringComparison.Ordinal);
            Assert.Contains("background:#1c1c1c", page, StringComparison.Ordinal);
            // A card taller than the window has to scroll rather than be cut off.
            Assert.Contains("min-height:100vh", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_picker_offers_one_link_per_provider_under_the_published_path()
    {
        var page = SsoPages.ProviderPicker(
            "/jellyfin",
            [Provider("first", "Authentik"), Provider("second", "Keycloak")],
            queryString: "?code=ABC123");

        Assert.Contains(
            "href=\"/jellyfin/sso/oidc/start/first?code=ABC123\"", page, StringComparison.Ordinal);
        Assert.Contains(
            "href=\"/jellyfin/sso/oidc/start/second?code=ABC123\"", page, StringComparison.Ordinal);
        Assert.Contains(">Authentik</a>", page, StringComparison.Ordinal);
        Assert.Contains(">Keycloak</a>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_id_that_needs_encoding_is_encoded_in_the_pickers_links()
    {
        var page = SsoPages.ProviderPicker(string.Empty, [Provider("a b&c")], string.Empty);

        Assert.Contains("/sso/oidc/start/a%20b%26c", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_name_cannot_close_the_tag_it_is_printed_in()
    {
        var hostile = "</a><script>alert(1)</script>";

        var picker = SsoPages.ProviderPicker(string.Empty, [Provider(name: hostile)], string.Empty);
        var link = SsoPages.SelfServiceLink(string.Empty, [Provider(name: hostile)]);

        Assert.DoesNotContain("<script>alert(1)</script>", picker, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", link, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", picker, StringComparison.Ordinal);
    }

    [Fact]
    public void The_link_page_posts_the_chosen_provider_to_the_linking_endpoint()
    {
        var page = SsoPages.SelfServiceLink("/jellyfin", [Provider("p1", "Authentik")]);

        Assert.Contains("/jellyfin/sso/link/start?providerId=", page, StringComparison.Ordinal);
        // The option value is a JavaScript literal, quotes included, not bare text.
        Assert.Contains("<option value=\"p1\">Authentik</option>", page, StringComparison.Ordinal);
        Assert.Contains("MediaBrowser Token=", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_login_page_passes_the_credentials_as_javascript_literals()
    {
        var page = SsoPages.CompleteWebLogin("/jellyfin", "ben\"); alert(1); //", "s3cret");

        // The quote is escaped, so the username stays a string instead of closing it and
        // running what follows.
        Assert.Contains("Username: \"ben\\u0022); alert(1); //\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ben\"); alert(1);", page, StringComparison.Ordinal);
        Assert.Contains("Pw: \"s3cret\"", page, StringComparison.Ordinal);
        Assert.Contains("/jellyfin/Users/AuthenticateByName", page, StringComparison.Ordinal);
        Assert.Contains("/jellyfin/web/index.html", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_approval_page_shows_the_code_and_carries_the_one_time_token()
    {
        var page = SsoPages.ApproveQuickConnect("/jellyfin", "ben", "123456", "tok&en");

        Assert.Contains("Approve this device?", page, StringComparison.Ordinal);
        Assert.Contains("<strong>ben</strong>", page, StringComparison.Ordinal);
        Assert.Contains(">123456<", page, StringComparison.Ordinal);
        Assert.Contains("value=\"tok&amp;en\"", page, StringComparison.Ordinal);
        Assert.Contains(
            "action=\"/jellyfin/sso/oidc/qc/approve\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "Account linked")]
    [InlineData(false, "Linking failed")]
    public void The_link_result_says_which_of_the_two_happened(bool success, string heading)
    {
        var page = SsoPages.LinkResult(string.Empty, "A message.", success);

        Assert.Contains($"<h1>{heading}</h1>", page, StringComparison.Ordinal);
        Assert.Contains("A message.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_in_form_script_describes_one_button_per_provider()
    {
        var script = SsoPages.LoginButtonScript(
            "/jellyfin", [Provider("p1", "Authentik"), Provider("p2", string.Empty)]);

        Assert.Contains("\"href\":\"/jellyfin/sso/oidc/start/p1\"", script, StringComparison.Ordinal);
        Assert.Contains("\"icon\":\"/jellyfin/sso/icon/authentik\"", script, StringComparison.Ordinal);
        // A provider nobody named still needs a label on its button.
        Assert.Contains("\"name\":\"SSO\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_in_form_script_looks_for_the_login_page_and_nothing_else()
    {
        var script = SsoPages.LoginButtonScript(string.Empty, [Provider("p1", "Authentik")]);

        Assert.Contains("'#loginPage, .loginPage'", script, StringComparison.Ordinal);

        // What put a "Login with …" button at the bottom of the profile page: any page
        // with a password field counted as the login page, and any form on it as the
        // login form.
        Assert.DoesNotContain("input[type=\"password\"]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector('form')", script, StringComparison.Ordinal);
    }
}
