using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Everything in this file is reachable by anyone who can send the server a request, and
/// all of it ends up in a redirect, a link or a page. Rather than guess which strings an
/// attacker would try, this throws a large number of random and deliberately awkward ones
/// at the parsers and asserts the properties that must hold no matter what arrives.
///
/// Not a replacement for a real fuzzer — it is a few thousand cases, not a few million —
/// but it covers the inputs this plugin actually takes from outside.
/// </summary>
public sealed class HostileInputTests
{
    private const int Cases = 2_000;

    private static readonly string[] Seeds =
    [
        "https://example.invalid", "//example.invalid", "\\\\example.invalid",
        "javascript:alert(1)", "data:text/html,<script>", "/jellyfin/../..",
        "/jellyfin\r\nX-Injected: 1", "/%2e%2e/%2e%2e", "/jellyfin?x=1#y",
        "/jellyfin<script>alert(1)</script>", "/jellyfin'\"<>", "\0", " ", "/".PadRight(300, 'a'),
        "jellyfin", "/jellyfin/", "///", "/‮", "/ü", "/jellyfin\u0000",
    ];

    private static IEnumerable<string> Inputs()
    {
        foreach (var seed in Seeds)
        {
            yield return seed;
        }

        var random = RandomNumberGenerator.Create();
        var bytes = new byte[32];
        for (var i = 0; i < Cases; i++)
        {
            random.GetBytes(bytes);
            var builder = new StringBuilder();
            foreach (var b in bytes)
            {
                // A mix of structure and noise: the characters that matter for URLs are
                // over-represented, because uniform random bytes rarely produce them.
                builder.Append((b % 8) switch
                {
                    0 => '/',
                    1 => ':',
                    2 => '\\',
                    3 => '.',
                    4 => '%',
                    5 => (char)('a' + (b % 26)),
                    6 => (char)b,
                    _ => '-',
                });
            }

            yield return builder.ToString();
        }
    }

    [Fact]
    public void A_prefix_is_either_empty_or_a_path()
    {
        foreach (var input in Inputs())
        {
            var prefix = ForwardedPaths.Prefix(input, null);
            if (prefix.Length == 0)
            {
                continue;
            }

            Assert.StartsWith("/", prefix);
            Assert.DoesNotContain("//", prefix);
            Assert.DoesNotContain(":", prefix);
            Assert.DoesNotContain("\\", prefix);
            Assert.DoesNotContain("\n", prefix);
            Assert.DoesNotContain("\r", prefix);
            Assert.DoesNotContain("<", prefix);
            Assert.EndsWith(prefix.TrimEnd('/'), prefix);
        }
    }

    [Fact]
    public void A_host_is_either_the_one_that_was_offered_or_the_fallback()
    {
        foreach (var input in Inputs())
        {
            var host = ForwardedPaths.Host(input, "fallback:8096");
            if (host == "fallback:8096")
            {
                continue;
            }

            Assert.Equal(input, host);
            Assert.DoesNotContain("/", host);
            Assert.DoesNotContain("@", host);
            Assert.DoesNotContain("\\", host);
            Assert.DoesNotContain("\n", host);
        }
    }

    [Fact]
    public void A_scheme_is_only_ever_http_or_https()
    {
        foreach (var input in Inputs())
        {
            var scheme = ForwardedPaths.Scheme(input, "https");
            Assert.True(scheme is "http" or "https", $"scheme was '{scheme}'");
        }
    }

    [Fact]
    public void A_provider_name_never_breaks_out_of_the_login_button()
    {
        foreach (var input in Inputs())
        {
            var markup = LoginButtonMarkup.Build(
                [new ProviderConfig { Id = input, Name = input, Enabled = true }],
                basePath: string.Empty);

            // The name is text, the id is part of a URL: neither may end the attribute
            // or the element it sits in. The label is what follows the icon element.
            var label = markup[(markup.IndexOf("/>", StringComparison.Ordinal) + 2)..];
            Assert.DoesNotContain("<", label[..label.IndexOf("</a>", StringComparison.Ordinal)]);
            Assert.Equal(1, Occurrences(markup, "</a>"));
            Assert.Equal(1, Occurrences(markup, "<a "));
        }
    }

    [Fact]
    public void An_icon_slug_is_always_one_of_the_known_marks()
    {
        var known = new HashSet<string>(
            ["authentik", "keycloak", "authelia", "google", "okta", "auth0", "generic"],
            StringComparer.Ordinal);

        foreach (var input in Inputs())
        {
            var slug = ProviderIcons.SlugFor(input, input);
            Assert.True(known.Contains(slug), $"slug was '{slug}'");
            Assert.StartsWith("<svg", ProviderIcons.Svg(input));
        }
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
