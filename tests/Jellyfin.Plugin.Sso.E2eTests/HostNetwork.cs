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

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Returns once the host's interfaces are settled, or after twenty seconds — a host
    /// that never settles should not fail a test that may never notice, and the browser
    /// errors this guards against are named when they happen.
    /// </summary>
    public static async Task SettleAsync()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(Interfaces))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        string? last = null;
        var unchangedSince = TimeSpan.Zero;

        while (clock.Elapsed < Limit)
        {
            var (snapshot, pending) = Read();
            if (!string.Equals(snapshot, last, StringComparison.Ordinal))
            {
                last = snapshot;
                unchangedSince = clock.Elapsed;
            }
            else if (!pending && clock.Elapsed - unchangedSince >= Quiet)
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    /// <summary>
    /// The interfaces with their link state and IPv6 addresses, and whether any address is
    /// still waiting for duplicate address detection.
    /// </summary>
    internal static (string Snapshot, bool Pending) Read()
    {
        var parts = new List<string>();
        foreach (var path in Directory.GetDirectories(Interfaces).Order(StringComparer.Ordinal))
        {
            parts.Add($"{Path.GetFileName(path)}={ReadOrEmpty(Path.Combine(path, "operstate"))}");
        }

        var pending = false;
        var addresses = File.Exists(Ipv6Addresses) ? ReadLines(Ipv6Addresses) : [];
        foreach (var line in addresses.Order(StringComparer.Ordinal))
        {
            parts.Add(line);
            pending |= IsPending(line);
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
