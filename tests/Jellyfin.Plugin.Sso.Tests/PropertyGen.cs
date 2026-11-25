using System.Text;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Input generators for the property tests. Every test starts from a fixed seed, so a run
/// is the same run on every machine: a failure names the case it died on, and the same
/// case is generated again the next time. Change the seed in the test to explore further.
/// </summary>
internal static class PropertyGen
{
    /// <summary>
    /// The pieces of a hostile string: the characters and sequences that change what a URL,
    /// a path or a header means, plus lookalikes that only become dangerous after a
    /// normalisation somewhere downstream.
    /// </summary>
    private static readonly string[] Fragments =
    [
        "/", "//", "///", "\\", "/\\", "\\/", "\\\\", "%2f", "%2F", "%5c", "%5C", "%2e%2e",
        "%2e", "..", ".", "..;/", "/.;/", "@", ":", "::", "?", "#", ";", "=", "&", "%", "%00",
        "%0d%0a", "%0A", "%u002f", "\t", "\r", "\n", "\r\n", "\0", " ", "\u000B", "\u000C",
        "\u0085", "\u00A0", "\u1680", "\u2028", "\u2029", "\u202E", "\u200B", "\uFEFF",
        "\uFF0F", "\u2215", "\u2044", "\u29F8", "\uFF3C", "\uFF0E", "\u3002", "\uFF20",
        "\uFE6B", "\uFF1A", "https://", "http://", "http:", "https:", "javascript:", "data:",
        "//evil.example", "evil.example", "\\\\evil.example", "user:pass@", "xn--", "<", ">",
        "\"", "'", "`", "[", "]", "[::1]", "::ffff:127.0.0.1", "127.0.0.1", "0x7f.1", "a", "b",
        "jellyfin", "sso", "web", "0", "1", "99999", "\u00FC", "\u00DF", "\u0130", "\uD800", "\uDC00",
        "\uD83D\uDE00", "\uFFFE", "\uFFFF",
    ];

    /// <summary>Random text from the fragment grammar, with noise characters between.</summary>
    internal static string Hostile(Random random, int maxParts = 8)
    {
        var parts = random.Next(1, maxParts + 1);
        var builder = new StringBuilder();
        for (var i = 0; i < parts; i++)
        {
            switch (random.Next(8))
            {
                case 0:
                    builder.Append(AnyChar(random));
                    break;
                default:
                    builder.Append(Fragments[random.Next(Fragments.Length)]);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Any UTF-16 code unit, lone surrogates included: the strings a .NET program can hold
    /// are not only the ones that are valid Unicode.
    /// </summary>
    internal static char AnyChar(Random random) => random.Next(5) switch
    {
        0 => (char)random.Next(0x20, 0x7F),
        1 => (char)random.Next(0, 0x20),
        2 => (char)random.Next(0x7F, 0xA0),
        3 => (char)random.Next(0xD800, 0xE000),
        _ => (char)random.Next(0, 0x10000),
    };

    /// <summary>
    /// A short string from the classes that matter to the configuration file: ordinary text,
    /// whitespace, C0 and C1 controls, NUL, the two noncharacters XML refuses, paired and lone
    /// surrogates. With <paramref name="dirty"/> false the unusual classes are rare, so that a
    /// fair share of the generated configurations is one the rules accept.
    /// </summary>
    internal static string Text(Random random, bool dirty)
    {
        var length = random.Next(0, 13);
        var builder = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            // Clean: the characters XML refuses (0 to 6) are rare, the unusual ones it keeps
            // (7 to 13) are not, so that a fair share of whole configurations is accepted.
            var pick = dirty ? random.Next(14)
                : random.Next(2_000) < 3 ? random.Next(7)
                : random.Next(10) == 0 ? 7 + random.Next(7)
                : 14;
            builder.Append(pick switch
            {
                0 => ((char)random.Next(0, 0x20)).ToString(),
                1 => ((char)random.Next(0x7F, 0xA0)).ToString(),
                2 => "\uFFFE",
                3 => "\uFFFF",
                4 => "\0",
                5 => ((char)random.Next(0xD800, 0xDC00)).ToString(),
                6 => ((char)random.Next(0xDC00, 0xE000)).ToString(),
                7 => char.ConvertFromUtf32(random.Next(0x10000, 0x110000)),
                8 => dirty ? "\r\n" : "\n\t",
                9 => "\u2028",
                10 => "]]>",
                11 => "&#x1;",
                12 => "\uFDD0",
                13 => "<&>\"'",
                _ => PrintableOrSpace(random),
            });
        }

        return builder.ToString();
    }

    private static string PrintableOrSpace(Random random) => random.Next(10) switch
    {
        0 => " ",
        1 => "\t",
        2 => "\n",
        3 => "\n",
        4 => "\u00FC",
        5 => "\u65E5",
        _ => ((char)random.Next(0x21, 0x7F)).ToString(),
    };
}
