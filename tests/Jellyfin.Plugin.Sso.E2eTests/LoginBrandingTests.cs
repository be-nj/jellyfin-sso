using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The login button is the only part of the plugin a household ever sees before signing
/// in, and it lives in Jellyfin's branding rather than in the plugin. Unit tests cover the
/// markup; these cover the part that only a running server can answer — whether saving the
/// configuration actually writes that markup into the server's branding, and whether
/// disabling the providers takes it away again.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class LoginBrandingTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public LoginBrandingTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Saving_the_configuration_puts_a_button_per_provider_into_the_branding()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["Name"] = "Authentik");

        var disclaimer = await DisclaimerAsync();

        Assert.Contains($"/sso/oidc/start/{JellyfinEnvironment.ProviderId}", disclaimer);
        Assert.Contains("Login with Authentik", disclaimer);
    }

    [Fact]
    public async Task A_second_provider_gets_its_own_button()
    {
        await _env.AddSecondProviderAsync();

        var disclaimer = await DisclaimerAsync();

        Assert.Contains($"/sso/oidc/start/{JellyfinEnvironment.ProviderId}", disclaimer);
        Assert.Contains($"/sso/oidc/start/{JellyfinEnvironment.SecondProviderId}", disclaimer);
    }

    [Fact]
    public async Task Disabling_every_provider_removes_the_buttons_but_keeps_the_house_text()
    {
        const string House = "<p>Be nice to each other.</p>";
        await SetDisclaimerAsync(House);

        await _env.PatchConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["Name"] = "Authentik");
        Assert.Contains("/sso/oidc/start/", await DisclaimerAsync());

        await _env.PatchConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["Enabled"] = false);

        Assert.Equal(House, await DisclaimerAsync());
    }

    [Fact]
    public async Task Turning_the_branding_button_off_removes_it()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["Name"] = "Authentik");
        Assert.Contains("/sso/oidc/start/", await DisclaimerAsync());

        await _env.PatchConfigAsync(config => config["AddLoginButtonToBranding"] = false);

        Assert.DoesNotContain("/sso/oidc/start/", await DisclaimerAsync());
    }

    [Fact]
    public async Task The_buttons_are_there_after_a_restart_without_anybody_saving()
    {
        await _env.PatchConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["Name"] = "Authentik");

        // Clear the branding behind the plugin's back, the way a server that was
        // configured before this version would come up.
        await SetDisclaimerAsync(string.Empty);
        Assert.Empty(await DisclaimerAsync());

        await _env.RestartServerAsync();

        Assert.Contains("/sso/oidc/start/", await DisclaimerAsync());
    }

    /// <summary>Jellyfin's branding as any visitor of the login page receives it.</summary>
    private async Task<string> DisclaimerAsync()
    {
        using var client = JellyfinEnvironment.TrustingClient();
        var branding = JsonNode.Parse(
            await client.GetStringAsync($"{_env.BaseUrl}/Branding/Configuration"));

        return branding?["LoginDisclaimer"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>Puts an administrator's own text into the branding, as the dashboard would.</summary>
    private async Task SetDisclaimerAsync(string disclaimer)
    {
        using var client = JellyfinEnvironment.TrustingClient();
        client.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(_env.AdminToken);

        var response = await client.PostAsync(
            $"{_env.BaseUrl}/System/Configuration/branding",
            new StringContent(
                new JsonObject
                {
                    ["LoginDisclaimer"] = disclaimer,
                    ["SplashscreenEnabled"] = false,
                }.ToJsonString(),
                System.Text.Encoding.UTF8,
                "application/json"));

        response.EnsureSuccessStatusCode();
    }
}
