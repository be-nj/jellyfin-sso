using System.Collections;
using System.Reflection;
using System.Xml;

namespace Jellyfin.Plugin.Sso.Configuration;

/// <summary>
/// Whether a text can be written to the plugin's configuration file and read back.
///
/// Jellyfin writes a plugin's configuration with a writer that checks no characters — a
/// control character goes out as <c>&amp;#x1;</c> — and reads it with a reader that refuses
/// that reference. When the read fails, <c>BasePlugin.LoadConfiguration</c> catches it,
/// builds a default configuration and saves it over the file. So one such character
/// anywhere in the configuration means that at the next restart the plugin has no
/// providers, no mappings and no account links, and nothing in a log says why (#306).
///
/// Some of what is stored comes from outside: the group names a provider sends are
/// remembered for the page's suggestions, and the <c>sub</c> is the key of a link. Each of
/// those is checked where it arrives; <see cref="Scrub"/> is what stands behind them, on
/// the way to the file.
/// </summary>
internal static class StorableText
{
    /// <summary>What stands in for a character the file cannot hold.</summary>
    internal const char Replacement = '�';

    /// <summary>
    /// Whether every character of the text is one XML 1.0 allows in a document and the file
    /// gives back unchanged. A carriage return is allowed and is not given back: the writer
    /// puts it into the file as it is and the reader, as XML requires, reads it as a line
    /// feed, so a subject that held one was another subject after the next restart (#329).
    /// </summary>
    internal static bool CanCarry(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]) || !XmlConvert.IsXmlChar(text[i]) || text[i] == '\r')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The text with every character the file cannot hold replaced.</summary>
    internal static string Carried(string text)
    {
        if (CanCarry(text))
        {
            return text;
        }

        var result = text.ToCharArray();
        for (var i = 0; i < result.Length; i++)
        {
            if (char.IsHighSurrogate(result[i]) && i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(result[i]) || !XmlConvert.IsXmlChar(result[i]) || result[i] == '\r')
            {
                result[i] = Replacement;
            }
        }

        return new string(result);
    }

    /// <summary>
    /// Where in a configuration the first text is that the file cannot hold, as a path a
    /// person can find ("Providers[0].Name"), or null when there is none.
    /// </summary>
    internal static string? FirstUncarried(object configuration)
    {
        string? found = null;
        Walk(configuration, string.Empty, (path, text) =>
        {
            if (found is null && !CanCarry(text))
            {
                found = path;
            }

            return text;
        });
        return found;
    }

    /// <summary>
    /// Replaces, in place, every such character in every text of a configuration. True
    /// when something was replaced.
    /// </summary>
    internal static bool Scrub(object configuration)
    {
        var changed = false;
        Walk(configuration, string.Empty, (_, text) =>
        {
            var carried = Carried(text);
            changed |= !ReferenceEquals(carried, text);
            return carried;
        });
        return changed;
    }

    /// <summary>
    /// Every string the configuration holds, however deep: its own, those in its lists of
    /// strings, and those of the entries in its lists of providers, mappings and links.
    /// Found by reflection, so a setting added later is covered without a line here.
    /// </summary>
    private static void Walk(object? node, string path, Func<string, string, string> visit)
    {
        if (node is null)
        {
            return;
        }

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var at = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            var value = property.GetValue(node);

            if (value is string text)
            {
                var visited = visit(at, text);
                if (!ReferenceEquals(visited, text) && property.CanWrite)
                {
                    property.SetValue(node, visited);
                }
            }
            else if (value is IList<string> texts)
            {
                for (var i = 0; i < texts.Count; i++)
                {
                    if (texts[i] is { } entry)
                    {
                        var visited = visit($"{at}[{i}]", entry);
                        if (!ReferenceEquals(visited, entry))
                        {
                            texts[i] = visited;
                        }
                    }
                }
            }
            else if (value is IEnumerable entries and not string
                     && property.PropertyType.IsGenericType
                     && property.PropertyType.GetGenericArguments()[0].Namespace == typeof(StorableText).Namespace)
            {
                var i = 0;
                foreach (var entry in entries)
                {
                    Walk(entry, $"{at}[{i++}]", visit);
                }
            }
        }
    }
}
