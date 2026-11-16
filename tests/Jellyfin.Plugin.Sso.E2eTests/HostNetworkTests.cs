namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// What <see cref="HostNetwork"/> waits for is read out of one column of
/// /proc/net/if_inet6. The lines below are the ones a container's veth went through on the
/// workstation: tentative at once, permanent 1.7 seconds later.
/// </summary>
public class HostNetworkTests
{
    [Fact]
    public void An_address_still_in_duplicate_address_detection_is_waited_for()
        => Assert.True(HostNetwork.IsPending(
            "fe80000000000000a8c7f1fffe2b3c4d 00001ac6 40 20 c0 vethb33ef0a"));

    [Fact]
    public void A_detected_address_is_not()
        => Assert.False(HostNetwork.IsPending(
            "fe80000000000000a8c7f1fffe2b3c4d 00001ac6 40 20 80 vethb33ef0a"));

    [Fact]
    public void A_failed_detection_is_not_waited_for_because_it_never_ends()
        => Assert.False(HostNetwork.IsPending(
            "fe80000000000000a8c7f1fffe2b3c4d 00001ac6 40 20 c8 vethb33ef0a"));

    [Fact]
    public void A_line_that_is_not_an_address_is_not_pending()
        => Assert.False(HostNetwork.IsPending("garbage"));
}
