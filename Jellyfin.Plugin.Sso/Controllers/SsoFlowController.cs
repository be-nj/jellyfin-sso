using Duende.IdentityModel.Client;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// A controller that can send a browser to an identity provider.
///
/// Two of them do: a login and a self-service link are the same request at the provider
/// and differ only in what the plugin writes into the state it keeps. So the request
/// itself is built in one place, and what the two endpoints do with the failures — which
/// are worded for different readers — stays with each of them.
/// </summary>
public abstract class SsoFlowController : SsoControllerBase
{
    private readonly PendingStateStore _states;

    private protected SsoFlowController(PendingStateStore states, OidcClientFactory oidc)
    {
        _states = states;
        Oidc = oidc;
    }

    private protected OidcClientFactory Oidc { get; }

    /// <summary>
    /// Issues the state for a new flow and builds the provider's authorize URL for it.
    /// Throws <see cref="PendingStateStore.TooManyPendingLoginsException"/> when the store
    /// is full, and <see cref="InvalidOperationException"/> when the provider cannot be
    /// reached or is configured wrongly.
    /// </summary>
    private protected async Task<string> AuthorizeUrlAsync(
        ProviderConfig provider,
        CancellationToken ct,
        string? quickConnectCode = null,
        Guid? linkUserId = null)
    {
        var redirectUri = At.CallbackUri(provider.Id);

        // The state travels through the provider and back, so it is not proof of who
        // started the login — anyone can hold one and hand it to somebody else. The cookie
        // is: it goes to this browser and comes back only from this browser.
        var browserId = PendingStateStore.BrowserId(Cookies.Value(SsoCookies.PendingLogin));
        Cookies.RememberPendingLogin(browserId);

        var (state, pending) = _states.Issue(
            provider.Id, redirectUri, browserId, quickConnectCode, linkUserId);

        var disco = await Oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        return new RequestUrl(disco.AuthorizeEndpoint
            ?? throw new InvalidOperationException("Discovery document has no authorize endpoint."))
            .CreateAuthorizeUrl(
                clientId: provider.ClientId,
                responseType: "code",
                scope: string.Join(' ', provider.EffectiveScopes()),
                redirectUri: redirectUri,
                state: state,
                nonce: pending.Nonce,
                codeChallenge: PendingStateStore.CodeChallenge(pending.CodeVerifier),
                codeChallengeMethod: "S256");
    }

    /// <summary>The state this browser started, consumed, or null when it does not match.</summary>
    private protected PendingStateStore.PendingState? ConsumePendingLogin(string state)
        => _states.Consume(state, Cookies.Value(SsoCookies.PendingLogin));
}
