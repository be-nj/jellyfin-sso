using Jellyfin.Plugin.Sso.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Stands up a <see cref="Plugin"/> instance backed by an in-memory configuration, because
/// the provisioning and permission code reads <c>Plugin.Instance.Configuration</c> directly.
/// Configuration saves are swallowed; the test asserts against <see cref="Configuration"/>.
/// </summary>
internal sealed class PluginHarness : IDisposable
{
    private readonly string _configDir;

    public PluginHarness()
    {
        _configDir = Directory.CreateTempSubdirectory("jellyfin-sso-tests-").FullName;

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_configDir);
        paths.PluginsPath.Returns(_configDir);
        paths.ProgramDataPath.Returns(_configDir);

        Configuration = new PluginConfiguration();

        var xmlSerializer = Substitute.For<IXmlSerializer>();
        xmlSerializer
            .DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(Configuration);

        Plugin = new Plugin(paths, xmlSerializer);
    }

    public Plugin Plugin { get; }

    public PluginConfiguration Configuration { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written; nothing to clean up.
        }
    }
}
