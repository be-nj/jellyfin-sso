using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// What a save refuses. None of these can be produced on the configuration page; they
/// arrive as hand-written JSON, which is how most of what this plugin stores wrongly has
/// arrived so far.
/// </summary>
public sealed class ConfigurationRulesTests
{
    private static PluginConfiguration With(params ProviderConfig[] providers)
    {
        var config = new PluginConfiguration();
        config.Providers.AddRange(providers);
        return config;
    }

    [Fact]
    public void An_ordinary_configuration_has_no_problem()
    {
        var config = With(
            new ProviderConfig { Id = "authentik", Name = "Authentik", ClientType = OidcClientType.Confidential, ConfidentialSecretRef = "env:SSO" },
            new ProviderConfig { Id = "k3ycl0ak_2.x~y", Name = "Keycloak" });
        config.GroupMappings.Add(new GroupMappingConfig { Group = "media", LibraryIds = [Guid.NewGuid().ToString("N")] });

        Assert.Null(ConfigurationRules.Problem(config));
    }

    // ─── Ids ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("a%2Fb")]
    [InlineData("ü")]
    public void A_provider_id_that_cannot_be_part_of_an_address_is_refused(string id)
        => Assert.Contains(
            "cannot be used", ConfigurationRules.Problem(With(new ProviderConfig { Id = id, Name = "P" })),
            StringComparison.Ordinal);

    [Fact]
    public void An_id_longer_than_any_the_page_makes_is_refused()
        => Assert.Contains(
            "cannot be used", ConfigurationRules.Problem(With(new ProviderConfig { Id = new string('a', 65) })),
            StringComparison.Ordinal);

    [Fact]
    public void Two_providers_with_one_id_are_refused()
    {
        var problem = ConfigurationRules.Problem(With(
            new ProviderConfig { Id = "same" }, new ProviderConfig { Id = "other" }, new ProviderConfig { Id = "same" }));

        Assert.Contains("Two providers have the id 'same'", problem, StringComparison.Ordinal);
    }

    /// <summary>Ids are compared as written, the way a login address and a link compare them.</summary>
    [Fact]
    public void Two_ids_that_differ_in_case_are_two_ids()
        => Assert.Null(ConfigurationRules.Problem(With(new ProviderConfig { Id = "a" }, new ProviderConfig { Id = "A" })));

    // ─── Values that only JSON can produce ────────────────────────────────────

    [Fact]
    public void A_client_type_that_is_neither_of_the_two_is_refused()
        => Assert.Contains(
            "client type",
            ConfigurationRules.Problem(With(new ProviderConfig { Id = "a", ClientType = (OidcClientType)7 })),
            StringComparison.Ordinal);

    [Fact]
    public void An_empty_entry_among_the_providers_is_refused_and_not_tripped_over()
        => Assert.Contains(
            "providers has an empty entry",
            ConfigurationRules.Problem(With(new ProviderConfig { Id = "a" }, null!)),
            StringComparison.Ordinal);

    [Fact]
    public void An_empty_entry_among_the_mappings_is_refused_and_not_tripped_over()
    {
        var config = With(new ProviderConfig { Id = "a" });
        config.GroupMappings.AddRange([new GroupMappingConfig { Group = "g" }, null!]);

        Assert.Contains("group mappings has an empty entry", ConfigurationRules.Problem(config), StringComparison.Ordinal);
    }

    /// <summary>
    /// The check looked for the first id that is not a GUID and compared what it found
    /// with null — so an id that is null was found, and read as nothing found.
    /// </summary>
    [Fact]
    public void A_library_id_that_is_null_is_not_a_library_id()
    {
        var config = With(new ProviderConfig { Id = "a" });
        config.GroupMappings.Add(new GroupMappingConfig { Group = "g", LibraryIds = [Guid.NewGuid().ToString("N"), null!] });

        Assert.Contains("is not a library id", ConfigurationRules.Problem(config), StringComparison.Ordinal);
    }

    // ─── What the file cannot hold ────────────────────────────────────────────

    [Fact]
    public void A_control_character_is_refused_with_the_place_it_is_in()
    {
        var config = With(new ProviderConfig { Id = "a", Name = "Authen\u0001tik" });

        var problem = ConfigurationRules.Problem(config);

        Assert.Contains("Providers[0].Name contains a control character", problem, StringComparison.Ordinal);
        // The refusal goes to a browser and to a log, and must not carry the character on.
        Assert.True(StorableText.CanCarry(problem));
    }

    [Fact]
    public void And_in_a_group_name_and_among_the_remembered_ones()
    {
        var mapping = With(new ProviderConfig { Id = "a" });
        mapping.GroupMappings.Add(new GroupMappingConfig { Group = "me\u0000dia" });
        Assert.Contains("GroupMappings[0].Group", ConfigurationRules.Problem(mapping), StringComparison.Ordinal);

        var seen = With(new ProviderConfig { Id = "a" });
        seen.SeenGroups.Add("me\u0000dia");
        Assert.Contains("SeenGroups[0]", ConfigurationRules.Problem(seen), StringComparison.Ordinal);
    }
}
