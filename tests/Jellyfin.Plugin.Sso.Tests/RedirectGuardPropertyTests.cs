using System.Text;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The guards on where a redirect or a link can point, held to one property: whatever
/// string is accepted, the address built from it stays on the origin the server is
/// published at. The inputs are the request headers anybody can send and the address an
/// administrator types, generated from a grammar of the fragments that move a URL
/// somewhere else (slashes in every spelling, backslashes, encoded slashes, control
/// characters, userinfo, lookalike characters) plus random code units.
///
/// "Stays on the origin" is judged the way a browser reads a Location header or an
/// href: tabs and line breaks removed, a backslash read as a slash, and then resolved
/// against the origin with <c>System.Uri</c>.
/// </summary>
public sealed class RedirectGuardPropertyTests
{
    private const int Seed = 20_261_126;
    private const int Cases = 3_000;

    private static readonly Uri Origin = new("https://jellyfin.example.com");

    private static readonly string[] PublicAddresses =
    [
        "https://jellyfin.example.com", "https://jellyfin.example.com/jf",
        "http://media.home.lan:8096", "https://[2001:db8::1]:8443/media/", "https://b\u00FCcher.example/jellyfin",
    ];

    private static string AsABrowserReadsIt(string reference)
        => reference.Replace("\t", string.Empty).Replace("\r", string.Empty)
            .Replace("\n", string.Empty).Replace('\\', '/');

    /// <summary>A path-absolute reference that the browser resolves on <see cref="Origin"/>.</summary>
    private static bool StaysOnOrigin(string pathAndQuery, Uri? origin = null)
    {
        origin ??= Origin;
        var seen = AsABrowserReadsIt(pathAndQuery);
        if (!seen.StartsWith('/') || seen.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        return Uri.TryCreate(origin, seen, out var resolved)
            && resolved.Scheme == origin.Scheme
            && resolved.Authority == origin.Authority
            && resolved.UserInfo.Length == 0;
    }

    private static string Candidate(Random random)
    {
        var text = PropertyGen.Hostile(random);
        return random.Next(3) switch
        {
            0 => "/" + text,
            1 => "/jellyfin" + text,
            _ => text,
        };
    }

    private static bool IsPathChar(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or '.' or '_' or '~' or '%' or '/' or '-';

    // ---- X-Forwarded-Prefix and the path base ---------------------------------------------

    /// <summary>
    /// Attack: <c>X-Forwarded-Prefix</c> turned into a link to somebody else's site, by
    /// <c>//host</c>, <c>/\host</c>, a scheme, userinfo, or a control character that the
    /// browser strips before it reads the address.
    /// </summary>
    [Fact]
    public void A_prefix_that_is_accepted_keeps_every_link_on_the_servers_own_origin()
    {
        var random = new Random(Seed);
        for (var i = 0; i < Cases; i++)
        {
            var forwarded = Candidate(random);
            var pathBase = random.Next(3) == 0 ? Candidate(random) : null;
            var prefix = ForwardedPaths.Prefix(forwarded, pathBase);

            var where = $"forwarded '{Show(forwarded)}', path base '{Show(pathBase)}', prefix '{Show(prefix)}'";
            Assert.True(prefix.All(IsPathChar), where);
            Assert.False(prefix.Contains("//", StringComparison.Ordinal), where);
            Assert.True(prefix.Length == 0 || prefix[0] == '/', where);
            Assert.False(prefix.EndsWith('/'), where);

            foreach (var link in new[] { "/web/index.html", "/sso/oidc/callback/p1", "/" })
            {
                Assert.True(StaysOnOrigin(prefix + link + "?x=1#y"), where + $" and link '{link}'");
            }

            Assert.True(
                Uri.TryCreate(ForwardedPaths.BaseUri("https", "jellyfin.example.com", prefix), UriKind.Absolute, out var baseUri)
                && baseUri.Authority == Origin.Authority,
                where);
        }
    }

    // ---- X-Forwarded-Host -----------------------------------------------------------------

    private static string HostLike(Random random)
    {
        const string Name = "abcdefghijklmnopqrstuvwxyz0123456789.-_";
        var builder = new StringBuilder();
        if (random.Next(5) == 0)
        {
            builder.Append('[').Append(random.Next(2) == 0 ? "::1" : "2001:db8::1").Append(']');
        }
        else
        {
            for (var n = random.Next(1, 14); n > 0; n--)
            {
                builder.Append(Name[random.Next(Name.Length)]);
            }
        }

        if (random.Next(3) == 0)
        {
            builder.Append(':').Append(random.Next(0, 100_000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // The value as a header carries it, with something extra on the end half the time.
        var host = random.Next(2) == 0 ? builder.ToString() : builder + PropertyGen.Hostile(random, 2);
        return host;
    }

    /// <summary>
    /// Attack: a forwarded host that carries more than a host: a line break after it, userinfo,
    /// a path, a port that is not a port. Whatever is accepted becomes the authority of the
    /// redirect URI that is registered with the provider, so it has to read back as exactly
    /// that authority.
    /// </summary>
    [Fact]
    public void A_host_that_is_accepted_is_an_authority_and_nothing_more()
    {
        var random = new Random(Seed + 1);
        for (var i = 0; i < Cases; i++)
        {
            var offered = random.Next(4) == 0 ? Candidate(random) : HostLike(random);
            var host = ForwardedPaths.Host(offered, "fallback:8096");
            if (host == "fallback:8096")
            {
                continue;
            }

            var where = $"offered '{Show(offered)}'";
            Assert.True(
                host.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                    or '.' or '_' or '-' or ':' or '[' or ']'),
                where);
            Assert.True(
                Uri.TryCreate("https://" + host + "/web/index.html", UriKind.Absolute, out var uri),
                where);
            Assert.Equal("/web/index.html", uri!.AbsolutePath);
            Assert.Equal(0, uri.UserInfo.Length);
            Assert.Equal(0, uri.Query.Length);
            // A name is kept as it is; a run of digits is an address in short form and is
            // rewritten ("7" is 0.0.0.7), which is Uri's reading and not a change of host.
            if (uri.HostNameType == UriHostNameType.Dns)
            {
                Assert.Equal(host.Split(':')[0], uri.Host, ignoreCase: true);
            }
        }
    }

    // ---- The configured public address ----------------------------------------------------

    private static string AddressLike(Random random)
    {
        var scheme = random.Next(6) switch { 0 => "http://", 1 => "ftp://", 2 => string.Empty, _ => "https://" };
        var host = random.Next(3) == 0 ? PropertyGen.Hostile(random, 3) : "jellyfin.example.com";
        var path = random.Next(3) == 0 ? "/" + PropertyGen.Hostile(random, 4) : random.Next(2) == 0 ? "/jf" : string.Empty;
        var text = scheme + host + path;
        return random.Next(4) == 0 ? " " + text + "\t" : text;
    }

    /// <summary>
    /// Attack: an address that <c>System.Uri</c> reads one way and the rest of the plugin another:
    /// <c>https://good.example\@evil.example</c>, a fragment or a tab inside the host, an
    /// encoded slash. The base address built from it has to read back as the same scheme, host
    /// and port, with no userinfo, and the sub-path has to be a path.
    /// </summary>
    [Fact]
    public void A_public_address_that_is_accepted_reads_back_as_the_same_origin()
    {
        var random = new Random(Seed + 2);
        var accepted = 0;
        for (var i = 0; i < Cases * 2; i++)
        {
            var typed = AddressLike(random);
            SsoAddress? configured;
            try
            {
                configured = SsoAddress.Configured(typed);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Configured threw {ex.GetType().Name} for '{Show(typed)}'");
                throw;
            }

            if (configured is null)
            {
                continue;
            }

            accepted++;
            var where = $"typed '{Show(typed)}', base '{configured.BaseUri}'";
            Assert.True(Uri.TryCreate(typed.Trim(), UriKind.Absolute, out var original), where);
            Assert.True(IsAuthority(configured.BaseUri), where);

            Assert.True(Uri.TryCreate(configured.BaseUri, UriKind.Absolute, out var rebuilt), where);
            Assert.True(original!.Scheme == rebuilt!.Scheme, where);
            Assert.True(
                string.Equals(original.IdnHost, rebuilt.IdnHost, StringComparison.OrdinalIgnoreCase),
                where + " host " + original.IdnHost + " vs " + rebuilt.IdnHost);
            Assert.True(original.Port == rebuilt.Port, where);
            Assert.Equal(0, rebuilt.UserInfo.Length);
            Assert.Equal(0, rebuilt.Query.Length);
            Assert.Equal(0, rebuilt.Fragment.Length);
            Assert.True(configured.BasePath.All(IsPathChar), where);
            Assert.False(configured.BasePath.Contains("//", StringComparison.Ordinal), where);
            Assert.True(StaysOnOrigin(configured.LocalWebClient, rebuilt), where);
            Assert.True(
                Uri.TryCreate(configured.CallbackUri("p1"), UriKind.Absolute, out var callback)
                && callback.Authority == rebuilt.Authority,
                where);
        }

        Assert.True(accepted > 100, $"only {accepted} addresses were accepted, so the property was barely tried");
    }

    // ---- The redirect to the public address -----------------------------------------------

    /// <summary>
    /// Attack: a request to the LAN address whose path or query is crafted so that the redirect
    /// to the public address ends up somewhere else. The target is built as the configured
    /// address plus the request's own path, so nothing in the request may change its authority.
    /// </summary>
    [Fact]
    public void The_redirect_to_the_public_address_never_leaves_it()
    {
        var random = new Random(Seed + 3);
        var redirected = 0;
        for (var i = 0; i < Cases; i++)
        {
            var publicAddress = PublicAddresses[random.Next(PublicAddresses.Length)];
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("lan.local:8096");
            context.Request.Path = new PathString("/" + PropertyGen.Hostile(random));
            context.Request.QueryString = QueryString.Create(
                Enumerable.Range(0, random.Next(0, 4)).Select(_ => KeyValuePair.Create<string, string?>(
                    PropertyGen.Hostile(random, 3), random.Next(2) == 0 ? null : PropertyGen.Hostile(random, 3))));
            if (random.Next(3) == 0)
            {
                context.Request.Headers["X-Forwarded-Host"] = PropertyGen.Hostile(random, 3);
                context.Request.Headers["X-Forwarded-Prefix"] = PropertyGen.Hostile(random, 3);
            }

            var at = SsoAddress.For(context.Request, publicAddress);
            var target = PublicAddressAttribute.Redirect(at, context.Request);
            if (target is null)
            {
                continue;
            }

            redirected++;
            var where = $"public '{publicAddress}', path '{Show(context.Request.Path.Value)}', target '{Show(target)}'";
            Assert.StartsWith(at.BaseUri + "/", target, StringComparison.Ordinal);
            Assert.DoesNotContain(target, c => c < 0x20 || c == 0x7F || c == '\\');
            Assert.True(Uri.TryCreate(target, UriKind.Absolute, out var parsed), where);
            Assert.True(Uri.TryCreate(at.BaseUri, UriKind.Absolute, out var expected), where);
            Assert.Equal(expected!.Scheme, parsed!.Scheme);
            Assert.Equal(expected.Authority, parsed.Authority);
            Assert.Equal(0, parsed.UserInfo.Length);
        }

        Assert.True(redirected > 1_000, $"only {redirected} requests were redirected");
    }

    // ---- Without a public address: what the headers decide --------------------------------

    /// <summary>
    /// Attack: forwarded headers, taken from whoever sent the request, steering the redirect
    /// URI. Without a configured public address they may name another host (the documented
    /// reason for that setting), but never a scheme other than http and https, a path
    /// that leaves the origin, or anything the browser would read as a second host.
    /// </summary>
    [Fact]
    public void The_address_the_headers_describe_is_one_origin_and_a_path_on_it()
    {
        var random = new Random(Seed + 4);
        for (var i = 0; i < Cases; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("jellyfin.example.com");
            if (random.Next(2) == 0)
            {
                context.Request.PathBase = new PathString(random.Next(2) == 0 ? "/jf" : string.Empty);
            }

            string? forwardedHost = random.Next(2) == 0 ? HostLike(random) : null;
            if (forwardedHost is not null)
            {
                context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
            }

            if (random.Next(2) == 0)
            {
                context.Request.Headers["X-Forwarded-Prefix"] = Candidate(random);
            }

            if (random.Next(2) == 0)
            {
                context.Request.Headers["X-Forwarded-Proto"] = PropertyGen.Hostile(random, 2);
            }

            var at = SsoAddress.For(context.Request, null);
            var where = $"base '{Show(at.BaseUri)}'";
            Assert.True(Uri.TryCreate(at.BaseUri, UriKind.Absolute, out var baseUri), where);
            Assert.True(baseUri!.Scheme is "http" or "https", where);
            Assert.Equal(0, baseUri.UserInfo.Length);
            Assert.Equal(0, baseUri.Query.Length);
            Assert.True(StaysOnOrigin(at.LocalWebClient, baseUri), where);
            Assert.True(
                Uri.TryCreate(at.CallbackUri("p1"), UriKind.Absolute, out var callback)
                && callback.Authority == baseUri.Authority
                && callback.AbsolutePath.EndsWith("/sso/oidc/callback/p1", StringComparison.Ordinal),
                where);
        }
    }

    /// <summary>Whether the part of the address between the scheme and the path is a host and a port.</summary>
    private static bool IsAuthority(string baseUri)
    {
        var rest = baseUri[(baseUri.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var authority = rest.Contains('/') ? rest[..rest.IndexOf('/')] : rest;
        return authority.Length > 0
            && authority.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                or '.' or '-' or '_' or ':' or '[' or ']');
    }

    private static string Show(string? value)
        => value is null
            ? "(null)"
            : string.Concat(value.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString()));

    // ---- Found by the properties above, and fixed ---------------------------------

    /// <summary>
    /// Attack: <c>X-Forwarded-Host: jellyfin.example.com</c> followed by a line feed. The
    /// pattern ends in <c>$</c>, which .NET lets match before a final line feed, so the value
    /// is accepted with the line feed in it and becomes part of the redirect URI.
    /// </summary>
    [Fact]
    public void A_host_with_a_trailing_line_feed_is_not_taken_from_the_header()
    {
        Assert.Equal("fallback:8096", ForwardedPaths.Host("jellyfin.example.com\n", "fallback:8096"));
    }

    /// <summary>
    /// Attack: <c>X-Forwarded-Host: jellyfin.example.com:99999</c>. Five digits are allowed for
    /// a port, and 99999 is not one; a leading dot or an empty label passes the pattern too.
    /// The redirect URI handed to the provider is then not an address at all.
    /// </summary>
    [Theory]
    [InlineData("jellyfin.example.com:99999")]
    [InlineData(".")]
    [InlineData("a..b")]
    [InlineData("-:1")]
    public void A_host_that_is_not_an_address_is_not_taken_from_the_header(string offered)
    {
        Assert.Equal("fallback:8096", ForwardedPaths.Host(offered, "fallback:8096"));
    }

    /// <summary>
    /// Attack, or a slip: a public address whose host System.Uri reads as something that is not a
    /// host: only a character it drops (a right-to-left override, a zero-width space), or a
    /// no-break space that IDN mapping turns into a space. It is accepted and stored as
    /// <c>https:///jf</c> or <c>https://xn-- -b35f</c>, and every redirect URI built from it
    /// is not an address.
    /// </summary>
    [Theory]
    [InlineData("https://\u202E/jf")]
    [InlineData("https://\u200B")]
    [InlineData("https://\u00A0\uC448")]
    [InlineData("https://1\uFEFF")]
    public void A_public_address_whose_host_is_not_a_host_name_is_refused(string typed)
    {
        Assert.Null(SsoAddress.Configured(typed));
    }

    /// <summary>
    /// Attack: a public address that System.Uri reads as a host and cannot turn into one in
    /// punycode. <c>Configured</c> asks for the punycode form and the exception reaches the
    /// caller: the settings endpoint answers 500 instead of "not a public address", and a value
    /// that got into the file some other way fails every request that builds the address.
    /// </summary>
    [Theory]
    [InlineData("https://h\uFE6B")]
    [InlineData("https://\uFF20")]
    [InlineData("https://a\u00ADb\uFE6B")]
    public void A_public_address_with_an_unconvertible_host_is_refused_not_thrown(string typed)
    {
        Assert.Null(Record.Exception(() => SsoAddress.Configured(typed)));
        Assert.Null(Record.Exception(() => Jellyfin.Plugin.Sso.Configuration.ConfigurationRules.Problem(
            new Jellyfin.Plugin.Sso.Configuration.PluginConfiguration { PublicAddress = typed })));
    }
}
