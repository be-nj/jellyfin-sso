using System.Globalization;
using System.Net;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The server-side request guard, held against an independent judge. The guard decides by
/// reading bytes; the judge below decides from a table of address blocks written out
/// from the IANA special-purpose registries, and the two are compared over thousands of
/// generated addresses: every block in the table, its first and last address, the
/// addresses either side of it, and every way of writing an address down that
/// <c>System.Uri</c> accepts and a resolver reads.
/// </summary>
public sealed class OutboundAddressesPropertyTests
{
    private const int Seed = 20_261_125;
    private const int Cases = 1_500;

    private enum Verdict
    {
        Public,
        NotPublic,

        /// <summary>
        /// Documentation and anycast ranges the guard has deliberately left alone, and which no
        /// server answers from: neither verdict is held against it.
        /// </summary>
        Either,
    }

    // --- The judge ------------------------------------------------------------

    private static readonly string[] V4NotPublic =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/4",
        "240.0.0.0/4",
    ];

    private static readonly string[] V4Either =
    [
        "192.0.2.0/24", "192.88.99.0/24", "198.51.100.0/24", "203.0.113.0/24",
    ];

    private static (uint Network, int Length) V4Block(string cidr)
    {
        var parts = cidr.Split('/');
        var bytes = IPAddress.Parse(parts[0]).GetAddressBytes();
        return (V4Value(bytes), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static uint V4Value(byte[] bytes)
        => ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static bool InV4(uint value, string cidr)
    {
        var (network, length) = V4Block(cidr);
        var mask = length == 0 ? 0 : uint.MaxValue << (32 - length);
        return (value & mask) == (network & mask);
    }

    private static Verdict Judge4(uint value)
    {
        if (V4NotPublic.Any(block => InV4(value, block)))
        {
            return Verdict.NotPublic;
        }

        return V4Either.Any(block => InV4(value, block)) ? Verdict.Either : Verdict.Public;
    }

    private static UInt128 V6Value(byte[] bytes)
    {
        UInt128 value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static (UInt128 Network, int Length) V6Block(string cidr)
    {
        var parts = cidr.Split('/');
        return (V6Value(IPAddress.Parse(parts[0]).GetAddressBytes()),
            int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static UInt128 V6Mask(int length)
        => length == 0 ? 0 : UInt128.MaxValue << (128 - length);

    private static bool InV6(UInt128 value, string cidr)
    {
        var (network, length) = V6Block(cidr);
        return (value & V6Mask(length)) == (network & V6Mask(length));
    }

    /// <summary>
    /// RFC 4291 and the registries around it: an address that carries an IPv4 address is
    /// the IPv4 address it carries; the tunnelling and local-use prefixes that cannot be
    /// told apart from the inside are not public; and outside 2000::/3 nothing is.
    /// </summary>
    private static Verdict Judge6(byte[] bytes)
    {
        var value = V6Value(bytes);
        var low = (uint)(value & uint.MaxValue);

        if (InV6(value, "::ffff:0:0/96"))
        {
            return Judge4(low);
        }

        if (InV6(value, "::/96"))
        {
            // "::" and "::1" are the unspecified and loopback addresses; the rest is the
            // deprecated IPv4-compatible form.
            return low <= 1 ? Verdict.NotPublic : Judge4(low);
        }

        if (InV6(value, "64:ff9b::/96"))
        {
            return Judge4(low);
        }

        // Local-use NAT64: reachable only through a translator an operator runs, which is
        // to say "inside".
        if (InV6(value, "64:ff9b:1::/48"))
        {
            return Verdict.NotPublic;
        }

        if (InV6(value, "2002::/16"))
        {
            return Judge4((uint)((value >> 80) & uint.MaxValue));
        }

        // Teredo, benchmarking and documentation space.
        if (InV6(value, "2001::/32") || InV6(value, "2001:2::/48")
            || InV6(value, "2001:db8::/32") || InV6(value, "3fff::/20"))
        {
            return Verdict.NotPublic;
        }

        if (InV6(value, "2001::/23"))
        {
            return Verdict.Either;
        }

        return InV6(value, "2000::/3") ? Verdict.Public : Verdict.NotPublic;
    }

    private static Verdict JudgeHeld(byte[] bytes) => Judge6(bytes);

    // --- Generators -----------------------------------------------------------

    private static uint RandomV4(Random random)
    {
        switch (random.Next(4))
        {
            case 0:
            case 1:
                var blocks = V4NotPublic.Concat(V4Either).ToArray();
                var (network, length) = V4Block(blocks[random.Next(blocks.Length)]);
                var host = length == 32 ? 0 : (uint)random.NextInt64(0, 1L << (32 - length));
                var mask = length == 0 ? 0 : uint.MaxValue << (32 - length);
                var first = network & mask;
                var last = first | ~mask;
                return random.Next(6) switch
                {
                    0 => first,
                    1 => last,
                    2 => first - 1,
                    3 => last + 1,
                    _ => first | host,
                };
            default:
                return (uint)random.NextInt64(0, 1L << 32);
        }
    }

    private static byte[] V4Bytes(uint value)
        => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] FromCidr6(string cidr, Random random)
    {
        var (network, length) = V6Block(cidr);
        var noise = new byte[16];
        random.NextBytes(noise);
        var value = (network & V6Mask(length)) | (V6Value(noise) & ~V6Mask(length));
        var bytes = new byte[16];
        for (var i = 15; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return bytes;
    }

    private static readonly string[] V6Blocks =
    [
        "::/96", "::ffff:0:0/96", "64:ff9b::/96", "64:ff9b:1::/48", "2002::/16", "2001::/32",
        "2001:2::/48", "2001:db8::/32", "3fff::/20", "2000::/3", "fc00::/7", "fe80::/10",
        "fec0::/10", "ff00::/8", "100::/64", "::ffff:0:0:0/96", "::/0", "2001:4860::/32",
        "2606:4700::/32", "2a00::/12",
    ];

    private static byte[] RandomV6(Random random)
    {
        var scheme = random.Next(10);
        if (scheme < 6)
        {
            return FromCidr6(V6Blocks[random.Next(V6Blocks.Length)], random);
        }

        // One of the forms that carry an IPv4 address, with an address from the table.
        var carried = V4Bytes(RandomV4(random));
        var bytes = FromCidr6(
            new[] { "::ffff:0:0/96", "::/96", "64:ff9b::/96", "2002::/16" }[random.Next(4)], random);
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            carried.CopyTo(bytes, 2);
        }
        else
        {
            carried.CopyTo(bytes, 12);
        }

        return bytes;
    }

    // --- Every way of writing an address down ---------------------------------

    private static IEnumerable<string> V4Forms(uint n, Random random)
    {
        var o = V4Bytes(n);
        yield return string.Join('.', o);
        yield return n.ToString(CultureInfo.InvariantCulture);
        yield return "0x" + n.ToString("x", CultureInfo.InvariantCulture);
        yield return "0" + Convert.ToString(n, 8);
        yield return string.Join('.', o.Select(b => "0" + Convert.ToString(b, 8)));
        yield return string.Join('.', o.Select(b => "0x" + b.ToString("X", CultureInfo.InvariantCulture)));
        yield return $"{o[0]}.{o[1]}.{(o[2] << 8) | o[3]}";
        yield return $"{o[0]}.{(o[1] << 16) | (o[2] << 8) | o[3]}";
        yield return string.Join('.', o.Select(b => random.Next(3) switch
        {
            0 => b.ToString(CultureInfo.InvariantCulture),
            1 => "0x" + b.ToString("x", CultureInfo.InvariantCulture),
            _ => "0" + Convert.ToString(b, 8),
        }));
    }

    private static IEnumerable<string> V6Forms(byte[] bytes, Random random)
    {
        var groups = Enumerable.Range(0, 8).Select(i => (bytes[2 * i] << 8) | bytes[(2 * i) + 1]).ToArray();
        yield return new IPAddress(bytes).ToString();
        yield return string.Join(':', groups.Select(g => g.ToString("x4", CultureInfo.InvariantCulture)));
        yield return string.Join(':', groups.Select(g => g.ToString("X4", CultureInfo.InvariantCulture)));
        yield return string.Join(':', groups.Select(g => g.ToString("x", CultureInfo.InvariantCulture)));
        yield return string.Join(':', groups.Take(6).Select(g => g.ToString("x", CultureInfo.InvariantCulture)))
            + $":{bytes[12]}.{bytes[13]}.{bytes[14]}.{bytes[15]}";
        yield return new IPAddress(bytes).ToString() + "%25" + random.Next(1, 20).ToString(CultureInfo.InvariantCulture);
        yield return new IPAddress(bytes).ToString() + "%25eth0";
    }

    private static async Task<bool?> Allowed(string host)
    {
        // The route a configured or claimed address takes: a Uri, then AllowedAsync, which
        // resolves the host the way the fetch would. Numeric forms need no network.
        if (!Uri.TryCreate("http://" + host + "/avatar.png", UriKind.Absolute, out var uri))
        {
            // What System.Uri refuses is never fetched, so there is nothing to judge.
            return null;
        }

        return await OutboundAddresses.AllowedAsync(uri, null, allowPrivateNetworks: false);
    }

    // --- Properties -----------------------------------------------------------

    /// <summary>
    /// Attack: a picture claim or a brand document naming an address on the server's own
    /// network. The guard has to agree with the registry for every IPv4 address, not for the
    /// handful somebody thought of.
    /// </summary>
    [Fact]
    public void An_IPv4_address_is_public_exactly_when_the_registries_say_so()
    {
        var random = new Random(Seed);
        for (var i = 0; i < Cases * 3; i++)
        {
            var n = RandomV4(random);
            var verdict = Judge4(n);
            if (verdict == Verdict.Either)
            {
                continue;
            }

            var address = new IPAddress(V4Bytes(n));
            Assert.True(
                OutboundAddresses.IsPublic(address) == (verdict == Verdict.Public),
                $"{address} is {verdict} but IsPublic said {OutboundAddresses.IsPublic(address)}");
        }
    }

    /// <summary>
    /// Attack: the same address written so that a filter that compares text misses it. The
    /// host is parsed by <c>System.Uri</c> and resolved by the operating system, and both
    /// accept decimal, octal, hexadecimal and short forms of a dotted address.
    /// </summary>
    [Fact]
    public async Task An_IPv4_address_is_judged_by_the_address_not_by_how_it_is_written()
    {
        var random = new Random(Seed + 1);
        for (var i = 0; i < Cases; i++)
        {
            var n = RandomV4(random);
            var verdict = Judge4(n);
            if (verdict == Verdict.Either)
            {
                continue;
            }

            foreach (var form in V4Forms(n, random))
            {
                var allowed = await Allowed(form);
                if (allowed is null)
                {
                    continue;
                }

                Assert.True(
                    allowed == (verdict == Verdict.Public),
                    $"{string.Join('.', V4Bytes(n))} written as '{form}' is {verdict} but AllowedAsync said {allowed}");
            }
        }
    }

    /// <summary>
    /// Attack: an IPv6 address that stands for something inside - loopback, a unique-local or
    /// link-local address, a site-local one, an embedded private IPv4 address in any of the
    /// mapped, compatible, NAT64 and 6to4 forms, or a tunnel prefix - presented as a public one.
    /// </summary>
    [Fact]
    public void An_IPv6_address_is_public_exactly_when_the_registries_say_so()
    {
        var random = new Random(Seed + 2);
        for (var i = 0; i < Cases * 3; i++)
        {
            var bytes = RandomV6(random);
            var verdict = JudgeHeld(bytes);
            if (verdict == Verdict.Either)
            {
                continue;
            }

            var address = new IPAddress(bytes);
            Assert.True(
                OutboundAddresses.IsPublic(address) == (verdict == Verdict.Public),
                $"{address} is {verdict} but IsPublic said {OutboundAddresses.IsPublic(address)}");
        }
    }

    /// <summary>
    /// The "never allowed" half of the property, split out so that a failure says which half
    /// it is: an address the judge calls not public that the guard lets through.
    /// </summary>
    [Fact]
    public void No_IPv6_address_inside_the_network_is_ever_allowed()
    {
        var random = new Random(Seed + 3);
        for (var i = 0; i < Cases * 3; i++)
        {
            var bytes = RandomV6(random);
            if (JudgeHeld(bytes) != Verdict.NotPublic)
            {
                continue;
            }

            var address = new IPAddress(bytes);
            Assert.False(OutboundAddresses.IsPublic(address), $"{address} was allowed");
        }
    }

    /// <summary>
    /// Attack: a bracketed IPv6 literal with a zone id, in full, compressed, upper-case or
    /// dotted-tail form.
    /// </summary>
    [Fact]
    public async Task An_IPv6_address_is_judged_by_the_address_not_by_how_it_is_written()
    {
        var random = new Random(Seed + 4);
        for (var i = 0; i < Cases; i++)
        {
            var bytes = RandomV6(random);
            var verdict = JudgeHeld(bytes);
            if (verdict != Verdict.NotPublic)
            {
                continue;
            }

            foreach (var form in V6Forms(bytes, random))
            {
                Assert.True(
                    await Allowed("[" + form + "]") != true,
                    $"{new IPAddress(bytes)} written as '[{form}]' was allowed");
            }
        }
    }

    /// <summary>
    /// The connect-time check takes an <c>IPAddress</c> with no host in front of it; the
    /// provider's own host is the only exception, and it is matched by name.
    /// </summary>
    [Fact]
    public void The_connect_check_never_lets_a_private_address_through_under_another_name()
    {
        var random = new Random(Seed + 5);
        for (var i = 0; i < Cases; i++)
        {
            var bytes = random.Next(2) == 0 ? V4Bytes(RandomV4(random)) : RandomV6(random);
            var judged = bytes.Length == 4 ? Judge4(V4Value(bytes)) : JudgeHeld(bytes);
            if (judged != Verdict.NotPublic)
            {
                continue;
            }

            var host = PropertyGen.Hostile(random, 3);
            var authority = new Uri("https://idp.example.com/");
            Assert.False(
                OutboundAddresses.AddressAllowed(new IPAddress(bytes), host, authority, allowPrivateNetworks: false),
                $"{new IPAddress(bytes)} was allowed for the name '{host}'");
        }
    }

    // \u2500\u2500\u2500 Found by the properties above, and fixed \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    /// <summary>
    /// Attack: a Teredo address (2001:0::/32). The prefix is a tunnel to a relay, and the
    /// client's IPv4 address is stored in it inverted, so a host that has a Teredo interface
    /// would deliver to whichever address the URL spells out. The guard calls the whole
    /// prefix public because it sits inside 2000::/3.
    /// </summary>
    [Fact]
    public void A_Teredo_address_is_not_public()
    {
        var random = new Random(Seed + 6);
        for (var i = 0; i < 200; i++)
        {
            var address = new IPAddress(FromCidr6("2001::/32", random));
            Assert.False(OutboundAddresses.IsPublic(address), $"{address} was allowed");
        }
    }

    /// <summary>
    /// Attack: the IPv6 benchmarking range (2001:2::/48). The IPv4 one, 198.18.0.0/15, is
    /// refused; its IPv6 counterpart goes through, as it lies inside 2000::/3.
    /// </summary>
    [Fact]
    public void The_IPv6_benchmarking_range_is_not_public()
    {
        var random = new Random(Seed + 7);
        for (var i = 0; i < 200; i++)
        {
            var address = new IPAddress(FromCidr6("2001:2::/48", random));
            Assert.False(OutboundAddresses.IsPublic(address), $"{address} was allowed");
        }
    }

    /// <summary>
    /// Attack: local-use NAT64 (64:ff9b:1::/48). Unlike 64:ff9b::/96, a /48 prefix does not
    /// carry the IPv4 address in the last four bytes (RFC 6052 puts it in bytes 6, 7, 9 and
    /// 10), so reading the last four judges an address the translator never sees: this one
    /// reaches 10.0.0.1 and the guard reads 8.8.8.8.
    /// </summary>
    [Fact]
    public void A_local_use_NAT64_address_is_not_judged_by_the_wrong_four_bytes()
    {
        var address = IPAddress.Parse("64:ff9b:1:a00:0:100:808:808");

        Assert.False(OutboundAddresses.IsPublic(address));
    }
}
