using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using NSubstitute;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The authentication provider is the bridge that makes Jellyfin mint a token. Anything
/// it accepts is a login, so it must accept exactly one thing: a freshly issued secret
/// presented by the user it was issued for.
/// </summary>
public sealed class SsoAuthenticationProviderTests
{
    private readonly EphemeralSecretStore _secrets = new();
    private readonly SsoAuthenticationProvider _provider;
    private readonly User _user = new("ben", "sso", "reset");
    private readonly IUserManager _users = Substitute.For<IUserManager>();

    public SsoAuthenticationProviderTests()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IUserManager)).Returns(_users);
        _users.GetUserByName("ben").Returns(_user);
        _provider = new SsoAuthenticationProvider(_secrets, services);
    }

    [Fact]
    public async Task A_freshly_issued_secret_authenticates_its_own_user()
    {
        var secret = _secrets.Issue(_user.Id, "ben");

        var result = await _provider.Authenticate("ben", secret);

        Assert.Equal("ben", result.Username);
    }

    [Fact]
    public async Task A_secret_cannot_be_used_twice()
    {
        var secret = _secrets.Issue(_user.Id, "ben");
        await _provider.Authenticate("ben", secret);

        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("ben", secret));
    }

    [Fact]
    public async Task A_secret_issued_for_someone_else_does_not_authenticate()
    {
        var secret = _secrets.Issue(_user.Id, "ben");

        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("admin", secret));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("guessed-secret")]
    public async Task An_empty_or_guessed_password_does_not_authenticate(string password)
    {
        // SSO accounts have no password. An empty one must never pass, which is the
        // failure mode that made password-less accounts a problem in the old plugin.
        _secrets.Issue(_user.Id, "ben");

        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("ben", password));
    }

    [Fact]
    public async Task Username_matching_is_case_sensitive()
    {
        var secret = _secrets.Issue(_user.Id, "ben");

        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("Ben", secret));
    }

    [Fact]
    public void Each_issued_secret_is_distinct()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => _secrets.Issue(_user.Id, "ben")).ToList();

        Assert.Equal(secrets.Count, secrets.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Consuming_an_unknown_secret_returns_null()
    {
        Assert.Null(_secrets.Consume("never-issued"));
    }

    [Fact]
    public async Task Password_changes_are_refused()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => _provider.ChangePassword(null!, "hunter2"));
    }

    [Fact]
    public async Task Reusing_a_deleted_accounts_username_does_not_reuse_its_secret()
    {
        var secret = _secrets.Issue(_user.Id, "ben");
        _users.GetUserByName("ben").Returns(new User("ben", "sso", "reset"));
        await Assert.ThrowsAsync<AuthenticationException>(() => _provider.Authenticate("ben", secret));
    }
}
