namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// What <see cref="BrowserSupport.IsLoginPage"/> takes for the login page. The first
/// address is the one #282's failures ended on, which the assertion it replaces let pass.
/// </summary>
public class LoginPageAddressTests
{
    [Theory]
    [InlineData("http://localhost:8096/web/#/login?serverid=5f0c&url=%2Fhome")]
    [InlineData("http://localhost:8096/web/index.html#/login.html")]
    [InlineData("https://host.docker.internal:26176/jellyfin/web/#/login")]
    public void The_login_route_of_a_new_and_of_an_old_web_client_is_the_login_page(string url)
        => Assert.True(BrowserSupport.IsLoginPage(url));

    [Theory]
    [InlineData("http://localhost:8096/web/#/home")]
    [InlineData("http://localhost:8096/web/index.html")]
    [InlineData("http://localhost:8096/web/index.html#/home.html")]
    // The provider's own sign-in page is not Jellyfin's, whatever its path says.
    [InlineData("https://idp.example.com/login?next=%2Fauthorize")]
    public void Anything_else_is_not(string url)
        => Assert.False(BrowserSupport.IsLoginPage(url));
}
