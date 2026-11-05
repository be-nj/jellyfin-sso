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

    /// <summary>
    /// A file that cannot be read is an error the callback can report, not one that
    /// escapes it. An IOException is not among the types the anonymous callback catches,
    /// so this used to answer 500 with the framework's message in the body — and that
    /// message names a path inside the container. The reason is kept, as the inner
    /// exception, for whoever reads the log.
    /// </summary>
    [Fact]
    public void A_file_that_is_not_there_is_reported_without_naming_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jellyfin-sso-missing-{Guid.NewGuid():N}");

        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.ResolveSecret($"file:{path}"));

        Assert.DoesNotContain(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConfidentialSecretRef", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
    }

    /// <summary>A directory, which is the other way a path can fail to be a file.</summary>
    [Fact]
    public void A_path_that_is_not_a_file_is_reported_the_same_way()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.ResolveSecret($"file:{Path.GetTempPath()}"));

        Assert.Contains("ConfidentialSecretRef", ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// This message used to quote the value it was given, and the callback handed it to
    /// anonymous callers. The value is what an operator pastes the real client secret
    /// into, so it does not belong in the message — not in an HTTP body, and not in the
    /// log either.
    /// </summary>
    [Fact]
    public void The_refusal_does_not_quote_the_value_it_was_given()
    {
        const string Pasted = "kc-7f3b9a21-literal-client-secret";

        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.ResolveSecret(Pasted));

        Assert.DoesNotContain(Pasted, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("7f3b9a21", ex.Message, StringComparison.Ordinal);
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
