using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Waits until the host has finished configuring the network interfaces a container start
/// or stop changed, so that Chromium does not see the change while a page is loading.
///
/// Chromium watches the host's addresses, and when one changes it ends every request in
/// flight with ERR_NETWORK_CHANGED — loopback included, which is where this suite's
/// browsers point. A container start adds a veth interface on the host, and the kernel
/// gives it an IPv6 link-local address at once but marks it tentative until duplicate
/// address detection is done: measured at 1.7 seconds after the start returned. When it
/// is done the kernel announces the address again, and that announcement is what reached
/// the browser. A probe that kept requests running across a container start caught
/// ERR_NETWORK_CHANGED in one of four rounds, about a second after the start, and a stop
/// cancelled one request of four as well.
///
/// So "the container is up" is not the moment the network is quiet. Twice in CI it was
/// not: PrefixedDeploymentTests starts its proxy and navigates straight away, and the first
/// time the navigation failed 237 ms into the test (#229), the second time a script of the
/// web client did, after the navigation had already succeeded, and the client sat on a
/// blank page for 90 seconds with a valid token (#287).
///
/// The wait is on the state itself, not on a delay: no tentative address left, and the
/// interfaces and their addresses unchanged for half a second, which covers the removals
/// a stop makes. A host without IPv6 has nothing announced late and returns at the first
/// quiet half second.
/// </summary>
internal static class HostNetwork
{
    private const string Ipv6Addresses = "/proc/net/if_inet6";
    private const string Interfaces = "/sys/class/net";

    // IFA_F_TENTATIVE and IFA_F_DADFAILED from linux/if_addr.h. A failed detection stays
    // tentative for good, so it is not waited for.
    private const int Tentative = 0x40;
    private const int DadFailed = 0x08;

    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    // Addresses that stayed tentative through a whole wait, on this host. An address on
    // an interface without carrier never leaves duplicate address detection, and waiting
    // for it again would cost every later call the full limit and tell nobody.
    private static readonly ConcurrentDictionary<string, byte> GivenUpOn = new();

    /// <summary>
    /// Returns once the host's interfaces are settled, or after twenty seconds — a host
    /// that never settles should not fail a test that may never notice, and the browser
    /// errors this guards against are named when they happen, next to <see cref="State"/>.
    /// </summary>
    public static Task SettleAsync()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(Interfaces))
        {
            return Task.CompletedTask;
        }

        var clock = Stopwatch.StartNew();
        return SettleAsync(Read, () => clock.Elapsed, Task.Delay, GivenUpOn);
    }

    /// <summary>
    /// What the waits so far ran into, for a failure message: a browser error on a host
    /// that never settled reads differently from one on a host that did.
    /// </summary>
    public static string State
        => GivenUpOn.IsEmpty
            ? "settled after every container start and stop"
            : $"gave up on addresses that stayed tentative for {Limit.TotalSeconds:0} s: {string.Join(" | ", GivenUpOn.Keys.Order(StringComparer.Ordinal))}";

    /// <summary>
    /// The wait itself, with the host and the clock handed in. True when it settled, false
    /// when the limit ran out — and then the addresses still tentative go into
    /// <paramref name="givenUpOn"/>, which later calls do not wait for.
    /// </summary>
    internal static async Task<bool> SettleAsync(
        Func<(string Snapshot, IReadOnlyList<string> Pending)> read,
        Func<TimeSpan> elapsed,
        Func<TimeSpan, Task> wait,
        ConcurrentDictionary<string, byte> givenUpOn)
    {
        string? last = null;
        var unchangedSince = TimeSpan.Zero;
        IReadOnlyList<string> waitingFor = [];

        while (elapsed() < Limit)
        {
            var (snapshot, pending) = read();
            waitingFor = [.. pending.Where(line => !givenUpOn.ContainsKey(line))];
            if (!string.Equals(snapshot, last, StringComparison.Ordinal))
            {
                last = snapshot;
                unchangedSince = elapsed();
            }
            else if (waitingFor.Count == 0 && elapsed() - unchangedSince >= Quiet)
            {
                return true;
            }

            await wait(Poll);
        }

        foreach (var line in waitingFor)
        {
            givenUpOn[line] = 0;
        }

        return false;
    }

    /// <summary>
    /// The interfaces with their link state and IPv6 addresses, and the addresses still
    /// waiting for duplicate address detection.
    /// </summary>
    internal static (string Snapshot, IReadOnlyList<string> Pending) Read()
    {
        var parts = new List<string>();
        foreach (var path in Directory.GetDirectories(Interfaces).Order(StringComparer.Ordinal))
        {
            parts.Add($"{Path.GetFileName(path)}={ReadOrEmpty(Path.Combine(path, "operstate"))}");
        }

        var pending = new List<string>();
        var addresses = File.Exists(Ipv6Addresses) ? ReadLines(Ipv6Addresses) : [];
        foreach (var line in addresses.Order(StringComparer.Ordinal))
        {
            parts.Add(line);
            if (IsPending(line))
            {
                pending.Add(line);
            }
        }

        return (string.Join('\n', parts), pending);
    }

    /// <summary>
    /// One line of /proc/net/if_inet6: address, interface index, prefix length, scope,
    /// flags and name, the numbers in hex.
    /// </summary>
    internal static bool IsPending(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 6
            && int.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags)
            && (flags & Tentative) != 0
            && (flags & DadFailed) == 0;
    }

    // An interface can disappear between listing and reading; that is a change like any
    // other and shows in the next snapshot.
    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
    }
}
