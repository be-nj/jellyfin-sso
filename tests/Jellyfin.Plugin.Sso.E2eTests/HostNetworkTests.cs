using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// <see cref="HostNetwork"/> without a host: what it waits for is one column of
/// /proc/net/if_inet6, and how long is a loop over a clock, so both are handed in here.
/// The lines are in the kernel's format, and the times are the ones measured on the
/// workstation: a container's veth tentative at once, permanent 1.7 seconds later.
/// </summary>
public class HostNetworkTests
{
    private const string Tentative = "fe80000000000000a8c7f1fffe2b3c4d 1ac6 40 20 c0 vethb33ef0a";
    private const string Permanent = "fe80000000000000a8c7f1fffe2b3c4d 1ac6 40 20 80 vethb33ef0a";
    private const string DadFailed = "fe80000000000000a8c7f1fffe2b3c4d 1ac6 40 20 c8 vethb33ef0a";

    [Fact]
    public void An_address_still_in_duplicate_address_detection_is_waited_for()
        => Assert.True(HostNetwork.IsPending(Tentative));

    [Fact]
    public void A_detected_address_is_not()
        => Assert.False(HostNetwork.IsPending(Permanent));

    [Fact]
    public void A_failed_detection_is_not_waited_for_because_it_never_ends()
        => Assert.False(HostNetwork.IsPending(DadFailed));

    [Fact]
    public void A_line_that_is_not_an_address_is_not_pending()
        => Assert.False(HostNetwork.IsPending("garbage"));

    [Fact]
    public async Task A_quiet_host_is_waited_on_for_the_quiet_half_second_only()
    {
        var host = new Host(_ => [Permanent]);

        Assert.True(await host.SettleAsync());

        Assert.InRange(host.Now, HostNetwork.Quiet, HostNetwork.Quiet + Slack);
    }

    [Fact]
    public async Task A_tentative_address_is_waited_out_and_the_quiet_time_starts_when_it_is_done()
    {
        var done = TimeSpan.FromSeconds(1.7);
        var host = new Host(now => [now < done ? Tentative : Permanent]);

        Assert.True(await host.SettleAsync());

        Assert.InRange(host.Now, done + HostNetwork.Quiet, done + HostNetwork.Quiet + Slack);
    }

    [Fact]
    public async Task An_interface_that_goes_away_starts_the_quiet_time_again()
    {
        var gone = TimeSpan.FromSeconds(0.3);
        var host = new Host(now => now < gone ? [Permanent] : []);

        Assert.True(await host.SettleAsync());

        Assert.InRange(host.Now, gone + HostNetwork.Quiet, gone + HostNetwork.Quiet + Slack);
    }

    [Fact]
    public async Task An_address_that_never_finishes_costs_one_wait_its_full_length_and_the_next_none()
    {
        var host = new Host(_ => [Tentative]);

        Assert.False(await host.SettleAsync());
        Assert.InRange(host.Now, HostNetwork.Limit, HostNetwork.Limit + Slack);
        Assert.Equal([Tentative], host.GivenUpOn.Keys);

        var second = new Host(_ => [Tentative], host.GivenUpOn);
        Assert.True(await second.SettleAsync());
        Assert.InRange(second.Now, HostNetwork.Quiet, HostNetwork.Quiet + Slack);
    }

    [Fact]
    public async Task A_new_tentative_address_is_still_waited_for_after_another_was_given_up_on()
    {
        var other = Tentative.Replace("vethb33ef0a", "veth0000001", StringComparison.Ordinal);
        var done = TimeSpan.FromSeconds(1.7);
        var givenUpOn = new ConcurrentDictionary<string, byte> { [Tentative] = 0 };
        var host = new Host(now => now < done ? [Tentative, other] : [Tentative], givenUpOn);

        Assert.True(await host.SettleAsync());

        Assert.InRange(host.Now, done + HostNetwork.Quiet, done + HostNetwork.Quiet + Slack);
    }

    // Two polls: the loop notices a change at the poll after it happened, and returns at
    // the poll after the quiet time is up.
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A host whose address lines depend on the time, and a clock that only moves when the
    /// loop waits — so the times asserted above are exact, and no test sleeps.
    /// </summary>
    private sealed class Host(Func<TimeSpan, string[]> lines, ConcurrentDictionary<string, byte>? givenUpOn = null)
    {
        public TimeSpan Now { get; private set; }

        public ConcurrentDictionary<string, byte> GivenUpOn { get; } = givenUpOn ?? new();

        public Task<bool> SettleAsync()
            => HostNetwork.SettleAsync(
                () =>
                {
                    var current = lines(Now);
                    return (string.Join('\n', current), [.. current.Where(HostNetwork.IsPending)]);
                },
                () => Now,
                delay =>
                {
                    Now += delay;
                    return Task.CompletedTask;
                },
                GivenUpOn);
    }
}
