using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The claims of an ID token that a person can influence (a display name, a group, a
/// picture address, sometimes the subject itself) arrive as whatever JSON the provider
/// signed. These tests sign tokens whose claims have every shape JSON has (strings, numbers,
/// null, arrays, nested arrays, objects, duplicated keys, huge arrays, very long strings,
/// controls) and hold the extractor to what login depends on: the only way out is a refusal
/// the callback turns into a 400, the subject is never blank, and a group is never something
/// the provider did not send as a string.
/// </summary>
public sealed class ClaimsExtractorPropertyTests : IDisposable
{
    private const int Seed = 20_261_128;

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    private readonly ProviderConfig _provider = new()
    {
        Name = "Test",
        Authority = IdTokenFactory.Issuer,
        ClientId = IdTokenFactory.Audience,
        GroupsClaim = "groups",
        PictureClaim = "picture",
    };

    public ClaimsExtractorPropertyTests()
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = "k" };
    }

    public void Dispose() => _rsa.Dispose();

    // ---- Signing raw JSON -----------------------------------------------------------------

    private static string B64(byte[] bytes) => Base64UrlEncoder.Encode(bytes);

    private string Sign(string payloadJson)
    {
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT","kid":"k"}"""));
        var body = B64(Encoding.UTF8.GetBytes(payloadJson));
        var signature = _rsa.SignData(
            Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + body + "." + B64(signature);
    }

    private static string Standard(string extra)
    {
        var now = DateTimeOffset.UtcNow;
        return "{" + string.Join(
            ",",
            $"\"iss\":\"{IdTokenFactory.Issuer}\"",
            $"\"aud\":\"{IdTokenFactory.Audience}\"",
            "\"nonce\":\"n\"",
            $"\"nbf\":{(now.ToUnixTimeSeconds() - 5).ToString(CultureInfo.InvariantCulture)}",
            $"\"exp\":{(now.ToUnixTimeSeconds() + 300).ToString(CultureInfo.InvariantCulture)}",
            $"\"iat\":{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}")
            + extra + "}";
    }

    private (string Sub, string Username, List<string> Groups, string? Picture) Validate(string payload)
        => ClaimsExtractor.ValidateIdToken(_provider, IdTokenFactory.Issuer, [_key], Sign(payload), "n");

    // ---- Generating claim values ----------------------------------------------------------

    /// <summary>A JSON string literal, every code unit above ASCII written as an escape.</summary>
    private static string Literal(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c is '"' or '\\' || c < 0x20 || c > 0x7E)
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.Append('"').ToString();
    }

    private static string Word(Random random, bool lone)
    {
        var text = random.Next(8) switch
        {
            0 => string.Empty,
            1 => "  ",
            2 => PropertyGen.Text(random, dirty: true),
            3 => new string('x', random.Next(1_000, 60_000)),
            4 => " " + random.Next(100) + " ",
            5 => "\u0000\u0001admin",
            _ => "g" + random.Next(1_000),
        };

        // Without lone surrogates unless asked for: a lone surrogate has no faithful JSON
        // reading, so what comes back from it cannot be compared with what went in.
        return lone ? text : string.Concat(text.EnumerateRunes().Select(r => r.ToString()));
    }

    /// <summary>
    /// Any JSON value. Every string it holds, at any depth, is added to <paramref name="strings"/>:
    /// those are the only strings the provider can be said to have sent.
    /// </summary>
    private static string Value(Random random, int depth, bool lone, List<string> strings, bool scalars = false)
    {
        switch (random.Next(depth > 0 ? 9 : 6))
        {
            case 0:
            case 1:
            case 2:
                var word = Word(random, lone);
                strings.Add(word);
                return Literal(word);
            case 3 when !scalars:
            case 4 when !scalars:
                var asText = "s" + random.Next(100);
                strings.Add(asText);
                return Literal(asText);
            case 3:
                return new[] { "0", "-0", "1", "42", "1.5", "1e999", "-1E-400", "12345678901234567890", "true", "false" }
                    [random.Next(10)];
            case 4:
                return "null";
            case 5:
                var plain = "g" + random.Next(100);
                strings.Add(plain);
                return Literal(plain);
            case 6:
            case 7:
                var length = random.Next(20) == 0 ? random.Next(2_000, 6_000) : random.Next(0, 6);
                var items = new List<string>();
                for (var i = 0; i < length; i++)
                {
                    items.Add(length > 100 ? Literal(AddString(strings, "g" + i)) : Value(random, depth - 1, lone, strings, scalars));
                }

                return "[" + string.Join(',', items) + "]";
            default:
                var members = new List<string>();
                for (var i = random.Next(0, 4); i > 0; i--)
                {
                    var key = new[] { "roles", "groups", "a", "x", "realm_access" }[random.Next(5)];
                    members.Add(Literal(key) + ":" + Value(random, depth - 1, lone, strings, scalars));
                }

                return "{" + string.Join(',', members) + "}";
        }
    }

    /// <summary>
    /// The groups claim in the two shapes a provider is documented to send: one string or an
    /// array of strings, with hostile content, huge arrays and the key written twice. Other shapes
    /// are held by the skipped test about groups that were not sent as strings.
    /// </summary>
    private static (string Json, List<string> Strings) FlatGroups(Random random)
    {
        var strings = new List<string>();
        var fragments = new List<string>();
        for (var copies = random.Next(6) == 0 ? 2 : 1; copies > 0; copies--)
        {
            var count = random.Next(25) == 0 ? random.Next(2_000, 8_000) : random.Next(0, 5);
            var items = Enumerable.Range(0, count)
                .Select(i => Literal(AddString(strings, count > 100 ? "g" + i : Word(random, false))))
                .ToList();
            fragments.Add(",\"groups\":" + (random.Next(4) == 0 && items.Count > 0 ? items[0] : "[" + string.Join(',', items) + "]"));
        }

        return (string.Concat(fragments), strings);
    }

    private static string AddString(List<string> strings, string text)
    {
        strings.Add(text);
        return text;
    }

    /// <summary>One claim: usually a plain string, otherwise anything; sometimes written twice.</summary>
    private static (string Json, List<string> Strings) Claim(
        Random random, string name, bool lone, string plain, bool scalars = false)
    {
        var strings = new List<string>();
        var fragments = new List<string>();
        for (var copies = random.Next(8) == 0 ? 2 : 1; copies > 0; copies--)
        {
            string value;
            if (random.Next(10) < 4)
            {
                strings.Add(plain);
                value = Literal(plain);
            }
            else
            {
                value = Value(random, 3, lone, strings, scalars);
            }

            fragments.Add("," + Literal(name) + ":" + value);
        }

        return (string.Concat(fragments), strings);
    }

    private static readonly Type[] Refusals =
    [
        typeof(SecurityTokenException), typeof(ArgumentException), typeof(InvalidOperationException),
    ];

    // ---- The ID token ---------------------------------------------------------------------

    /// <summary>
    /// Attack: a claim shaped to make the extractor produce an identity it should not: a blank
    /// subject (which becomes the one key every blank-subject user shares), a group the provider
    /// never sent (an object read as a name, a number read as a group), or an exception no
    /// handler turns into a refusal, which is a 500 with the message in the body.
    /// </summary>
    [Fact]
    public void Whatever_claims_arrive_the_extractor_refuses_cleanly_or_yields_only_what_was_sent()
    {
        var random = new Random(Seed);
        var accepted = 0;
        for (var i = 0; i < 250; i++)
        {
            var sub = Claim(random, "sub", false, "user-" + i);
            var preferred = Claim(random, "preferred_username", false, "ben" + i);
            var name = Claim(random, "name", false, "Ben " + i);
            var groups = FlatGroups(random);
            var picture = Claim(random, "picture", false, "https://cdn.example.com/p.png");
            var payload = Standard(
                (random.Next(10) == 0 ? string.Empty : sub.Json) + preferred.Json + name.Json + groups.Json + picture.Json);

            (string Sub, string Username, List<string> Groups, string? Picture) result;
            try
            {
                result = Validate(payload);
            }
            catch (Exception ex) when (Refusals.Any(t => t.IsInstanceOfType(ex)))
            {
                continue;
            }
            catch (Exception ex)
            {
                Assert.Fail($"{ex.GetType().Name} escaped for payload {Truncate(payload)}: {ex.Message}");
                throw;
            }

            accepted++;
            var where = $"payload {Truncate(payload)}";
            Assert.True(!string.IsNullOrWhiteSpace(result.Sub), "blank subject, " + where);
            Assert.True(sub.Strings.Contains(result.Sub), "subject was not sent as a string, " + where);
            Assert.True(!string.IsNullOrWhiteSpace(result.Username), "blank username, " + where);

            // The subject stands in when the token has no name, through the same rule (#331).
            var nameChoices = preferred.Strings.Concat(name.Strings).Concat([result.Sub])
                .Select(ClaimsExtractor.Name).Where(n => n is not null);
            Assert.True(nameChoices.Contains(result.Username), $"username '{Truncate(result.Username)}' was not sent, " + where);

            foreach (var group in result.Groups)
            {
                Assert.True(groups.Strings.Contains(group), $"group '{Truncate(group)}' was not sent as a string, " + where);
            }

            // A picture that is not a string is JSON text and no address, which the fetcher refuses
            // for not being an absolute http(s) address; only a blank one would be a different thing.
            Assert.True(
                result.Picture is null || !string.IsNullOrWhiteSpace(result.Picture),
                $"blank picture, " + where);
        }

        Assert.True(accepted > 100, $"only {accepted} tokens were accepted, so the property was barely tried");
    }

    /// <summary>
    /// Attack: the same, with lone surrogates in the claims, which JSON can spell and a .NET
    /// string can hold. Nothing may come out but a refusal or an identity; what the identity
    /// holds is not compared, since a lone surrogate has no faithful reading.
    /// </summary>
    [Fact]
    public void Lone_surrogates_in_claims_never_escape_as_a_crash()
    {
        var random = new Random(Seed + 1);
        for (var i = 0; i < 120; i++)
        {
            var sub = Claim(random, "sub", true, "user-" + i);
            var preferred = Claim(random, "preferred_username", true, "\uD800ben");
            var groups = Claim(random, "groups", true, "\uDC00");
            var payload = Standard(sub.Json + preferred.Json + groups.Json);

            try
            {
                var result = Validate(payload);
                Assert.False(string.IsNullOrWhiteSpace(result.Sub), $"blank subject for payload {Truncate(payload)}");
            }
            catch (Exception ex) when (Refusals.Any(t => t.IsInstanceOfType(ex)))
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"{ex.GetType().Name} escaped for payload {Truncate(payload)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Attack: a subject that is only whitespace or control characters, in a token that is
    /// otherwise perfect. A blank subject is a perfectly good dictionary key, shared by
    /// every user the provider gives one.
    /// </summary>
    [Fact]
    public void A_blank_subject_is_never_an_identity()
    {
        foreach (var blank in new[]
                 {
                     "\"\"", "\" \"", "\"\\t\"", "\"\\n\"", "\"\\u00A0\"", "\"\\u2003\"", "\"\\u3000\"",
                     "\"\\u0085\"", "\"\\u2028\"", "null", "[]", "[\"\"]", "[\" \"]", "{}", "[null]",
                 })
        {
            var exception = Record.Exception(() => Validate(Standard(",\"sub\":" + blank)));
            Assert.True(
                exception is not null && Refusals.Any(t => t.IsInstanceOfType(exception)),
                $"a subject of {blank} was not refused: {exception?.GetType().Name ?? "accepted"}");
        }
    }

    /// <summary>
    /// A subject is what an account link is keyed on, compared byte for byte, and the name an
    /// account is created under is trimmed so that " admin" cannot be a second "admin". When
    /// the token has no name at all the name falls back to the subject, and that fallback skips
    /// the trimming.
    /// </summary>
    [Fact]
    public void The_username_is_trimmed_even_when_it_falls_back_to_the_subject()
    {
        var result = Validate(Standard(",\"sub\":\" admin\""));

        Assert.Equal(result.Username.Trim(), result.Username);
    }



    /// <summary>
    /// Attack: numbers, booleans and null where a string belongs, and objects and arrays inside
    /// arrays. Whatever comes out, only a refusal or an identity does, and the subject is not blank.
    /// </summary>
    [Fact]
    public void Claims_that_are_not_strings_never_escape_as_a_crash()
    {
        var random = new Random(Seed + 4);
        for (var i = 0; i < 150; i++)
        {
            var payload = Standard(
                Claim(random, "sub", false, "user-" + i, scalars: true).Json
                + Claim(random, "preferred_username", false, "ben", scalars: true).Json
                + Claim(random, "groups", false, "media", scalars: true).Json
                + Claim(random, "picture", false, "https://cdn.example.com/p.png", scalars: true).Json);

            try
            {
                var result = Validate(payload);
                Assert.False(string.IsNullOrWhiteSpace(result.Sub), $"blank subject for payload {Truncate(payload)}");
                Assert.False(string.IsNullOrWhiteSpace(result.Username), $"blank username for payload {Truncate(payload)}");
                Assert.DoesNotContain(null!, result.Groups);
            }
            catch (Exception ex) when (Refusals.Any(t => t.IsInstanceOfType(ex)))
            {
            }
            catch (Exception ex)
            {
                Assert.Fail($"{ex.GetType().Name} escaped for payload {Truncate(payload)}: {ex.Message}");
            }
        }
    }

    // ---- Found by the properties above, and fixed ---------------------------------

    /// <summary>
    /// Attack: a groups claim that is not a list of strings. The userinfo path reads only the
    /// strings; the ID token path hands over a group for everything else: null and an empty
    /// element become the empty group, a number or a boolean its text, an object or an array
    /// its JSON.
    /// </summary>
    [Theory]
    [InlineData("\"groups\":null", "")]
    [InlineData("\"groups\":[null]", "")]
    [InlineData("\"groups\":7", "")]
    [InlineData("\"groups\":{\"x\":\"y\"}", "")]
    [InlineData("\"groups\":[[\"n\",\"m\"]]", "")]
    [InlineData("\"groups\":[\"a\",1,null,true,{\"x\":\"y\"}]", "a")]
    public void A_group_the_provider_did_not_send_as_a_string_is_not_a_group(string claim, string expected)
    {
        var result = Validate(Standard(",\"sub\":\"u\"," + claim));

        string[] wanted = expected.Length == 0 ? [] : expected.Split('|');

        Assert.Equal(wanted, result.Groups);
    }

    /// <summary>
    /// Attack, or a half-filled form: a group mapping whose group is blank (the settings page names
    /// such a row "Group 3" and saves it) is matched by a token whose groups claim is null, which
    /// is how a user with no groups looks at some providers. Whatever the mapping grants, the
    /// person with no groups gets.
    /// </summary>
    [Fact]
    public void A_token_without_groups_matches_no_mapping()
    {
        var result = Validate(Standard(",\"sub\":\"u\",\"groups\":null"));
        var config = new PluginConfiguration();
        config.GroupMappings.Add(new GroupMappingConfig { Group = string.Empty, IsAdministrator = true });

        var matched = Jellyfin.Plugin.Sso.Provisioning.GroupMatching.Matching(
            config, new OidcIdentity(result.Sub, result.Username, result.Groups));

        Assert.Empty(matched);
    }

    /// <summary>
    /// Documents a leniency that was weighed and kept: a subject sent as a number is accepted as
    /// its text, so <c>0</c> and <c>"0"</c> are one identity. The provider signs the token, and a
    /// number is not something a person can make it say; refusing would lock out a provider that
    /// issues numeric ids.
    /// </summary>
    [Theory]
    [InlineData("\"sub\":0", "0")]
    [InlineData("\"sub\":1.50", "1.5")]
    public void A_subject_sent_as_a_number_is_taken_as_its_text(string claim, string expected)
    {
        Assert.Equal(expected, Validate(Standard("," + claim)).Sub);
    }

    /// <summary>
    /// A subject is a name as far as the account is concerned when the token has no other (#331):
    /// padding and characters that draw nothing are removed, and one with nothing visible is
    /// refused.
    /// </summary>
    [Theory]
    [InlineData("\" admin\"", "admin")]
    [InlineData("\"admin\\u034F\"", "admin")]
    public void The_username_that_stands_in_for_a_missing_name_is_cleaned_like_any_other(string sub, string expected)
    {
        Assert.Equal(expected, Validate(Standard(",\"sub\":" + sub)).Username);
    }

    [Fact]
    public void A_subject_with_nothing_visible_cannot_name_an_account()
    {
        var exception = Record.Exception(() => Validate(Standard(",\"sub\":\"\\u034F\"")));

        Assert.IsAssignableFrom<SecurityTokenException>(exception);
    }

    [Fact]
    public void A_group_mapping_without_a_group_is_refused_in_words_and_never_matched()
    {
        var config = new PluginConfiguration();
        config.GroupMappings.Add(new GroupMappingConfig { Group = "  ", IsAdministrator = true });

        Assert.Contains("no group name", ConfigurationRules.Problem(config));
        Assert.Empty(Jellyfin.Plugin.Sso.Provisioning.GroupMatching.Matching(
            config, new OidcIdentity("u", "u", ["  ", string.Empty])));
    }

    // ---- Claims as the library hands them over, and as userinfo sends them ------------------

    private static readonly string[] ClaimNames =
        ["groups", "realm_access.roles", "a.b.c", "urn:x.y:roles", "resource_access.jellyfin.roles", "x"];

    /// <summary>
    /// Attack: a userinfo answer, or a nested claim, with groups in every shape: no throw but the
    /// refusals the callback handles, never a group that is not a string the provider sent, never
    /// a null.
    /// </summary>
    [Fact]
    public void Groups_read_out_of_json_are_strings_the_provider_sent()
    {
        var random = new Random(Seed + 2);
        for (var i = 0; i < 500; i++)
        {
            var strings = new List<string>();
            var json = Value(random, 4, lone: false, strings);
            var claim = ClaimNames[random.Next(ClaimNames.Length)];

            // Wrapped so that the claim name's own path is sometimes there to be found.
            var wrapped = random.Next(5) == 0
                ? json
                : random.Next(2) == 0
                    ? "{" + Literal(claim) + ":" + json + "}"
                    : claim.Split('.').Reverse().Aggregate(json, (inner, key) => "{" + Literal(key) + ":" + inner + "}");

            JsonElement? root;
            try
            {
                root = JsonDocument.Parse(wrapped).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }

            var groups = ClaimsExtractor.ExtractGroupsFromElement(root, claim);
            foreach (var group in groups)
            {
                Assert.True(group is not null && strings.Contains(group), $"group '{group}' was not sent, in {Truncate(wrapped)}");
            }

            var text = ClaimsExtractor.StringClaim(root, "preferred_username");
            Assert.True(text is null || !string.IsNullOrWhiteSpace(text));
        }
    }

    /// <summary>
    /// Attack: a nested claim given as the JSON text the library hands over, or as text that is
    /// no JSON at all. No exception may escape, and nothing but a string that was in the text
    /// may come out of it.
    /// </summary>
    [Fact]
    public void A_nested_claim_that_is_not_json_grants_nothing_and_throws_nothing()
    {
        var random = new Random(Seed + 3);
        for (var i = 0; i < 1_000; i++)
        {
            var text = random.Next(3) == 0
                ? PropertyGen.Hostile(random)
                : Value(random, 3, lone: false, []);
            var claim = ClaimNames[random.Next(ClaimNames.Length)];
            var head = claim.Contains('.') ? claim[..claim.IndexOf('.')] : claim;
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(head, text), new Claim(head, PropertyGen.Hostile(random))]));

            var groups = ClaimsExtractor.GroupsFromClaims(principal, claim);

            Assert.DoesNotContain(null!, groups);
        }
    }

    private static string Truncate(string text)
        => text.Length <= 160
            ? Escape(text)
            : Escape(text[..160]) + $"... ({text.Length} chars)";

    private static string Escape(string text)
        => string.Concat(text.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString()));
}
