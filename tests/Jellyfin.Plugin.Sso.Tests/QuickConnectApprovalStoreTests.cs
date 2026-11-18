using Jellyfin.Plugin.Sso.Auth;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The approval token is what stands between a finished SSO login and a device the person
/// may never have seen. It has to be unguessable, usable once, and tied to the login that
/// produced it.
/// </summary>
public sealed class QuickConnectApprovalStoreTests
{
    private readonly QuickConnectApprovalStore _store = new();

    [Fact]
    public void An_approval_carries_the_user_and_the_code_back()
    {
        var userId = Guid.NewGuid();
        var token = _store.Issue(userId, "ben", "ABC123");

        var pending = _store.Consume(token);

        Assert.NotNull(pending);
        Assert.Equal(userId, pending!.UserId);
        Assert.Equal("ben", pending.Username);
        Assert.Equal("ABC123", pending.Code);
    }

    [Fact]
    public void An_approval_works_once()
    {
        var token = _store.Issue(Guid.NewGuid(), "ben", "ABC123");

        Assert.NotNull(_store.Consume(token));
        Assert.Null(_store.Consume(token));
    }

    // ─── A typed code that did not fit ────────────────────────────────────────

    /// <summary>
    /// One login, three codes at most. The token that carried a wrong code is spent; the
    /// page gets another, and after the third wrong code there is none.
    /// </summary>
    [Fact]
    public void A_wrong_code_gets_another_token_until_the_login_has_had_its_tries()
    {
        var first = _store.Consume(_store.Issue(Guid.NewGuid(), "ben", string.Empty))!;

        var second = _store.Consume(_store.IssueAgain(first))!;
        Assert.Equal(1, second.WrongCodes);
        Assert.Equal(first.UserId, second.UserId);

        var third = _store.Consume(_store.IssueAgain(second))!;
        Assert.Equal(2, third.WrongCodes);

        Assert.Null(_store.IssueAgain(third));
        Assert.Equal(3, QuickConnectApprovalStore.MaxWrongCodes);
    }

    [Fact]
    public void A_wrong_code_buys_no_time()
    {
        var first = _store.Consume(_store.Issue(Guid.NewGuid(), "ben", string.Empty))!;

        var second = _store.Consume(_store.IssueAgain(first))!;

        Assert.Equal(first.Expiry, second.Expiry);
    }

    [Fact]
    public void An_approval_that_has_run_out_is_not_handed_a_second_token()
    {
        var first = _store.Consume(_store.Issue(Guid.NewGuid(), "ben", string.Empty))!;

        Assert.Null(_store.IssueAgain(first with { Expiry = DateTimeOffset.UtcNow.AddSeconds(-1) }));
    }

    [Fact]
    public void The_token_for_another_try_works_once_like_the_first()
    {
        var first = _store.Consume(_store.Issue(Guid.NewGuid(), "ben", string.Empty))!;
        var again = _store.IssueAgain(first);

        Assert.NotNull(_store.Consume(again));
        Assert.Null(_store.Consume(again));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Anything_else_is_refused(string? token)
        => Assert.Null(_store.Consume(token));

    [Fact]
    public void Two_approvals_never_share_a_token()
    {
        var tokens = Enumerable.Range(0, 50)
            .Select(_ => _store.Issue(Guid.NewGuid(), "ben", "ABC123"))
            .ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token => Assert.Equal(64, token.Length));
    }
}
