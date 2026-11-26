using System.Xml.Serialization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A group mapping limited to some providers (#340). A group name is decided by whoever runs
/// the provider, so with two providers that set permissions an unscoped mapping of
/// "jf-admins" to administrator trusts both of them with the administrator flag. The second
/// one can be a provider where somebody else picks the names — a friend's identity provider,
/// a GitLab where users create groups — and a user there who calls a group "jf-admins" used
/// to become a Jellyfin administrator at the next login.
///
/// Every place that asks which mappings a login matched is asked here: the administrator
/// decision and the libraries in the permission sync, and the login gate.
/// </summary>
public sealed class ProviderScopedMappingTests : IDisposable
{
    private const string Household = "authentik";
    private const string Friends = "friends-idp";

    private static readonly Guid MoviesId = Guid.NewGuid();

    private readonly PluginHarness _harness = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly RecordingLogger<PermissionSync> _log = new();
    private readonly PermissionSync _sync;
    private readonly Guid _userId = Guid.NewGuid();

    public ProviderScopedMappingTests()
    {
        _sync = new PermissionSync(_userManager, _log);
        Config.Providers.Add(new ProviderConfig { Id = Household, Name = "Household" });
        Config.Providers.Add(new ProviderConfig { Id = Friends, Name = "Friends" });

        var user = new User(
            "ben",
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, false);
        _userManager.GetUserById(_userId).Returns(user);
        _userManager.GetUserDto(user).Returns(new MediaBrowser.Model.Dto.UserDto { Policy = new UserPolicy() });
    }

    private PluginConfiguration Config => _harness.Configuration;

    private static OidcIdentity Through(string providerId, params string[] groups)
        => new("sub-1", "ben", groups, null, providerId);

    /// <summary>The household's administrator group, which only the household's provider may grant.</summary>
    private void MapAdministratorsTo(params string[] providers)
        => Config.GroupMappings.Add(new GroupMappingConfig
        {
            Group = "jf-admins",
            IsAdministrator = true,
            LibraryIds = [MoviesId.ToString("N")],
            ProviderIds = [.. providers],
        });

    private ProviderConfig Provider(string id) => Config.Providers.Single(p => p.Id == id);

    private UserPolicy CapturedPolicy()
    {
        var calls = _userManager.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IUserManager.UpdatePolicyAsync))
            .Select(c => c.GetArguments())
            .ToList();

        Assert.NotEmpty(calls);
        return Assert.IsType<UserPolicy>(calls[^1][1]);
    }

    // ─── The permission sync ──────────────────────────────────────────────────

    [Fact]
    public async Task A_mapping_for_one_provider_does_not_make_an_administrator_through_another()
    {
        MapAdministratorsTo(Household);

        await _sync.SyncAsync(_userId, Through(Friends, "jf-admins"), default, Provider(Friends));

        var policy = CapturedPolicy();
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);
    }

    [Fact]
    public async Task A_mapping_for_one_provider_grants_no_libraries_through_another()
    {
        Config.GroupMappings.Add(new GroupMappingConfig
        {
            Group = "media",
            LibraryIds = [MoviesId.ToString("N")],
            ProviderIds = [Household],
        });

        await _sync.SyncAsync(_userId, Through(Friends, "media"), default, Provider(Friends));

        Assert.Empty(CapturedPolicy().EnabledFolders);
    }

    [Fact]
    public async Task The_provider_it_names_still_gets_it()
    {
        MapAdministratorsTo(Household);

        await _sync.SyncAsync(_userId, Through(Household, "jf-admins"), default, Provider(Household));

        Assert.True(CapturedPolicy().IsAdministrator);
    }

    /// <summary>
    /// The log says why, because what the person sees — no administrator, no libraries — says
    /// nothing about it, and because it is also what an attempt looks like.
    /// </summary>
    [Fact]
    public async Task The_log_says_the_mapping_belongs_to_another_provider()
    {
        MapAdministratorsTo(Household);

        await _sync.SyncAsync(_userId, Through(Friends, "jf-admins"), default, Provider(Friends));

        Assert.Contains(
            _log.Warnings,
            line => line.Contains("'jf-admins'", StringComparison.Ordinal)
                && line.Contains("other providers", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two providers with their own mapping of one group name is an ordinary setup, and the
    /// login that matched its own provider's mapping is not a near miss of the other one.
    /// </summary>
    [Fact]
    public async Task A_group_mapped_once_per_provider_is_not_reported_as_a_near_miss()
    {
        MapAdministratorsTo(Household);
        MapAdministratorsTo(Friends);

        await _sync.SyncAsync(_userId, Through(Friends, "jf-admins"), default, Provider(Friends));

        Assert.True(CapturedPolicy().IsAdministrator);
        Assert.Empty(_log.Warnings);
    }

    // ─── Unscoped, which is how every mapping saved before this behaves ───────

    [Theory]
    [InlineData(Household)]
    [InlineData(Friends)]
    public async Task A_mapping_that_names_no_provider_still_applies_to_every_provider(string provider)
    {
        MapAdministratorsTo();

        await _sync.SyncAsync(_userId, Through(provider, "jf-admins"), default, Provider(provider));

        Assert.True(CapturedPolicy().IsAdministrator);
    }

    [Fact]
    public void A_mapping_that_names_several_providers_applies_to_each_of_them()
    {
        MapAdministratorsTo(Household, Friends);

        Assert.Single(GroupMatching.Matching(Config, Through(Household, "jf-admins")));
        Assert.Single(GroupMatching.Matching(Config, Through(Friends, "jf-admins")));
    }

    /// <summary>
    /// An identity without a provider does not arise at a login. If one ever did, it matches
    /// what applies to every provider and nothing that was limited to some.
    /// </summary>
    [Fact]
    public void A_login_whose_provider_is_not_known_matches_no_limited_mapping()
    {
        MapAdministratorsTo(Household);

        Assert.Empty(GroupMatching.Matching(Config, new OidcIdentity("sub-1", "ben", ["jf-admins"])));
    }

    /// <summary>Provider ids are compared exactly, as everywhere else: "a" and "A" are two providers.</summary>
    [Fact]
    public void A_provider_id_that_differs_in_case_is_another_provider()
    {
        MapAdministratorsTo(Household);

        Assert.Empty(GroupMatching.Matching(Config, Through(Household.ToUpperInvariant(), "jf-admins")));
    }

    // ─── The login gate ───────────────────────────────────────────────────────

    [Fact]
    public void Refuse_logins_that_match_no_mapping_refuses_the_other_provider()
    {
        Config.RequireMappedGroup = true;
        MapAdministratorsTo(Household);

        var refusal = LoginGate.Refusal(Config, Through(Friends, "jf-admins"), null, Provider(Friends));

        Assert.NotNull(refusal);
        var (mapping, arrived, reason) = Assert.Single(refusal!.NearMisses);
        Assert.Equal("jf-admins", mapping);
        Assert.Equal("jf-admins", arrived);
        Assert.Contains("other providers", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_logins_that_match_no_mapping_still_admits_the_provider_it_names()
    {
        Config.RequireMappedGroup = true;
        MapAdministratorsTo(Household);

        Assert.Null(LoginGate.Refusal(Config, Through(Household, "jf-admins"), null, Provider(Household)));
    }

    [Theory]
    [InlineData(Household)]
    [InlineData(Friends)]
    public void Refuse_logins_that_match_no_mapping_admits_both_through_an_unscoped_mapping(string provider)
    {
        Config.RequireMappedGroup = true;
        MapAdministratorsTo();

        Assert.Null(LoginGate.Refusal(Config, Through(provider, "jf-admins"), null, Provider(provider)));
    }

    // ─── Saving ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_mapping_limited_to_providers_that_exist_is_saved()
    {
        MapAdministratorsTo(Household, Friends);

        Assert.Null(ConfigurationRules.Problem(Config));
    }

    /// <summary>
    /// Matching no login would be safe, but it is not what anybody meant, and the other
    /// remedy — dropping the id — empties the list into "every provider".
    /// </summary>
    [Theory]
    [InlineData("keycloak")]
    [InlineData("AUTHENTIK")]
    [InlineData("")]
    public void A_mapping_limited_to_a_provider_that_does_not_exist_is_refused(string id)
    {
        MapAdministratorsTo(Household, id);

        var problem = ConfigurationRules.Problem(Config);

        Assert.NotNull(problem);
        Assert.Contains($"'{id}'", problem, StringComparison.Ordinal);
        Assert.Contains("no provider with that id", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_entry_among_the_providers_of_a_mapping_is_refused_and_not_tripped_over()
    {
        MapAdministratorsTo(Household, null!);

        Assert.Contains("no provider with that id", ConfigurationRules.Problem(Config), StringComparison.Ordinal);
    }

    /// <summary>A list that is null, as hand-written JSON can make it, is an empty list: every provider.</summary>
    [Fact]
    public void A_mapping_whose_provider_list_is_null_applies_to_every_provider()
    {
        MapAdministratorsTo();
        Config.GroupMappings[0].ProviderIds = null!;

        Assert.Null(ConfigurationRules.Problem(Config));
        Assert.Single(GroupMatching.Matching(Config, Through(Friends, "jf-admins")));
    }

    // ─── The configuration file ───────────────────────────────────────────────

    [Fact]
    public void The_provider_list_comes_back_from_the_configuration_file()
    {
        MapAdministratorsTo(Household, Friends);
        Config.GroupMappings.Add(new GroupMappingConfig { Group = "media" });

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var file = new StringWriter();
        serializer.Serialize(file, Config);
        using var reader = new StringReader(file.ToString());
        var read = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.Equal([Household, Friends], read.GroupMappings[0].ProviderIds);
        Assert.Empty(read.GroupMappings[1].ProviderIds);
    }

    /// <summary>A mapping saved before the list existed has none, and so applies to every provider.</summary>
    [Fact]
    public void A_mapping_saved_before_the_list_existed_applies_to_every_provider()
    {
        const string Saved = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <GroupMappings>
                <GroupMappingConfig>
                  <Group>jf-admins</Group>
                  <IsAdministrator>true</IsAdministrator>
                </GroupMappingConfig>
              </GroupMappings>
            </PluginConfiguration>
            """;

        using var reader = new StringReader(Saved);
        var read = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        var mapping = Assert.Single(read.GroupMappings);
        Assert.Empty(mapping.ProviderIds);
        Assert.Single(GroupMatching.Matching(read, Through(Friends, "jf-admins")));
    }

    /// <summary>The ids are text in the file like any other, and a save checks them the same way.</summary>
    [Fact]
    public void A_control_character_in_a_provider_id_of_a_mapping_is_found()
        => Assert.Equal(
            "GroupMappings[0].ProviderIds[0]",
            StorableText.FirstUncarried(new PluginConfiguration
            {
                GroupMappings = [new GroupMappingConfig { Group = "g", ProviderIds = ["a\u0001"] }],
            }));

    public void Dispose() => _harness.Dispose();
}
