using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The plugin's configuration is an XML file that Jellyfin writes without checking its
/// characters and throws away when it cannot read it back: a default configuration is
/// saved over it at the next start. One control character anywhere in it therefore cost
/// every provider, mapping and account link (#306), and it could come from a provider —
/// a group name, a subject — as well as from an administrator.
///
/// A restart is not something this suite can do to the server every other test shares,
/// so what is held here is what decides the restart: the file on disk, read the way
/// Jellyfin reads it.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class ConfigurationFileTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;

    public ConfigurationFileTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public Task InitializeAsync() => _env.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Reads the file as Jellyfin does, and fails the way Jellyfin's load would.</summary>
    private async Task<XDocument> ReadBackAsync()
    {
        using var file = new MemoryStream(await _env.ConfigurationFileAsync());
        using var reader = XmlReader.Create(file);
        return XDocument.Load(reader);
    }

    [Fact]
    public async Task A_group_name_with_a_control_character_leaves_a_file_the_server_can_read_back()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-ctl", "ctluser", ["media", "evil\u0001group"]));
        Assert.True(login.Succeeded, login.Body);

        // The name is not remembered; the one beside it is, so the list was written.
        var seen = (await _env.PluginConfigAsync())["SeenGroups"]!.AsArray()
            .Select(g => g!.GetValue<string>()).ToList();
        Assert.Contains("media", seen);
        Assert.DoesNotContain(seen, g => g.Contains("evil", StringComparison.Ordinal));

        var file = await ReadBackAsync();
        Assert.NotEmpty(file.Root!.Element("Providers")!.Elements());
        Assert.NotEmpty(file.Root.Element("Links")!.Elements());
    }

    /// <summary>
    /// The subject is the key of the account link, so it would be in the file too. A
    /// login that names one the file cannot hold is refused before an account exists.
    /// </summary>
    [Fact]
    public async Task An_identity_whose_subject_the_file_cannot_hold_is_refused_before_anything_is_stored()
    {
        var login = await _env.StartWebLoginAsync(new FakeUser("sub\u0001evil", "ctlsub", ["media"]));

        Assert.False(login.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, login.CallbackStatus);
        Assert.Contains("cannot be stored", login.Body!);
        Assert.Null(await _env.FindUserAsync("ctlsub"));
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
        await ReadBackAsync();
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "name-control", "Providers[0].Name contains a control character" },
        { "group-control", "GroupMappings[0].Group contains a control character" },
        { "seen-control", "SeenGroups" },
        { "id-empty", "cannot be used" },
        { "id-slash", "cannot be used" },
        { "id-twice", "Two providers have the id" },
        { "client-type", "client type" },
        { "provider-null", "providers has an empty entry" },
        { "mapping-null", "group mappings has an empty entry" },
        { "library-null", "is not a library id" },
    };

    /// <summary>
    /// None of these can be typed into the page. Each used to be saved, or to answer 500
    /// from wherever the value was first touched; each is refused with a sentence now.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task The_save_refuses_what_only_hand_written_json_can_say(string what, string because)
    {
        var before = (await _env.PluginConfigAsync()).ToJsonString();

        var result = await _env.TrySaveConfigAsync(config =>
        {
            var providers = config["Providers"]!.AsArray();
            var mappings = config["GroupMappings"]!.AsArray();
            switch (what)
            {
                case "name-control": providers[0]!["Name"] = "Fake\u0001IdP"; break;
                case "group-control": mappings[0]!["Group"] = "me\u0001dia"; break;
                case "seen-control": config["SeenGroups"]!.AsArray().Add("me\u0001dia"); break;
                case "id-empty": providers[0]!["Id"] = string.Empty; break;
                case "id-slash": providers[0]!["Id"] = "a/b c?d#e"; break;
                case "id-twice": providers.Add(JsonNode.Parse(providers[0]!.ToJsonString())); break;
                case "client-type": providers[0]!["ClientType"] = 7; break;
                case "provider-null": providers.Add(null); break;
                case "mapping-null": mappings.Add(null); break;
                case "library-null": mappings[0]!["LibraryIds"]!.AsArray().Add(null); break;
                default: throw new ArgumentOutOfRangeException(nameof(what));
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Contains(because, result.Body);
        Assert.Contains("Nothing was saved", result.Body);
        Assert.Equal(before, (await _env.PluginConfigAsync()).ToJsonString());
    }

    /// <summary>
    /// Jellyfin's own endpoint for a plugin's configuration runs none of the plugin's
    /// rules. What goes through it still ends in a file that can be read back, because
    /// the plugin replaces such a character on the way to the file.
    /// </summary>
    [Fact]
    public async Task What_jellyfins_own_endpoint_lets_through_is_scrubbed_on_the_way_to_the_file()
    {
        using var admin = JellyfinEnvironment.TrustingClient();
        admin.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(_env.AdminToken);
        var url = $"{_env.BaseUrl}/Plugins/{JellyfinEnvironment.PluginId}/Configuration";
        var config = JsonNode.Parse(await admin.GetStringAsync(url))!;
        config["Providers"]![0]!["Name"] = "Fake\u0001IdP";

        (await admin.PostAsJsonAsync(url, config)).EnsureSuccessStatusCode();

        Assert.Equal(
            "Fake�IdP",
            (await _env.PluginConfigAsync())["Providers"]![0]!["Name"]!.GetValue<string>());
        var file = await ReadBackAsync();
        Assert.Equal("Fake�IdP", file.Root!.Element("Providers")!.Elements().First().Element("Name")!.Value);
    }
}
