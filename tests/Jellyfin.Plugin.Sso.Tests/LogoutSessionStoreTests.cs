using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

public sealed class LogoutSessionStoreTests
{
    private static ProviderConfig Provider() => new()
    {
        Id = "authentik",
        Authority = "https://idp.example.com/application/o/jellyfin/",
        ClientId = "jellyfin",
    };

    [Fact]
    public void A_handle_returns_the_original_token_once()
    {
        var store = new LogoutSessionStore();
        var provider = Provider();
        var handle = store.Issue(provider, "original-id-token");

        Assert.DoesNotContain("original-id-token", handle);
        var session = Assert.IsType<LogoutSessionStore.Session>(store.Consume(handle));
        Assert.Equal("original-id-token", session.IdentityToken);
        Assert.True(session.Matches(provider));
        Assert.Null(store.Consume(handle));
    }

    [Fact]
    public void Browser_sessions_do_not_share_tokens_even_for_the_same_provider()
    {
        var store = new LogoutSessionStore();
        var first = store.Issue(Provider(), "first-token");
        var second = store.Issue(Provider(), "second-token");

        Assert.NotEqual(first, second);
        Assert.Equal("first-token", store.Consume(first)!.IdentityToken);
        Assert.Equal("second-token", store.Consume(second)!.IdentityToken);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("authority")]
    [InlineData("client")]
    public void A_token_is_not_used_for_a_different_provider_or_changed_configuration(string field)
    {
        var store = new LogoutSessionStore();
        var provider = Provider();
        var handle = store.Issue(provider, "original-id-token");

        if (field == "id") provider.Id = "other";
        if (field == "authority") provider.Authority = "https://other.example.com";
        if (field == "client") provider.ClientId = "other-client";

        Assert.False(store.Consume(handle)!.Matches(provider));
    }

    [Fact]
    public void Missing_unknown_and_expired_handles_return_no_token()
    {
        var store = new LogoutSessionStore(TimeSpan.FromMilliseconds(-1));

        Assert.Null(store.Consume(null));
        Assert.Null(store.Consume("unknown"));
        Assert.Null(store.Consume(store.Issue(Provider(), "old-token")));
    }

    [Fact]
    public void Abandoned_sessions_are_bounded()
    {
        var store = new LogoutSessionStore();
        var oldest = store.Issue(Provider(), "oldest");
        for (var i = 0; i < LogoutSessionStore.MaxSessions; i++)
            store.Issue(Provider(), "token");

        Assert.Null(store.Consume(oldest));
    }
}
