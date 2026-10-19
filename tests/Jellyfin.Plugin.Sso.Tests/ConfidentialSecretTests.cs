using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A confidential client's secret is the one value this plugin refuses to keep: the
/// configuration file holds a reference, and the secret is read at login from the
/// environment or from a file. What that reference may be is therefore the rule that keeps
/// the secret out of the file, and these tests hold it.
/// </summary>
public sealed class ConfidentialSecretTests : IDisposable
{
    private const string Variable = "JELLYFIN_SSO_TEST_SECRET";

    private readonly List<string> _files = [];

    private string FileHolding(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jellyfin-sso-{Guid.NewGuid():N}");
        File.WriteAllText(path, contents);
        _files.Add(path);
        return path;
    }

    [Fact]
    public void A_secret_is_read_from_the_environment()
    {
        Environment.SetEnvironmentVariable(Variable, "from-the-environment");

        Assert.Equal("from-the-environment", OidcClientFactory.ResolveSecret($"env:{Variable}"));
    }

    [Fact]
    public void An_environment_variable_nobody_set_is_an_error_rather_than_an_empty_secret()
    {
        Environment.SetEnvironmentVariable(Variable, null);

        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.ResolveSecret($"env:{Variable}"));

        Assert.Contains(Variable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_is_read_from_a_file()
    {
        var path = FileHolding("from-a-file");

        Assert.Equal("from-a-file", OidcClientFactory.ResolveSecret($"file:{path}"));
    }

    [Fact]
    public void The_newline_a_file_ends_with_is_not_part_of_the_secret()
    {
        // `echo secret > /run/secrets/sso` leaves one, and a secret with a newline in it
        // fails at the token endpoint with nothing to see in the plugin's own log.
        var path = FileHolding("  from-a-file\n");

        Assert.Equal("from-a-file", OidcClientFactory.ResolveSecret($"file:{path}"));
    }

    [Fact]
    public void A_file_that_is_not_there_fails_loudly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jellyfin-sso-missing-{Guid.NewGuid():N}");

        Assert.ThrowsAny<IOException>(() => OidcClientFactory.ResolveSecret($"file:{path}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_reference_at_all_names_what_is_missing(string? secretRef)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.ResolveSecret(secretRef));

        Assert.Contains("ConfidentialSecretRef", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // The literal secret, which is the one thing that must never be accepted here: taking
    // it would put it in the plugin's configuration file.
    [InlineData("s3cret")]
    [InlineData("ENV:JELLYFIN_SSO_TEST_SECRET")]
    [InlineData("FILE:/run/secrets/sso")]
    [InlineData("env")]
    [InlineData("file")]
    [InlineData("vault:secret/sso")]
    public void Anything_that_is_not_a_reference_is_refused(string secretRef)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.ResolveSecret(secretRef));

        Assert.Contains("must start with", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);

        foreach (var path in _files)
        {
            File.Delete(path);
        }
    }
}
