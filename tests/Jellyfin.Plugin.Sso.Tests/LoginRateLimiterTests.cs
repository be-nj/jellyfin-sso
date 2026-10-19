using Jellyfin.Plugin.Sso.Auth;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The limit in front of the anonymous parts of a login. The thing being protected is not
/// a secret — those endpoints give nothing away — but the work behind them: a pending
/// state per start, a token exchange per callback, a signature check per logout. The
/// tests pin down that a household is never refused and a script always is.
/// </summary>
public sealed class LoginRateLimiterTests
{
    private const int Limit = 5;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private int _limit = Limit;

    private LoginRateLimiter Limiter() => new(Window, () => _now, () => _limit);

    [Fact]
    public void A_caller_under_the_limit_is_let_through_every_time()
    {
        var limiter = Limiter();

        for (var i = 0; i < Limit; i++)
        {
            Assert.True(limiter.Check("10.0.0.1").Allowed, $"request {i + 1} was refused");
        }
    }

    [Fact]
    public void The_request_past_the_limit_is_refused()
    {
        var limiter = Limiter();
        for (var i = 0; i < Limit; i++)
        {
            limiter.Check("10.0.0.1");
        }

        Assert.False(limiter.Check("10.0.0.1").Allowed);
    }

    [Fact]
    public void A_refusal_says_how_long_the_window_still_has_to_run()
    {
        var limiter = Limiter();
        for (var i = 0; i < Limit; i++)
        {
            limiter.Check("10.0.0.1");
        }

        _now += TimeSpan.FromSeconds(20);
        var decision = limiter.Check("10.0.0.1");

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(40), decision.RetryAfter);
    }

    [Fact]
    public void Only_the_first_refusal_of_a_window_is_worth_a_log_line()
    {
        var limiter = Limiter();
        for (var i = 0; i < Limit; i++)
        {
            limiter.Check("10.0.0.1");
        }

        Assert.True(limiter.Check("10.0.0.1").FirstRefusal);
        Assert.False(limiter.Check("10.0.0.1").FirstRefusal);
        Assert.False(limiter.Check("10.0.0.1").FirstRefusal);
    }

    [Fact]
    public void The_next_window_lets_the_same_caller_back_in()
    {
        var limiter = Limiter();
        for (var i = 0; i < Limit + 3; i++)
        {
            limiter.Check("10.0.0.1");
        }

        _now += Window;

        var decision = limiter.Check("10.0.0.1");
        Assert.True(decision.Allowed);

        // And the log line is due again if they fill the new window too: that first
        // request spent one, so the rest of the allowance is what is left.
        for (var i = 1; i < Limit; i++)
        {
            Assert.True(limiter.Check("10.0.0.1").Allowed);
        }

        Assert.True(limiter.Check("10.0.0.1").FirstRefusal);
    }

    [Fact]
    public void One_callers_flood_does_not_shut_out_another()
    {
        var limiter = Limiter();
        for (var i = 0; i < Limit * 4; i++)
        {
            limiter.Check("10.0.0.1");
        }

        Assert.True(limiter.Check("10.0.0.2").Allowed);
    }

    [Fact]
    public void Requests_without_a_readable_address_share_one_count()
    {
        var limiter = Limiter();

        for (var i = 0; i < Limit; i++)
        {
            Assert.True(limiter.Check(null).Allowed);
        }

        Assert.False(limiter.Check(null).Allowed);
        Assert.False(limiter.Check("   ").Allowed);
    }

    [Fact]
    public void A_limit_of_zero_turns_the_whole_thing_off()
    {
        _limit = 0;
        var limiter = Limiter();

        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(limiter.Check("10.0.0.1").Allowed);
        }

        // Nothing was counted either, so turning it back on starts from nothing.
        Assert.Equal(0, limiter.TrackedCallers);
    }

    [Fact]
    public void A_lower_limit_takes_effect_without_a_restart()
    {
        var limiter = Limiter();
        Assert.True(limiter.Check("10.0.0.1").Allowed);

        _limit = 1;

        Assert.False(limiter.Check("10.0.0.1").Allowed);
    }

    [Fact]
    public void A_caller_that_stopped_knocking_is_forgotten()
    {
        var limiter = Limiter();
        limiter.Check("10.0.0.1");
        Assert.Equal(1, limiter.TrackedCallers);

        _now += Window;
        limiter.Check("10.0.0.2");

        Assert.Equal(1, limiter.TrackedCallers);
    }

    [Fact]
    public void Saving_the_configuration_lets_a_refused_caller_back_in()
    {
        var limiter = Limiter();
        for (var i = 0; i < Limit + 1; i++)
        {
            limiter.Check("10.0.0.1");
        }

        Assert.False(limiter.Check("10.0.0.1").Allowed);

        // Raising the limit is something an administrator does because somebody is being
        // turned away now, so the counts do not outlive the save.
        limiter.Reset();

        Assert.True(limiter.Check("10.0.0.1").Allowed);
    }

    [Fact]
    public void The_table_of_callers_has_an_end()
    {
        var limiter = Limiter();

        // One address per request, as a caller with a /64 to walk would send them. They
        // all fall inside one window, so none of them expires on its own.
        for (var i = 0; i < LoginRateLimiter.MaxTrackedCallers + 2_000; i++)
        {
            limiter.Check($"2001:db8::{i:x}");
        }

        Assert.True(
            limiter.TrackedCallers <= LoginRateLimiter.MaxTrackedCallers,
            $"{limiter.TrackedCallers} addresses are being counted.");
    }
}
