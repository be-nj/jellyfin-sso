using System.Collections;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The configuration is one XML file, and Jellyfin answers a file it cannot read by
/// starting over with an empty configuration (#306). So the promise that matters is not
/// that a character is refused, but that everything the rules accept comes back from the
/// file unchanged. These tests fill every text of a configuration, in every list and every
/// entry, with strings from the classes that go wrong (NUL, C0 and C1 controls, U+FFFE and
/// U+FFFF, paired and lone surrogates, line breaks), then hold the rules and the scrubber to
/// that promise through the same writer and reader Jellyfin uses.
/// </summary>
public sealed class ConfigurationRoundTripPropertyTests
{
    private const int Seed = 20_261_127;

    // ---- The file, as Jellyfin writes and reads it ----------------------------------------

    private static PluginConfiguration ThroughTheFile(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var file = new MemoryStream();
        using (var text = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true))
        using (var writer = new XmlTextWriter(text))
        {
            serializer.Serialize(writer, config);
        }

        file.Position = 0;
        using var reader = XmlReader.Create(file);
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    // ---- Generating a configuration -------------------------------------------------------

    private static void Fill(object target, Random random, bool dirty)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var type = property.PropertyType;
            if (type == typeof(string))
            {
                property.SetValue(target, PropertyGen.Text(random, dirty));
            }
            else if (type == typeof(bool))
            {
                property.SetValue(target, random.Next(2) == 0);
            }
            else if (type == typeof(int))
            {
                property.SetValue(target, random.Next(0, 1000));
            }
            else if (type == typeof(int?))
            {
                property.SetValue(target, random.Next(2) == 0 ? null : random.Next(0, 22));
            }
            else if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                property.SetValue(target, values.GetValue(random.Next(values.Length)));
            }
            else if (type == typeof(List<string>))
            {
                property.SetValue(target, Enumerable.Range(0, random.Next(0, 4))
                    .Select(_ => PropertyGen.Text(random, dirty)).ToList());
            }
            else if (type == typeof(List<Guid>))
            {
                property.SetValue(target, Enumerable.Range(0, random.Next(0, 3)).Select(_ => Guid.NewGuid()).ToList());
            }
            else if (type == typeof(Guid))
            {
                property.SetValue(target, Guid.NewGuid());
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
                && type.GetGenericArguments()[0].Namespace == typeof(PluginConfiguration).Namespace)
            {
                var list = (IList)Activator.CreateInstance(type)!;
                for (var n = random.Next(0, 4); n > 0; n--)
                {
                    var entry = Activator.CreateInstance(type.GetGenericArguments()[0])!;
                    Fill(entry, random, dirty);
                    list.Add(entry);
                }

                property.SetValue(target, list);
            }
        }
    }

    /// <summary>
    /// A configuration whose every text is hostile, with the fields the rules check for
    /// something other than characters set to values they accept, so that what is left for the
    /// rules to decide is the characters.
    /// </summary>
    private static PluginConfiguration Generate(Random random, bool dirty)
    {
        var config = new PluginConfiguration();
        Fill(config, random, dirty);

        config.PublicAddress = random.Next(2) == 0 ? string.Empty : "https://jellyfin.example.com/jf";
        for (var i = 0; i < config.Providers.Count; i++)
        {
            var provider = config.Providers[i];
            provider.Id = "p" + i;
            provider.ConfidentialSecretRef = random.Next(2) == 0 ? null : "env:" + PropertyGen.Text(random, dirty);
            provider.SigningAlgorithms = [];
            provider.EndpointHosts = [];
        }

        foreach (var mapping in config.GroupMappings)
        {
            // A mapping has a group: the rules refuse one without (#330).
            mapping.Group = "g" + mapping.Group;
            mapping.LibraryIds = Enumerable.Range(0, random.Next(0, 3)).Select(_ => Guid.NewGuid().ToString()).ToList();
        }

        return config;
    }

    // ---- Comparing two configurations -----------------------------------------------------

    private static string? FirstDifference(object? expected, object? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: {Show(expected)} became {Show(actual)}";
        }

        if (expected is string a)
        {
            return string.Equals(a, actual as string, StringComparison.Ordinal)
                ? null
                : $"{path}: {Show(a)} became {Show(actual as string)}";
        }

        var type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(Guid))
        {
            return expected.Equals(actual) ? null : $"{path}: {expected} became {actual}";
        }

        if (expected is IEnumerable left && actual is IEnumerable right)
        {
            var one = left.Cast<object?>().ToList();
            var other = right.Cast<object?>().ToList();
            if (one.Count != other.Count)
            {
                return $"{path}: {one.Count} entries became {other.Count}";
            }

            for (var i = 0; i < one.Count; i++)
            {
                if (FirstDifference(one[i], other[i], $"{path}[{i}]") is { } inner)
                {
                    return inner;
                }
            }

            return null;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (FirstDifference(property.GetValue(expected), property.GetValue(actual), $"{path}.{property.Name}") is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string Show(object? value)
        => value is string text
            ? "\"" + string.Concat(text.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString())) + "\""
            : value is null ? "null" : value.ToString()!;

    /// <summary>What happens to the configuration in the file, as a sentence: null when nothing.</summary>
    private static string? WhatTheFileDoes(PluginConfiguration config)
    {
        try
        {
            return FirstDifference(config, ThroughTheFile(config), "config");
        }
        catch (Exception ex) when (ex is InvalidOperationException or XmlException)
        {
            return "the file cannot be read back: " + (ex.InnerException ?? ex).Message;
        }
    }

    // ---- Properties -----------------------------------------------------------------------

    /// <summary>
    /// Attack: a provider sends, or an administrator pastes, a text the rules let in and the
    /// file does not keep, and the next restart finds an empty configuration (every provider,
    /// mapping and account link gone), or a link whose key is no longer the subject it was.
    /// </summary>
    [Fact]
    public void A_configuration_the_rules_accept_comes_back_from_the_file_unchanged()
    {
        var random = new Random(Seed);
        var accepted = 0;
        for (var i = 0; i < 600; i++)
        {
            var config = Generate(random, dirty: i % 3 == 0);
            if (ConfigurationRules.Problem(config) is not null)
            {
                continue;
            }

            accepted++;
            var what = WhatTheFileDoes(config);
            Assert.True(what is null, "an accepted configuration changed in the file: " + what);
        }

        Assert.True(accepted > 300, $"only {accepted} configurations were accepted, so the property was barely tried");
    }

    /// <summary>
    /// The scrubber is what stands behind everything that arrives from outside: whatever it is
    /// given, what it hands back is acceptable to the rules, survives the file unchanged, and is
    /// left alone by a second pass.
    /// </summary>
    [Fact]
    public void A_scrubbed_configuration_is_always_storable()
    {
        var random = new Random(Seed + 1);
        var scrubbed = 0;
        for (var i = 0; i < 600; i++)
        {
            var config = Generate(random, dirty: true);
            if (StorableText.Scrub(config))
            {
                scrubbed++;
            }

            Assert.Null(StorableText.FirstUncarried(config));
            Assert.Null(ConfigurationRules.Problem(config));
            var what = WhatTheFileDoes(config);
            Assert.True(what is null, "a scrubbed configuration changed in the file: " + what);
            Assert.False(StorableText.Scrub(config), "a second scrub changed something");
        }

        Assert.True(scrubbed > 400, $"only {scrubbed} configurations needed scrubbing");
    }

    /// <summary>
    /// What <c>CanCarry</c> says about a text, held against the file itself, one character at a
    /// time and at the edges of a value, where an XML reader trims and normalises.
    /// </summary>
    [Fact]
    public void A_text_that_can_be_carried_survives_the_file_whatever_its_characters()
    {
        var units = Enumerable.Range(0, 0x300)
            .Concat([0x2028, 0x2029, 0xFEFF, 0xFFFD, 0xFDD0, 0xFFFE, 0xFFFF, 0xE000, 0xD7FF])
            .Select(code => ((char)code).ToString())
            .Concat(["\uD83D\uDE00", "\uDBFF\uDFFF", "\uD800\uDC00", "\r\n", "\n\r", "\r\r\n"]);

        foreach (var unit in units)
        {
            foreach (var text in new[] { unit, "x" + unit + "x", unit + "x" + unit, unit + unit, "  " + unit })
            {
                if (!StorableText.CanCarry(text))
                {
                    continue;
                }

                var config = new PluginConfiguration();
                config.SeenGroups.Add(text);
                config.Providers.Add(new ProviderConfig { Id = "p", Name = text });
                config.Links.Add(new LinkConfig { ProviderId = "p", Sub = text });

                var what = WhatTheFileDoes(config);
                Assert.True(what is null, $"CanCarry accepted {Show(text)} and then: {what}");
            }
        }
    }

    /// <summary>
    /// The replacement keeps what the file can hold and nothing else: the same length, every
    /// whole character kept, every other one replaced, judged against the XML 1.0 definition of
    /// a character written out from the specification.
    /// </summary>
    [Fact]
    public void Scrubbing_replaces_exactly_the_characters_xml_does_not_allow()
    {
        var random = new Random(Seed + 2);
        for (var i = 0; i < 5_000; i++)
        {
            var text = PropertyGen.Text(random, dirty: true);
            var carried = StorableText.Carried(text);

            Assert.True(StorableText.CanCarry(carried), $"{Show(text)} became {Show(carried)}");
            Assert.True(carried.Length == text.Length, $"{Show(text)} became {Show(carried)}");
            Assert.True(
                carried == Reference(text),
                $"{Show(text)} became {Show(carried)}, expected {Show(Reference(text))}");
        }
    }

    /// <summary>XML 1.0, section 2.2, applied to a UTF-16 string: lone surrogates are not characters.</summary>
    private static string Reference(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            // A carriage return is a character, but the file reads it back as a line feed (#329).
            var allowed = v is 0x9 or 0xA
                || v is >= 0x20 and <= 0xD7FF
                || v is >= 0xE000 and <= 0xFFFD
                || v is >= 0x10000 and <= 0x10FFFF;

            // EnumerateRunes already stands U+FFFD in for a lone surrogate.
            result.Append(allowed ? rune.ToString() : "\uFFFD");
        }

        return result.ToString();
    }

    // ---- Found by the properties above, and fixed ---------------------------------

    /// <summary>
    /// Attack, or just a value: a subject, group name or provider name with a carriage return in
    /// it. The writer puts the character into the file as it is, and the reader, as XML
    /// requires, turns it into a line feed: the key of an account link is not the subject it was
    /// written from, so the next login with that subject finds no link.
    /// </summary>
    [Theory]
    [InlineData("a\rb")]
    [InlineData("a\r\nb")]
    [InlineData("a\r")]
    public void A_carriage_return_is_either_refused_or_survives_the_file(string text)
    {
        var config = new PluginConfiguration();
        config.Links.Add(new LinkConfig { ProviderId = "p", Sub = text });

        if (!StorableText.CanCarry(text))
        {
            return;
        }

        var back = ThroughTheFile(config);
        Assert.Equal(text, back.Links[0].Sub);
    }
}
