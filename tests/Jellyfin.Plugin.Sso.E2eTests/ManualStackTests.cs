using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Not a test: the end-to-end stack, held open so a person — or a phone, a television
/// box, an emulator — can log into it by hand.
///
/// Everything the automated suite builds is already right for this. A throwaway Jellyfin
/// with the release ZIP installed, the provider the suite uses, a media tree, group
/// mappings. What was missing is a way to keep it running, because every other test tears
/// it down within seconds.
///
/// It needs no password. The suite's provider has no login form: whoever opens its
/// authorize endpoint comes straight back as <see cref="FakeIdentityProvider.NextUser"/>,
/// which is set below. That is what makes a client check repeatable — no account at a real
/// provider, no credentials anywhere near it.
///
///   make stack                     # holds for 45 minutes
///   E2E_STACK_MINUTES=120 make stack
///
/// It prints what a client needs, and also writes it to tests/runs/stack.json. Stop it
/// early by deleting that file.
/// </summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "Manual")]
public sealed class ManualStackTests
{
    private readonly JellyfinEnvironment _env;

    public ManualStackTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task A_stack_a_person_can_log_into()
    {
        await _env.ResetAsync();

        // Whoever logs in through a browser arrives as this person: in the "media" group,
        // so the group mapping gives them the Movies library and nothing else.
        _env.Idp.NextUser = new FakeUser("sub-manual-stack", "stackuser", ["media"]);

        await _env.PatchConfigAsync(config => config["InjectLoginButtonIntoForm"] = true);

        var minutes = int.TryParse(
            Environment.GetEnvironmentVariable("E2E_STACK_MINUTES"), out var requested)
            ? requested
            : 45;

        var details = new JsonObject
        {
            ["jellyfin"] = _env.BaseUrl,
            ["jellyfinPort"] = new Uri(_env.BaseUrl).Port,
            ["providerPort"] = _env.Idp.Port,
            ["providerAuthorizeUrl"] = _env.Idp.AuthorizeEndpoint,
            ["providerPlainPort"] = _env.Idp.PlainPort,
            ["ssoLogin"] = $"{_env.BaseUrl}/sso/oidc/start",
            ["ssoQuickConnect"] = $"{_env.BaseUrl}/sso/oidc/start/qc?code=CODE",
            ["signsInAs"] = _env.Idp.NextUser.PreferredUsername,
            ["localAdmin"] = _env.AdminUserName,
            ["holdsUntil"] = DateTimeOffset.Now.AddMinutes(minutes).ToString("HH:mm:ss"),
        };

        var path = Path.Combine(_env.WorkDirectory, "stack.json");
        await File.WriteAllTextAsync(path, details.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        Console.WriteLine("─── stack up ───────────────────────────────────────────────");
        Console.WriteLine(details.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        Console.WriteLine($"details also in {path}; delete it to stop early");
        Console.WriteLine("────────────────────────────────────────────────────────────");

        var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
        while (DateTimeOffset.UtcNow < deadline && File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        // The point is the time in between; reaching here is success either way.
        Assert.True(true);
    }
}
