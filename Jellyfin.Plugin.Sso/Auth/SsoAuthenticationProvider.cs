using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// IAuthenticationProvider bridge: validates an ephemeral secret produced after OIDC login
/// and returns the bound username so Jellyfin can mint its own access token.
/// </summary>
public sealed class SsoAuthenticationProvider : IAuthenticationProvider
{
    private readonly EphemeralSecretStore _secrets;
    private readonly IServiceProvider _services;

    public SsoAuthenticationProvider(EphemeralSecretStore secrets, IServiceProvider services)
    {
        _secrets = secrets;
        _services = services;
    }

    /// <summary>
    /// Jellyfin's own password-reset provider, by the full type name Jellyfin 12 looks it
    /// up with. An unknown name falls back to the same provider, so a stale one did no
    /// harm, but every writer of the policy uses this one.
    /// </summary>
    internal const string DefaultPasswordResetProviderId =
        "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider";

    public string Name => "SSO";
    public bool IsEnabled => true;

    public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
    {
        var bound = _secrets.ConsumeEntry(password);
        // Resolve lazily: IUserManager constructs the authentication providers itself.
        if (bound is null || !string.Equals(bound.Username, username, StringComparison.Ordinal)
            || _services.GetRequiredService<IUserManager>().GetUserByName(username)?.Id != bound.UserId)
            throw new AuthenticationException("Invalid or expired SSO secret.");

        return Task.FromResult(new ProviderAuthenticationResult { Username = bound.Username });
    }

    public Task ChangePassword(User user, string newPassword)
        => throw new NotSupportedException("Password changes are managed by the external IdP.");
}
