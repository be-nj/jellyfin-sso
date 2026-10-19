using System.Text.Json;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Some providers — Authelia among them — keep the profile claims out of the ID token, so
/// the name and the groups come from the userinfo endpoint instead. That reply is JSON the
/// provider chose the shape of: an array, a single string, a missing property, a null. It
/// decides what a JIT-created account is called and which libraries it sees, and no part
/// of it can be reached without a live provider — hence these tests, straight at the
/// readers.
/// </summary>
public sealed class UserInfoClaimsTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ─── Groups ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_array_of_groups_is_read_as_the_groups()
    {
        var groups = ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"groups":["media","kids"]}"""), "groups");

        Assert.Equal(["media", "kids"], groups);
    }

    [Fact]
    public void A_single_group_named_as_a_string_is_read_as_one_group()
    {
        var groups = ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"groups":"media"}"""), "groups");

        Assert.Equal(["media"], groups);
    }

    [Fact]
    public void Only_the_configured_claim_is_read()
    {
        var groups = ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"groups":["media"],"roles":["admins"]}"""), "roles");

        Assert.Equal(["admins"], groups);
    }

    [Fact]
    public void A_reply_without_the_claim_grants_nothing()
    {
        Assert.Empty(ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"preferred_username":"ben"}"""), "groups"));
    }

    [Fact]
    public void Entries_that_are_not_strings_are_passed_over_rather_than_guessed_at()
    {
        var groups = ClaimsExtractor.ExtractGroupsFromElement(
            Json("""{"groups":["media",7,null,{"name":"kids"},"admins"]}"""), "groups");

        Assert.Equal(["media", "admins"], groups);
    }

    [Theory]
    [InlineData("""{"groups":null}""")]
    [InlineData("""{"groups":7}""")]
    [InlineData("""{"groups":{"name":"media"}}""")]
    [InlineData("""{"groups":true}""")]
    public void A_claim_that_is_neither_an_array_nor_a_string_grants_nothing(string reply)
    {
        Assert.Empty(ClaimsExtractor.ExtractGroupsFromElement(Json(reply), "groups"));
    }

    [Fact]
    public void A_reply_that_never_arrived_grants_nothing()
    {
        Assert.Empty(ClaimsExtractor.ExtractGroupsFromElement(null, "groups"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"media\"")]
    [InlineData("[\"media\"]")]
    public void A_reply_that_is_not_an_object_grants_nothing(string reply)
    {
        Assert.Empty(ClaimsExtractor.ExtractGroupsFromElement(Json(reply), "groups"));
    }

    // ─── The name ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_string_claim_is_read_from_the_reply()
    {
        Assert.Equal("ben", ClaimsExtractor.StringClaim(
            Json("""{"preferred_username":"ben"}"""), "preferred_username"));
    }

    [Fact]
    public void A_claim_the_reply_does_not_carry_is_null()
    {
        Assert.Null(ClaimsExtractor.StringClaim(Json("""{"name":"Ben"}"""), "preferred_username"));
    }

    [Theory]
    [InlineData("""{"preferred_username":7}""")]
    [InlineData("""{"preferred_username":null}""")]
    [InlineData("""{"preferred_username":["ben"]}""")]
    [InlineData("""{"preferred_username":{"value":"ben"}}""")]
    public void A_claim_that_is_not_a_string_is_null_rather_than_its_json(string reply)
    {
        Assert.Null(ClaimsExtractor.StringClaim(Json(reply), "preferred_username"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_no_name(string value)
    {
        // Otherwise the JIT-created account would be called " " — and the fallback to the
        // subject, which at least identifies somebody, would never be reached.
        Assert.Null(ClaimsExtractor.StringClaim(
            Json($"{{\"preferred_username\":\"{value}\"}}"), "preferred_username"));
    }

    [Fact]
    public void A_reply_that_never_arrived_carries_no_name()
    {
        Assert.Null(ClaimsExtractor.StringClaim(null, "preferred_username"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"ben\"")]
    [InlineData("7")]
    public void A_name_is_only_read_out_of_an_object(string reply)
    {
        Assert.Null(ClaimsExtractor.StringClaim(Json(reply), "preferred_username"));
    }
}
