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
