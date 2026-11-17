using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Sso.Configuration;

/// <summary>
/// Settings from a JSON file, for servers set up by configuration management. The file is
/// named by <see cref="EnvironmentVariable"/> and read at startup. Every setting it names
/// wins over the configuration page; the ones it leaves out stay editable there. Links and
/// seen groups are the plugin's own state and cannot come from the file.
/// </summary>
public sealed class SettingsFile
{
    public const string EnvironmentVariable = "JELLYFIN_SSO_SETTINGS_FILE";

    private static readonly string[] State = [nameof(PluginConfiguration.Links), nameof(PluginConfiguration.SeenGroups)];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly PluginConfiguration? _values;
    private readonly PropertyInfo[] _managed;

    private SettingsFile(string path, PluginConfiguration? values, PropertyInfo[] managed, string? error)
    {
        Path = path;
        _values = values;
        _managed = managed;
        Error = error;
    }

    public string Path { get; }

    /// <summary>Why the file was not applied, or null when it was.</summary>
    public string? Error { get; }

    /// <summary>The settings the file decides, named as in <see cref="PluginConfiguration"/>.</summary>
    public IReadOnlyList<string> Managed => _managed.Select(p => p.Name).ToList();

    /// <summary>The file the environment names, or null when it names none.</summary>
    public static SettingsFile? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(path) ? null : Load(path.Trim());
    }

    public static SettingsFile Load(string path)
    {
        try
        {
            return Parse(path, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(path, $"cannot be read ({ex.GetType().Name})");
        }
    }

    internal static SettingsFile Parse(string path, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return Failed(path, $"is not valid JSON (line {ex.LineNumber + 1})");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failed(path, "must hold a JSON object");
            }

            var managed = new List<PropertyInfo>();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var property = typeof(PluginConfiguration).GetProperty(
                    entry.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property is null || !property.CanWrite)
                {
                    return Failed(path, $"names '{entry.Name}', which is not a setting");
                }

                if (State.Contains(property.Name))
                {
                    return Failed(path, $"names '{property.Name}', which the plugin keeps itself");
                }

                managed.Add(property);
            }

            PluginConfiguration values;
            try
            {
                values = document.RootElement.Deserialize<PluginConfiguration>(Options)!;
            }
            catch (JsonException ex)
            {
                return Failed(path, $"has a value of the wrong type ({ex.Path})");
            }

            // Links name their provider by id. A provider without one would get a new
            // random id at every start, and every account linked through it would be lost.
            if (managed.Any(p => p.Name == nameof(PluginConfiguration.Providers))
                && document.RootElement.EnumerateObject()
                    .First(e => e.Name.Equals(nameof(PluginConfiguration.Providers), StringComparison.OrdinalIgnoreCase))
                    .Value.EnumerateArray()
                    .Any(p => p.ValueKind != JsonValueKind.Object || !HasText(p, nameof(ProviderConfig.Id))))
            {
                return Failed(path, "has a provider without an Id");
            }

            if (ConfigurationRules.Problem(values) is { } problem)
            {
                return Failed(path, problem);
            }

            return new SettingsFile(path, values, managed.ToArray(), null);
        }
    }

    /// <summary>Puts the file's settings into a configuration; nothing when the file failed.</summary>
    public void ApplyTo(PluginConfiguration config)
    {
        if (_values is null)
        {
            return;
        }

        foreach (var property in _managed)
        {
            property.SetValue(config, property.GetValue(_values));
        }
    }

    private static bool HasText(JsonElement provider, string name)
        => provider.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()));

    private static SettingsFile Failed(string path, string why) => new(path, null, [], why);
}
