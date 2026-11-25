using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// What the configuration file can hold, and the reason it matters: the way Jellyfin
/// writes the file and the way it reads it disagree about one class of characters, and
/// what it does when the read fails is start over with an empty configuration.
/// </summary>
public sealed class StorableTextTests
{
    // ─── Why ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The writer and the reader Jellyfin's MyXmlSerializer uses, in that order: an
    /// XmlTextWriter over a UTF-8 stream writer, then XmlReader.Create on the stream.
    /// </summary>
    private static PluginConfiguration ThroughTheFile(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var file = new MemoryStream();
        using (var text = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true))
        using (var writer = new XmlTextWriter(text))
        {
            serializer.Serialize(writer, config);
        }

        file.Position = 0;
        using var reader = XmlReader.Create(file);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    [Fact]
    public void An_ordinary_configuration_comes_back_from_the_file()
    {
        var config = new PluginConfiguration();
        config.SeenGroups.Add("Müller & Söhne <admins>");

        Assert.Equal(["Müller & Söhne <admins>"], ThroughTheFile(config).SeenGroups);
    }

    /// <summary>
    /// The fault itself, held here so that the day Jellyfin's serializer stops behaving
    /// like this is a day somebody notices: written without complaint, refused on the way
    /// back. Jellyfin answers that refusal with a default configuration saved over the file.
    /// </summary>
    [Fact]
    public void A_control_character_is_written_to_the_file_and_cannot_be_read_back()
    {
        var config = new PluginConfiguration();
        config.SeenGroups.Add("evil\u0001group");

        var refused = Assert.Throws<InvalidOperationException>(() => ThroughTheFile(config));

        // The character and nothing else: not the encoding, not the shape of the document.
        var reason = Assert.IsType<XmlException>(refused.InnerException);
        Assert.Contains("0x01", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scrubbed_configuration_comes_back_from_the_file()
    {
        var config = new PluginConfiguration();
        config.SeenGroups.Add("evil\u0001group");
        config.Providers.Add(new ProviderConfig { Id = "a", Name = "x\u0000y" });
        config.Links.Add(new LinkConfig { ProviderId = "a", Sub = "s" + (char)0xDC00 });

        Assert.True(StorableText.Scrub(config));
        var read = ThroughTheFile(config);

        Assert.Equal(["evil�group"], read.SeenGroups);
        Assert.Equal("x�y", Assert.Single(read.Providers).Name);
        Assert.Equal("s�", Assert.Single(read.Links).Sub);
    }

    // ─── Which characters ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("media")]
    [InlineData("Müller & Söhne <admins>")]
    [InlineData("tab\there, line\nbreak")]
    [InlineData("家庭")]
    [InlineData("\U0001F3AC movies")]
    public void Text_a_person_would_write_can_be_carried(string? text)
        => Assert.True(StorableText.CanCarry(text));

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u0001b")]
    [InlineData("a\u001Fb")]
    [InlineData("a\u000Bb")]
    [InlineData("a￾b")]
    [InlineData("a￿b")]
    public void A_control_character_cannot(string text)
    {
        Assert.False(StorableText.CanCarry(text));
        Assert.True(StorableText.CanCarry(StorableText.Carried(text)));
        Assert.Equal(text.Length, StorableText.Carried(text).Length);
    }

    /// <summary>
    /// Half of a pair, either half, anywhere. Built here and not written as test data: an
    /// attribute cannot hold half a pair either, and hands the test U+FFFD in its place.
    /// </summary>
    [Theory]
    [InlineData(0xD83C, true)]
    [InlineData(0xDFAC, true)]
    [InlineData(0xD83C, false)]
    [InlineData(0xDFAC, false)]
    public void Half_a_surrogate_pair_cannot(int half, bool atTheEnd)
    {
        var text = atTheEnd ? "a" + (char)half : (char)half + "b";

        Assert.False(StorableText.CanCarry(text));
        Assert.Equal(atTheEnd ? "a\uFFFD" : "\uFFFDb", StorableText.Carried(text));
    }

    [Fact]
    public void A_whole_pair_between_two_halves_is_kept()
    {
        var text = (char)0xDFAC + "\U0001F3AC" + (char)0xD83C;

        Assert.Equal("\uFFFD\U0001F3AC\uFFFD", StorableText.Carried(text));
    }

    [Fact]
    public void Text_that_can_be_carried_is_handed_back_as_it_is()
    {
        const string text = "\U0001F3AC movies";

        Assert.Same(text, StorableText.Carried(text));
    }

    // ─── Where in a configuration ─────────────────────────────────────────────

    [Fact]
    public void A_clean_configuration_has_nothing_to_find_and_nothing_to_scrub()
    {
        var config = new PluginConfiguration { PublicAddress = "https://jellyfin.example.com" };
        config.Providers.Add(new ProviderConfig { Id = "a", Name = "Authentik", Scopes = ["openid", "groups"] });
        config.GroupMappings.Add(new GroupMappingConfig { Group = "media" });

        Assert.Null(StorableText.FirstUncarried(config));
        Assert.False(StorableText.Scrub(config));
    }

    /// <summary>
    /// Every text, however deep, found without a list of them being kept in the plugin: a
    /// setting added later is covered because it is a property, not because somebody
    /// remembered. The place is named, and the configuration built from the name, so that
    /// each row is a test of its own in a listing as well as in a run.
    /// </summary>
    [Theory]
    [InlineData("PublicAddress")]
    [InlineData("SeenGroups[1]")]
    [InlineData("Providers[0].Name")]
    [InlineData("Providers[0].Authority")]
    [InlineData("Providers[0].Scopes[1]")]
    [InlineData("Providers[1].ConfidentialSecretRef")]
    [InlineData("GroupMappings[0].Group")]
    [InlineData("GroupMappings[0].LibraryIds[0]")]
    [InlineData("Links[0].Sub")]
    public void A_character_the_file_cannot_hold_is_found_wherever_it_is(string place)
    {
        const string Bad = "a\u0001b";
        var config = new PluginConfiguration();
        switch (place)
        {
            case "PublicAddress": config.PublicAddress = Bad; break;
            case "SeenGroups[1]": config.SeenGroups.AddRange(["fine", Bad]); break;
            case "Providers[0].Name": config.Providers.Add(new ProviderConfig { Id = "a", Name = Bad }); break;
            case "Providers[0].Authority": config.Providers.Add(new ProviderConfig { Id = "a", Authority = Bad }); break;
            case "Providers[0].Scopes[1]": config.Providers.Add(new ProviderConfig { Id = "a", Scopes = ["openid", Bad] }); break;
            case "Providers[1].ConfidentialSecretRef":
                config.Providers.AddRange([new ProviderConfig { Id = "a" }, new ProviderConfig { Id = "b", ConfidentialSecretRef = Bad }]);
                break;
            case "GroupMappings[0].Group": config.GroupMappings.Add(new GroupMappingConfig { Group = Bad }); break;
            case "GroupMappings[0].LibraryIds[0]": config.GroupMappings.Add(new GroupMappingConfig { Group = "g", LibraryIds = [Bad] }); break;
            case "Links[0].Sub": config.Links.Add(new LinkConfig { ProviderId = "a", Sub = Bad }); break;
            default: throw new ArgumentOutOfRangeException(nameof(place));
        }

        Assert.Equal(place, StorableText.FirstUncarried(config));

        Assert.True(StorableText.Scrub(config));
        Assert.Null(StorableText.FirstUncarried(config));
    }

    [Fact]
    public void An_empty_entry_in_a_list_is_stepped_over()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(null!);
        config.SeenGroups.Add(null!);

        Assert.Null(StorableText.FirstUncarried(config));
    }

    // ─── What a provider sends ────────────────────────────────────────────────

    /// <summary>
    /// The one list of what a login adds to the remembered group names. Both places that
    /// remember them ask here; the first version of the rule was in one of them only, and
    /// an end-to-end test found the other.
    /// </summary>
    [Fact]
    public void The_group_names_to_remember_are_the_new_ones_the_file_can_hold_once_each()
    {
        var config = new PluginConfiguration();
        config.SeenGroups.Add("media");

        var remember = config.GroupsToRemember(["media", "kids", "evil\u0001group", "kids", "admins"]);

        Assert.Equal(["kids", "admins"], remember);
    }

    [Fact]
    public void And_never_more_than_the_list_has_room_for()
    {
        var config = new PluginConfiguration();
        config.SeenGroups.AddRange(Enumerable.Range(0, PluginConfiguration.MaxSeenGroups - 1).Select(i => $"g{i}"));

        Assert.Equal(["one"], config.GroupsToRemember(["one", "two"]));
    }

    /// <summary>
    /// The count alone did not bound the list: sixty-four names of 64 KB from one login
    /// wrote four megabytes into the configuration file (#323).
    /// </summary>
    [Fact]
    public void A_group_name_longer_than_the_cap_is_not_remembered()
    {
        var config = new PluginConfiguration();
        var longest = new string('g', PluginConfiguration.MaxRememberedGroupLength);
        var tooLong = longest + "g";

        Assert.Equal(["media", longest], config.GroupsToRemember(["media", longest, tooLong]));
    }

    [Fact]
    public void But_it_still_matches_a_mapping_of_that_name()
    {
        var tooLong = new string('g', PluginConfiguration.MaxRememberedGroupLength + 1);
        var config = new PluginConfiguration();
        config.GroupMappings.Add(new GroupMappingConfig { Group = tooLong });

        var matched = Jellyfin.Plugin.Sso.Provisioning.GroupMatching.Matching(
            config, new Jellyfin.Plugin.Sso.Oidc.OidcIdentity("sub", "name", [tooLong]));

        Assert.Single(matched);
    }

    // ─── On the way to the file ───────────────────────────────────────────────

    /// <summary>
    /// The last line: whatever put the text there, the plugin does not hand Jellyfin a
    /// configuration whose file would be thrown away at the next start.
    /// </summary>
    [Fact]
    public void The_plugin_scrubs_what_it_saves()
    {
        using var harness = new PluginHarness();
        harness.Configuration.SeenGroups.Add("evil\u0001group");

        harness.Plugin.SaveConfiguration();

        Assert.Equal(["evil�group"], harness.Configuration.SeenGroups);
    }

    [Fact]
    public void A_configuration_that_arrives_through_jellyfins_own_endpoint_is_scrubbed_too()
    {
        using var harness = new PluginHarness();
        var incoming = new PluginConfiguration();
        incoming.Providers.Add(new ProviderConfig { Id = "a", Name = "x\u0001y" });

        harness.Plugin.UpdateConfiguration(incoming);

        Assert.Equal("x�y", Assert.Single(harness.Plugin.Configuration.Providers).Name);
    }
}
