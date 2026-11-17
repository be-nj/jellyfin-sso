using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Tests;

public sealed class SettingsFileTests
{
    private const string Provider = """
        { "Id": "authentik", "Name": "Authentik", "Enabled": true,
          "Authority": "https://auth.example.org/application/o/jellyfin/", "ClientId": "jellyfin",
          "ClientType": "Confidential", "ConfidentialSecretRef": "env:SSO_SECRET" }
        """;

    [Fact]
    public void The_settings_it_names_are_applied_and_the_rest_stay_as_they_were()
    {
        var file = SettingsFile.Parse("/etc/sso.json", $$"""
            {
              // comments and trailing commas, as hand-written files have them
              "providers": [{{Provider}}],
              "RequireMappedGroup": true,
            }
            """);

        Assert.Null(file.Error);
        Assert.Equal(["Providers", "RequireMappedGroup"], file.Managed);

        var config = new PluginConfiguration { SignOutOfIdentityProvider = true };
        config.Links.Add(new LinkConfig { ProviderId = "authentik", Sub = "a" });
        file.ApplyTo(config);

        var provider = Assert.Single(config.Providers);
        Assert.Equal("authentik", provider.Id);
        Assert.Equal(OidcClientType.Confidential, provider.ClientType);
        Assert.True(config.RequireMappedGroup);
        Assert.True(config.SignOutOfIdentityProvider);
        Assert.Single(config.Links);
    }

    [Theory]
    [InlineData("""{ "Providrs": [] }""", "names 'Providrs', which is not a setting")]
    [InlineData("""{ "Links": [] }""", "names 'Links', which the plugin keeps itself")]
    [InlineData("""{ "Providers": [{ "Name": "no id" }] }""", "has a provider without an Id")]
    [InlineData("""{ "RequireMappedGroup": "yes" }""", "has a value of the wrong type")]
    [InlineData("""[]""", "must hold a JSON object")]
    [InlineData("""{ "Providers": [ """, "is not valid JSON")]
    [InlineData("""{ "Providers": [{ "Id": "a", "ConfidentialSecretRef": "hunter2" }] }""", "not hold the secret itself")]
    public void A_file_with_a_mistake_is_not_applied_at_all(string json, string error)
    {
        var file = SettingsFile.Parse("/etc/sso.json", json);

        Assert.Contains(error, file.Error);
        Assert.Empty(file.Managed);

        var config = new PluginConfiguration();
        file.ApplyTo(config);
        Assert.Empty(config.Providers);
    }

    [Fact]
    public void A_missing_file_is_an_error_not_an_exception()
    {
        var file = SettingsFile.Load("/nonexistent/sso.json");

        Assert.Contains("cannot be read", file.Error);
    }

    [Fact]
    public void A_saved_configuration_cannot_change_what_the_file_decides()
    {
        using var harness = new PluginHarness();
        harness.Plugin.SettingsFile = SettingsFile.Parse("/etc/sso.json", """{ "RequireMappedGroup": true }""");

        harness.Plugin.UpdateConfiguration(new PluginConfiguration
        {
            RequireMappedGroup = false,
            SignOutOfIdentityProvider = true,
        });

        Assert.True(harness.Plugin.Configuration.RequireMappedGroup);
        Assert.True(harness.Plugin.Configuration.SignOutOfIdentityProvider);
    }
}
