using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Not every provider puts its groups at the top of the token. Keycloak keeps roles in
/// `realm_access.roles` unless a mapper moves them, and Entra ID keeps a client's in
/// `resource_access.&lt;client&gt;.roles`. A dotted group claim reaches those — without
/// breaking the claim names that contain dots of their own, which is the part that needs
/// holding down.
/// </summary>
public sealed class NestedGroupsClaimTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value))));

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ─── In the ID token ──────────────────────────────────────────────────────

    [Fact]
    public void A_dotted_claim_reads_the_roles_out_of_the_nested_object()
    {
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(("realm_access", """{"roles":["media","kids"]}""")),
            "realm_access.roles");

        Assert.Equal(["media", "kids"], groups);
    }

    [Fact]
    public void A_path_goes_as_deep_as_it_is_written()
    {
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(("resource_access", """{"jellyfin":{"roles":["media"]}}""")),
            "resource_access.jellyfin.roles");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void A_claim_whose_own_name_contains_dots_is_found_before_the_name_is_split()
    {
        // The reason the whole name is tried first: these are real claim types, and a
        // configuration naming one worked before dotted paths existed.
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(
                ("urn:zitadel:iam:org:project:roles", "media"),
                ("urn", """{"zitadel":{"iam":"nonsense"}}""")),
            "urn:zitadel:iam:org:project:roles");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void A_dotted_key_inside_the_path_is_still_reached()
    {
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(("resource_access", """{"jellyfin.example.com":{"roles":["media"]}}""")),
            "resource_access.jellyfin.example.com.roles");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void A_flat_claim_still_works_exactly_as_before()
    {
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(("groups", "media"), ("groups", "kids")),
            "groups");

        Assert.Equal(["media", "kids"], groups);
    }

    [Fact]
    public void A_single_role_in_the_nested_object_is_read_as_one_group()
    {
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(("realm_access", """{"roles":"media"}""")),
            "realm_access.roles");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void A_nested_claim_that_is_not_json_grants_nothing()
    {
        Assert.Empty(ClaimsExtractor.GroupsFromClaims(
            Principal(("realm_access", "not json at all")),
            "realm_access.roles"));
    }

    [Fact]
    public void A_path_that_does_not_lead_anywhere_grants_nothing()
    {
        Assert.Empty(ClaimsExtractor.GroupsFromClaims(
            Principal(("realm_access", """{"roles":["media"]}""")),
            "realm_access.groups"));
    }

    [Fact]
    public void A_token_without_the_carrier_claim_grants_nothing()
    {
        Assert.Empty(ClaimsExtractor.GroupsFromClaims(
            Principal(("groups", "media")),
            "realm_access.roles"));
    }

    [Fact]
    public void Entries_that_are_not_strings_are_passed_over_rather_than_guessed_at()
    {
        var groups = ClaimsExtractor.GroupsFromClaims(
            Principal(("realm_access", """{"roles":["media",7,null,{"x":1},"kids"]}""")),
            "realm_access.roles");

        Assert.Equal(["media", "kids"], groups);
    }

    // ─── In the userinfo reply ────────────────────────────────────────────────

    [Fact]
    public void Userinfo_is_read_by_the_same_path()
    {
        var groups = ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"sub":"ben","realm_access":{"roles":["media"]}}"""),
            "realm_access.roles");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void A_userinfo_property_named_with_dots_wins_over_the_path_reading()
    {
        var groups = ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"urn:example:roles":["media"],"urn":{"example":{"roles":["wrong"]}}}"""),
            "urn:example:roles");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void A_userinfo_path_through_something_that_is_not_an_object_grants_nothing()
    {
        Assert.Empty(ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"realm_access":"media"}"""),
            "realm_access.roles"));
    }
}
