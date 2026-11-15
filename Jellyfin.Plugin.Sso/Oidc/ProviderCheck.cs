using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>One line of a provider check: what was looked at, whether it held, and what was found.</summary>
public sealed record ProviderCheckLine(string What, bool Ok, string Detail);

/// <summary>The outcome of checking one provider's saved settings.</summary>
public sealed record ProviderCheckResult(bool Ok, IReadOnlyList<ProviderCheckLine> Lines);

/// <summary>
/// Looks at a provider the way a login is about to, without anybody having to attempt one
/// and read the server log afterwards: are the settings complete, does the provider
/// publish a discovery document the plugin accepts, are there keys, and — for a
/// confidential client — is the secret where the reference says it is.
///
/// It fetches what a login fetches and nothing else, from the provider as it is saved.
/// It takes no address from the request, so it cannot be pointed anywhere a saved
/// provider could not already send the server. The secret's value is read to see that
/// it is there and is never part of an answer.
/// </summary>
public sealed class ProviderCheck(OidcClientFactory oidc)
{
    public async Task<ProviderCheckResult> RunAsync(ProviderConfig provider, CancellationToken ct = default)
    {
        var lines = new List<ProviderCheckLine>();

        if (string.IsNullOrWhiteSpace(provider.Authority))
            lines.Add(new("Issuer URL", false, "Not filled in yet."));
        if (string.IsNullOrWhiteSpace(provider.ClientId))
            lines.Add(new("Client ID", false, "Not filled in yet."));

        if (!string.IsNullOrWhiteSpace(provider.Authority))
            await CheckDiscoveryAsync(provider, lines, ct).ConfigureAwait(false);

        if (provider.ClientType == OidcClientType.Confidential)
            lines.Add(CheckSecret(provider));

        if (!provider.Enabled)
        {
            lines.Add(new("Enabled", true,
                "Not yet: the login page shows no button for this provider until it is."));
        }

        return new ProviderCheckResult(lines.TrueForAll(line => line.Ok), lines);
    }

    private async Task CheckDiscoveryAsync(
        ProviderConfig provider, List<ProviderCheckLine> lines, CancellationToken ct)
    {
        try
        {
            // Fetched again rather than read from the cache: somebody pressing the button
            // has usually just changed something, at the provider or here.
            oidc.Forget(provider);
            var document = await oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

            lines.Add(new("Discovery document", true, $"Issuer {document.Issuer}"));
            lines.Add(Endpoint("Token endpoint", document.TokenEndpoint, required: true));
            lines.Add(Endpoint("Authorization endpoint", document.AuthorizeEndpoint, required: true));
            lines.Add(Endpoint("Userinfo endpoint", document.UserInfoEndpoint, required: provider.GroupsFromUserInfo));
            lines.Add(Endpoint("End-session endpoint", document.EndSessionEndpoint, required: false));

            var keys = document.KeySet?.Keys ?? [];
            var algorithms = keys.Select(key => key.Alg).Where(alg => !string.IsNullOrEmpty(alg)).Distinct().ToList();
            lines.Add(new(
                "Signing keys",
                keys.Count > 0,
                keys.Count == 0
                    ? "The provider publishes no keys, so no token of its could be verified."
                    : $"{keys.Count} published"
                        + (algorithms.Count > 0 ? $" ({string.Join(", ", algorithms)})" : string.Empty)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException
            or HttpRequestException or OperationCanceledException)
        {
            lines.Add(new("Discovery document", false, ex.Message));
        }
    }

    private static ProviderCheckLine Endpoint(string what, string? address, bool required)
        => string.IsNullOrEmpty(address)
            ? new(what, !required, required ? "Not published, and this setup needs it." : "Not published.")
            : new(what, true, address);

    private static ProviderCheckLine CheckSecret(ProviderConfig provider)
    {
        try
        {
            var secret = OidcClientFactory.ResolveSecret(provider.ConfidentialSecretRef);
            return string.IsNullOrWhiteSpace(secret)
                ? new("Client secret", false, "The reference resolves to an empty value.")
                : new("Client secret", true, "Found where the reference says it is.");
        }
        catch (InvalidOperationException ex)
        {
            return new("Client secret", false, ex.Message);
        }
    }
}
