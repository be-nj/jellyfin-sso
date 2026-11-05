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

    /// <summary>
    /// What the endpoint does: ask, do the work, then record. The two halves are separate
    /// so that a failure in between leaves the provider's retry a retry — recording first
    /// turned it into a refused replay and left the sessions the provider had ended alive.
    /// </summary>
    private static bool Act(
        LogoutTokenLedger ledger, string providerId, string? jti, string token,
        DateTimeOffset? now = null)
        => !ledger.AlreadyActedOn(providerId, jti, token, now)
            && ledger.Remember(providerId, jti, token, now);

    [Fact]
    public void A_token_is_accepted_once_and_then_refused()
    {
        Assert.True(Act(_ledger, "p1", "jti-1", Token));
        Assert.False(Act(_ledger, "p1", "jti-1", Token));
        Assert.False(Act(_ledger, "p1", "jti-1", Token));
    }

    /// <summary>A provider that sends no jti is covered by the token's own bytes.</summary>
    [Fact]
    public void A_token_without_a_jti_is_recognised_by_its_contents()
    {
        Assert.True(Act(_ledger, "p1", null, Token));
        Assert.False(Act(_ledger, "p1", null, Token));
        Assert.False(Act(_ledger, "p1", string.Empty, Token));

        Assert.True(Act(_ledger, "p1", null, Token + "x"));
    }

    [Fact]
    public void A_different_token_from_the_same_provider_is_accepted()
    {
        Assert.True(Act(_ledger, "p1", "jti-1", Token));
        Assert.True(Act(_ledger, "p1", "jti-2", Token));
    }

    /// <summary>
    /// Two providers may both mint a token called "1". One must not spend the other's.
    /// </summary>
    [Fact]
    public void The_same_jti_from_another_provider_is_its_own_token()
    {
        Assert.True(Act(_ledger, "p1", "jti-1", Token));
        Assert.True(Act(_ledger, "p2", "jti-1", Token));
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

        Assert.True(Act(ledger, "p1", "jti-1", Token, start));
        Assert.False(Act(ledger, "p1", "jti-1", Token, start + LogoutTokenValidator.MaxAge));
        Assert.False(Act(ledger, "p1", "jti-1", Token,
            start + LogoutTokenValidator.MaxAge + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void A_record_is_forgotten_once_its_token_could_no_longer_be_used()
    {
        var ledger = new LogoutTokenLedger(TimeSpan.FromSeconds(1));
        var start = DateTimeOffset.UtcNow;

        Assert.True(Act(ledger, "p1", "jti-1", Token, start));
        Assert.True(Act(ledger, "p1", "jti-1", Token, start + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void The_ledger_stops_growing_at_its_cap()
    {
        for (var i = 0; i < LogoutTokenLedger.MaxRemembered + 50; i++)
        {
            Assert.True(Act(_ledger, "p1", $"jti-{i}", Token));
        }

        // The newest record is the one that must survive: it is the replay still possible.
        Assert.False(Act(_ledger, "p1", $"jti-{LogoutTokenLedger.MaxRemembered + 49}", Token));
    }

    /// <summary>
    /// The ordering that matters. The endpoint used to record the token and then revoke;
    /// if the revocation failed, the provider's retry was answered "already been used"
    /// and the sessions it had ended stayed alive — Jellyfin's tokens do not expire on
    /// their own. Asking must not record.
    /// </summary>
    [Fact]
    public void Asking_does_not_spend_the_token()
    {
        Assert.False(_ledger.AlreadyActedOn("p1", "jti-1", Token));
        Assert.False(_ledger.AlreadyActedOn("p1", "jti-1", Token));

        // The work would have happened here and failed. A retry still gets through.
        Assert.False(_ledger.AlreadyActedOn("p1", "jti-1", Token));
        Assert.True(_ledger.Remember("p1", "jti-1", Token));
        Assert.True(_ledger.AlreadyActedOn("p1", "jti-1", Token));
    }

    /// <summary>
    /// Two posts of the same token arriving together both do the work — revoking twice is
    /// the same as revoking once — and exactly one of them records it.
    /// </summary>
    [Fact]
    public void Only_one_of_two_simultaneous_records_wins()
    {
        Assert.True(_ledger.Remember("p1", "jti-1", Token));
        Assert.False(_ledger.Remember("p1", "jti-1", Token));
    }

}
