using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Logging;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Values a person or a provider chose, on their way into a page, a stylesheet, a script and
/// a log line. The property in each case is that the value stays in the place it was written
/// into: an attribute stays one attribute, a string literal one string, a CSS rule one rule, a
/// log entry one line. Judged structurally, from the outside: the output for a hostile value is
/// matched against the shape of the output for a harmless one.
/// </summary>
public sealed class MarkupPropertyTests
{
    private const int Seed = 20_261_129;

    private static readonly string[] MarkupFragments =
    [
        "</script>", "<script>alert(1)</script>", "<!--", "-->", "<img src=x onerror=alert(1)>",
        "\"onmouseover=\"alert(1)", "'+alert(1)+'", "\"><svg/onload=alert(1)>", "${x}", "{{x}}",
        "javascript:alert(1)", "\u2028", "\u2029", "&amp;", "&#x3c;", "</style>", "}", "{", "*/", "/*",
        "\\", "\\22 ", "\r\n", "]]>",
    ];

    /// <summary>Hostile text that is valid Unicode: lone surrogates are refused before they get here.</summary>
    private static string Markupish(Random random)
    {
        var text = string.Concat(Enumerable.Range(0, random.Next(1, 5)).Select(_ =>
            random.Next(2) == 0 ? MarkupFragments[random.Next(MarkupFragments.Length)] : PropertyGen.Hostile(random, 3)));
        return string.Concat(text.EnumerateRunes().Select(r => r.ToString()));
    }

    private static string SafeId(Random random)
        => "p" + random.Next(1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- The login button -----------------------------------------------------------------

    private const string Chunk = "(?:[^\"<>&]|&#?[A-Za-z0-9]+;)*";

    private static readonly Regex Form = new(
        "<form class=\"sso-login-form\" method=\"get\" action=\"(?<action>" + Chunk + ")\">"
        + "<button type=\"submit\" data-provider=\"(?<id>" + Chunk + ")\" class=\"raised block emby-button sso-login-button\">"
        + "<img src=\"(?<icon>" + Chunk + ")\" alt=\"\" height=\"22\" class=\"sso-login-mark\"/>"
        + "<span class=\"sso-login-label\">(?<label>" + Chunk + ")</span></button></form>",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Attack: a provider's name or id that ends the attribute or the element it is written
    /// into, and starts markup of its own in Jellyfin's login page, where the disclaimer is
    /// rendered as HTML for everybody who opens it.
    /// </summary>
    [Fact]
    public void A_login_button_is_one_form_whatever_the_provider_is_called()
    {
        var random = new Random(Seed);
        for (var i = 0; i < 1_000; i++)
        {
            var providers = Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => new ProviderConfig { Id = Markupish(random), Name = Markupish(random), Enabled = true })
                .ToList();
            var basePath = ForwardedPaths.Prefix(random.Next(2) == 0 ? "/jf" : null, null);

            var markup = LoginButtonMarkup.Build(providers, basePath);

            Assert.StartsWith(LoginButtonMarkup.Start, markup, StringComparison.Ordinal);
            Assert.EndsWith(LoginButtonMarkup.End, markup, StringComparison.Ordinal);
            var inner = markup[LoginButtonMarkup.Start.Length..^LoginButtonMarkup.End.Length];

            var matches = Form.Matches(inner);
            Assert.True(
                matches.Count == providers.Count && matches.Sum(m => m.Length) == inner.Length,
                $"the markup for {providers.Count} providers is not exactly {providers.Count} forms: {inner}");

            for (var n = 0; n < providers.Count; n++)
            {
                var m = matches[n];
                Assert.Equal(providers[n].Id, WebUtility.HtmlDecode(m.Groups["id"].Value));
                Assert.Equal(
                    string.IsNullOrWhiteSpace(providers[n].Name) ? "Login with SSO" : "Login with " + providers[n].Name,
                    WebUtility.HtmlDecode(m.Groups["label"].Value));
                Assert.Equal(
                    basePath + "/sso/oidc/start/" + providers[n].Id,
                    WebUtility.UrlDecode(m.Groups["action"].Value));
            }
        }
    }

    // ---- The stylesheet -------------------------------------------------------------------

    private static string Structure(string css)
        => string.Join(
            ',',
            new[] { '{', '}', '"', '\'', '<', '>', ';', '\n', '\r' }.Select(c => css.Count(x => x == c)))
            + "/" + Regex.Matches(css, @"/\*").Count + "/" + Regex.Matches(css, @"\*/").Count;

    /// <summary>
    /// Attack: a provider id, name, authority or button colour that closes the rule it is written
    /// into and opens another in Jellyfin's global custom CSS, which every page of the server
    /// loads. The structure of the rules for a hostile provider has to be the structure of the
    /// rules for an ordinary one.
    /// </summary>
    [Fact]
    public void A_provider_adds_the_same_rules_to_the_stylesheet_whatever_it_is_called()
    {
        var random = new Random(Seed + 1);
        for (var i = 0; i < 300; i++)
        {
            var branded = random.Next(2) == 0;
            var logos = random.Next(2) == 0;
            var hostile = new ProviderConfig
            {
                Id = Markupish(random),
                Name = Markupish(random),
                Authority = Markupish(random),
                ButtonColour = Markupish(random),
                BrandedLoginButton = branded,
                Enabled = true,
            };
            var ordinary = new ProviderConfig
            {
                Id = "p1",
                Name = "Provider",
                Authority = "https://idp.example.com",
                ButtonColour = string.Empty,
                BrandedLoginButton = branded,
                Enabled = true,
            };

            var css = LoginButtonMarkup.Css(true, [hostile], logos);
            var expected = LoginButtonMarkup.Css(true, [ordinary], logos);

            Assert.True(
                Structure(css) == Structure(expected),
                $"rules for id '{hostile.Id}', colour '{hostile.ButtonColour}' differ in structure: {Structure(css)} against {Structure(expected)}");
        }
    }

    /// <summary>
    /// Attack: a button colour that is more than a colour. Whatever is accepted is a hex colour
    /// and nothing else, because it is written into a stylesheet unquoted.
    /// </summary>
    [Fact]
    public void A_colour_that_is_accepted_is_a_hex_colour_and_nothing_else()
    {
        var random = new Random(Seed + 2);
        var accepted = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var typed = random.Next(3) switch
            {
                0 => Markupish(random),
                1 => "#" + Markupish(random),
                _ => "#" + string.Concat(Enumerable.Range(0, random.Next(2, 9)).Select(_ => "0123456789abcdefABCDEFgG \t\n;"[random.Next(28)])),
            };

            var colour = ProviderIcons.SanitiseColour(typed);
            if (colour is null)
            {
                continue;
            }

            accepted++;
            Assert.Matches("^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$", colour);
            Assert.Matches("^#[0-9a-f]{6}$", ProviderIcons.HoverShadeOf(colour));
            Assert.Contains(ProviderIcons.ReadableOn(colour), new[] { "#101418", "#ffffff" });
        }

        Assert.True(accepted > 100, $"only {accepted} colours were accepted");
    }

    // ---- The served pages -----------------------------------------------------------------

    private static string Marks(string html)
        => string.Join(',', new[] { '<', '>', '"', '\'' }.Select(c => html.Count(x => x == c)))
            // A backtick is text in a page and a string delimiter in a script.
            + "/" + Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline)
                .Sum(m => m.Groups[1].Value.Count(c => c == '`'));

    /// <summary>A line separator inside a script ends a line of JavaScript; in text it is only text.</summary>
    private static bool ScriptHasLineSeparator(string html)
        => Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline)
            .Any(m => m.Groups[1].Value.Any(c => c is '\u2028' or '\u2029'));

    private static string Inside(string html, string open, string close)
    {
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        return html[start..html.IndexOf(close, start, StringComparison.Ordinal)];
    }

    /// <summary>
    /// Attack: a name, a message, a code or a token that ends the text or the attribute a page
    /// prints it in. Every page is rendered with a hostile value and with a harmless one, and
    /// the two must have the same number of tag brackets, quotes and script-ending characters:
    /// the value added text and nothing else.
    /// </summary>
    [Fact]
    public void A_value_printed_on_a_page_adds_text_and_no_markup()
    {
        var random = new Random(Seed + 3);
        for (var i = 0; i < 150; i++)
        {
            var v = Markupish(random);
            var basePath = ForwardedPaths.Prefix(random.Next(2) == 0 ? "/jf" : null, null);
            var good = new ProviderConfig { Id = "p1", Name = "Authentik", Authority = "https://idp.example.com", Enabled = true };
            var bad = new ProviderConfig { Id = SafeId(random), Name = v, Authority = v, Enabled = true };

            var pairs = new (string Hostile, string Harmless)[]
            {
                (SsoPages.ProviderPicker(basePath, [bad], "?code=" + Uri.EscapeDataString(v), quickConnect: i % 2 == 0),
                    SsoPages.ProviderPicker(basePath, [good], "?code=x", quickConnect: i % 2 == 0)),
                (SsoPages.LinkResult(basePath, v, success: i % 2 == 0), SsoPages.LinkResult(basePath, "x", success: i % 2 == 0)),
                (SsoPages.ApproveQuickConnect(basePath, v, v, v, refused: v), SsoPages.ApproveQuickConnect(basePath, "x", "x", "x", refused: "x")),
                (SsoPages.ApproveQuickConnect(basePath, v, string.Empty, v), SsoPages.ApproveQuickConnect(basePath, "x", string.Empty, "x")),
                (SsoPages.SelfServiceLink(basePath, [bad]), SsoPages.SelfServiceLink(basePath, [good])),
                (SsoPages.CompleteWebLogin(basePath, v, v), SsoPages.CompleteWebLogin(basePath, "x", "x")),
                (SsoPages.LoginButtonScript(basePath, [bad]), SsoPages.LoginButtonScript(basePath, [good])),
            };

            foreach (var (hostile, harmless) in pairs)
            {
                Assert.True(
                    Marks(hostile) == Marks(harmless),
                    $"a hostile value changed the markup of a page ({Marks(hostile)} against {Marks(harmless)}): '{v}'");
                Assert.False(ScriptHasLineSeparator(hostile), $"a line separator reached a script: '{v}'");
            }
        }
    }

    /// <summary>
    /// Attack: a value written into a script as a literal that ends the literal. Whatever the
    /// value, the literal is one JSON string that reads back as the value, with nothing in it that
    /// ends a script element or a line of JavaScript.
    /// </summary>
    [Fact]
    public void A_value_written_into_a_script_is_one_string_literal_that_reads_back_unchanged()
    {
        var random = new Random(Seed + 4);
        for (var i = 0; i < 5_000; i++)
        {
            var v = Markupish(random);
            var literal = SsoPages.JsonEscape(v);

            Assert.Equal(v, JsonSerializer.Deserialize<string>(literal));
            Assert.DoesNotContain("<", literal, StringComparison.Ordinal);
            Assert.DoesNotContain(">", literal, StringComparison.Ordinal);
            Assert.DoesNotContain("'", literal, StringComparison.Ordinal);
            Assert.DoesNotContain("&", literal, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2028", literal, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2029", literal, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", literal, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", literal, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The login button script carries every provider as data. Whatever the names and ids are, the
    /// data reads back as one entry per provider, with the label as written and an address that
    /// reads back to the id.
    /// </summary>
    [Fact]
    public void The_login_button_script_carries_each_provider_exactly()
    {
        var random = new Random(Seed + 5);
        for (var i = 0; i < 500; i++)
        {
            var providers = Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => new ProviderConfig { Id = Markupish(random), Name = Markupish(random), Authority = Markupish(random) })
                .ToList();
            var script = SsoPages.LoginButtonScript("/jf", providers);

            var json = Inside(script, "var buttons = ", ";\n");
            using var data = JsonDocument.Parse(json);
            var entries = data.RootElement.EnumerateArray().ToList();

            Assert.Equal(providers.Count, entries.Count);
            for (var n = 0; n < providers.Count; n++)
            {
                Assert.Equal(
                    string.IsNullOrWhiteSpace(providers[n].Name) ? "SSO" : providers[n].Name,
                    entries[n].GetProperty("name").GetString());
                Assert.Equal(
                    "/jf/sso/oidc/start/" + providers[n].Id,
                    Uri.UnescapeDataString(entries[n].GetProperty("href").GetString()!));
            }
        }
    }

    // ---- The log --------------------------------------------------------------------------

    /// <summary>
    /// Attack: a display name or a group with a line break in it, written to the log so that it
    /// reads as a second entry the server wrote. Whatever arrives comes out as one line, of
    /// bounded length, with no character any log viewer or parser treats as the end of one.
    /// </summary>
    [Fact]
    public void A_value_in_the_log_is_one_bounded_line()
    {
        var random = new Random(Seed + 6);
        for (var i = 0; i < 8_000; i++)
        {
            var text = random.Next(4) == 0
                ? new string('a', random.Next(150, 400)) + PropertyGen.Hostile(random)
                : PropertyGen.Hostile(random, 12) + PropertyGen.Text(random, dirty: true);

            var line = Loggable.OneLine(text);

            Assert.True(line.Length <= Loggable.MaxLoggedLength + 1, "a logged value was not cut");
            foreach (var c in line)
            {
                Assert.True(
                    !char.IsControl(c) && c is not ('\u2028' or '\u2029' or '\u0085') && (!char.IsSeparator(c) || c == ' '),
                    $"U+{(int)c:X4} reached the log from '{text}'");
            }
        }

        var many = Loggable.OneLine(Enumerable.Range(0, 50).Select(i => "g\n" + i));
        Assert.DoesNotContain("\n", many, StringComparison.Ordinal);
    }

    // ---- The endpoint host list -----------------------------------------------------------

    /// <summary>
    /// Attack: an "endpoint host" entry that says more than a host: a path, userinfo, a wildcard.
    /// The list is what lets a provider's discovery document name hosts other than its own, so
    /// whatever is accepted has to be exactly a host name and a port.
    /// </summary>
    [Fact]
    public void An_endpoint_host_that_is_accepted_is_a_host_and_nothing_more()
    {
        var random = new Random(Seed + 7);
        var accepted = 0;
        for (var i = 0; i < 8_000; i++)
        {
            var typed = random.Next(3) == 0
                ? PropertyGen.Hostile(random)
                : string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => "abcxyz019.-:_"[random.Next(13)]))
                    + (random.Next(3) == 0 ? PropertyGen.Hostile(random, 2) : string.Empty);

            var host = OidcClientFactory.EndpointHost(typed);
            if (host is null)
            {
                continue;
            }

            accepted++;
            var trimmed = typed.Trim();
            Assert.True(
                // Outside ASCII anything goes here: the lookalikes that should not are the skipped
                // test below.
                host.All(c => c > '\u007F'
                    ? true
                    : c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or ':' or '[' or ']' or '_'),
                $"'{typed}' was accepted as '{host}'");
            Assert.True(Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out var uri), $"'{typed}' was accepted as '{host}'");
            Assert.Equal("/", uri!.AbsolutePath);
            Assert.Equal(0, uri.UserInfo.Length);
            Assert.True(
                string.Equals(host, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(host + ":" + uri.Port, trimmed, StringComparison.OrdinalIgnoreCase),
                $"'{typed}' was accepted as '{host}'");
        }

        Assert.True(accepted > 100, $"only {accepted} entries were accepted");
    }

    /// <summary>
    /// Attack, or a typo: an endpoint host with a no-break space or a fullwidth slash in it. The
    /// ASCII spellings of both are refused; these pass because System.Uri keeps them in the host,
    /// and the entry then names a host no discovery document can carry.
    /// </summary>
    [Theory]
    [InlineData("a\u00A0b.example")]
    [InlineData("a\uFF3Cb.example")]
    [InlineData("a\uFF0Fb.example")]
    public void An_endpoint_host_with_a_lookalike_separator_is_refused(string typed)
    {
        Assert.Null(OidcClientFactory.EndpointHost(typed));
    }
}
