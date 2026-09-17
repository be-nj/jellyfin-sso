using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// IAuthenticationProvider bridge: validates an ephemeral secret produced after OIDC login
/// and returns the bound username so Jellyfin can mint its own access token.
/// </summary>
public sealed class SsoAuthenticationProvider : IAuthenticationProvider
{
    private readonly EphemeralSecretStore _secrets;

    public SsoAuthenticationProvider(EphemeralSecretStore secrets)
    {
        _secrets = secrets;
    }

    public string Name => "SSO";
    public bool IsEnabled => true;

    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        var bound = _secrets.Consume(password);
        if (bound is null || !string.Equals(bound, username, StringComparison.Ordinal))
            throw new AuthenticationException("Invalid or expired SSO secret.");

        return Task.FromResult(new ProviderAuthenticationResult { Username = bound });
    }

    public Task ChangePassword(User user, string newPassword)
        => throw new NotSupportedException("Password changes are managed by the external IdP.");
}
