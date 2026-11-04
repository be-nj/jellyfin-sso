using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A logout token used to be accepted as often as it was presented, for as long as its
/// iat was fresh. The ledger is what makes it single-use, the way the pending state and
/// the logout handle already are.
/// </summary>
public sealed class LogoutTokenLedgerTests
{
    private readonly LogoutTokenLedger _ledger = new();

    private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJiZW4ifQ.signature";

    [Fact]
    public void A_token_is_accepted_once_and_then_refused()
    {
        Assert.True(_ledger.Accept("p1", "jti-1", Token));
        Assert.False(_ledger.Accept("p1", "jti-1", Token));
        Assert.False(_ledger.Accept("p1", "jti-1", Token));
    }

    /// <summary>A provider that sends no jti is covered by the token's own bytes.</summary>
    [Fact]
    public void A_token_without_a_jti_is_recognised_by_its_contents()
    {
        Assert.True(_ledger.Accept("p1", null, Token));
        Assert.False(_ledger.Accept("p1", null, Token));
        Assert.False(_ledger.Accept("p1", string.Empty, Token));

        Assert.True(_ledger.Accept("p1", null, Token + "x"));
    }

    [Fact]
    public void A_different_token_from_the_same_provider_is_accepted()
    {
        Assert.True(_ledger.Accept("p1", "jti-1", Token));
        Assert.True(_ledger.Accept("p1", "jti-2", Token));
    }

    /// <summary>
    /// Two providers may both mint a token called "1". One must not spend the other's.
    /// </summary>
    [Fact]
    public void The_same_jti_from_another_provider_is_its_own_token()
    {
        Assert.True(_ledger.Accept("p1", "jti-1", Token));
        Assert.True(_ledger.Accept("p2", "jti-1", Token));
    }

    /// <summary>
    /// A record outlives the window in which its token would still pass validation, so a
    /// token can never come back after its record has gone.
    /// </summary>
    [Fact]
    public void A_record_outlives_the_age_its_token_may_reach()
    {
        var ledger = new LogoutTokenLedger(TimeSpan.FromMinutes(30));
        var start = DateTimeOffset.UtcNow;

        Assert.True(ledger.Accept("p1", "jti-1", Token, start));
        Assert.False(ledger.Accept("p1", "jti-1", Token, start + LogoutTokenValidator.MaxAge));
        Assert.False(ledger.Accept("p1", "jti-1", Token,
            start + LogoutTokenValidator.MaxAge + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void A_record_is_forgotten_once_its_token_could_no_longer_be_used()
    {
        var ledger = new LogoutTokenLedger(TimeSpan.FromSeconds(1));
        var start = DateTimeOffset.UtcNow;

        Assert.True(ledger.Accept("p1", "jti-1", Token, start));
        Assert.True(ledger.Accept("p1", "jti-1", Token, start + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void The_ledger_stops_growing_at_its_cap()
    {
        for (var i = 0; i < LogoutTokenLedger.MaxRemembered + 50; i++)
        {
            Assert.True(_ledger.Accept("p1", $"jti-{i}", Token));
        }

        // The newest record is the one that must survive: it is the replay still possible.
        Assert.False(_ledger.Accept("p1", $"jti-{LogoutTokenLedger.MaxRemembered + 49}", Token));
    }
}
