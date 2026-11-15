using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Not a test the suite can run: a complete login against Google, clicked by a person.
///
/// Google cannot be started in a container and its login form cannot be driven by a
/// script, so this is the one provider check that needs somebody at a browser. What the
/// harness does is everything around the click: a throwaway Jellyfin on a fixed port, so
/// the redirect URI could be registered with Google beforehand; a Google provider
/// configured the way the documentation says to configure one; the client's secret
/// handed to the server as a file, the way a confidential client's secret is meant to
/// arrive; and then it waits for the login and writes down what Google sent.
///
///   make google
///
/// It needs tests/runs/google/client.env, which is never committed:
///
///   GOOGLE_CLIENT_ID=<value>
///   GOOGLE_CLIENT_SECRET=<value>
///
/// and a Google OAuth client of type "Web application" whose authorized redirect URI is
/// http://localhost:8097/sso/oidc/callback/google. tests/google/README.md has the steps.
/// Neither value is printed or written anywhere but into the throwaway server.
/// </summary>
[Collection(JellyfinCollection.Name)]
[Trait("Category", "Manual")]
public sealed class GoogleLoginTests
{
    private const string ProviderId = "google";
    private const string SecretPath = "/config/google-client-secret";

    private static readonly string[] GoogleHosts =
        ["www.googleapis.com", "oauth2.googleapis.com", "openidconnect.googleapis.com"];

    private readonly JellyfinEnvironment _env;

    public GoogleLoginTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public async Task A_google_account_signs_in()
    {
        var directory = Path.GetFullPath(Path.Combine(_env.WorkDirectory, "..", "google"));
        var credentials = Path.Combine(directory, "client.env");
        Assert.True(
            File.Exists(credentials),
            $"{credentials} is missing. It holds GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET; "
            + "see tests/google/README.md.");
        Assert.True(
            JellyfinEnvironment.FixedHostPort is not null,
            "Run this through `make google`: the redirect URI registered with Google names a "
            + "port, so the server has to be on that port.");

        var values = (await File.ReadAllLinesAsync(credentials))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
        Assert.True(
            values.TryGetValue("GOOGLE_CLIENT_ID", out var clientId) && clientId.Length > 0
            && values.TryGetValue("GOOGLE_CLIENT_SECRET", out var secret) && secret.Length > 0,
            $"{credentials} needs GOOGLE_CLIENT_ID=… and GOOGLE_CLIENT_SECRET=… lines.");

        await _env.ResetAsync();
        await _env.WriteServerFileAsync(SecretPath, values["GOOGLE_CLIENT_SECRET"]);

        // The setup the documentation recommends for Google, except that this one creates
        // the account: there is nobody to link on a server that was made a minute ago.
        await _env.PatchConfigAsync(config => config["Providers"] = new JsonArray(new JsonObject
        {
            ["Id"] = ProviderId,
            ["Name"] = "Google",
            ["Enabled"] = true,
            ["Authority"] = "https://accounts.google.com",
            ["ClientId"] = values["GOOGLE_CLIENT_ID"],
            ["ClientType"] = 1,
            ["ConfidentialSecretRef"] = $"file:{SecretPath}",
            ["EndpointHosts"] = new JsonArray(GoogleHosts.Select(h => (JsonNode)h).ToArray()),
            ["CreateAccounts"] = true,
            ["ManagePermissions"] = false,
        }));

        using var admin = JellyfinEnvironment.TrustingClient();
        admin.DefaultRequestHeaders.Authorization = JellyfinEnvironment.TokenHeader(_env.AdminToken);
        var check = JsonNode.Parse(await (await admin.PostAsync(
            $"{_env.BaseUrl}/sso/admin/providers/{ProviderId}/check", null)).Content.ReadAsStringAsync())!;
        Assert.True(check["Ok"]!.GetValue<bool>(), check.ToJsonString());

        var port = JellyfinEnvironment.FixedHostPort!.Value;
        var start = $"http://localhost:{port}/sso/oidc/start/{ProviderId}";
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("E2E_STACK_MINUTES"), out var requested)
            ? requested
            : 30;
        var indented = new JsonSerializerOptions { WriteIndented = true };

        var ready = Path.Combine(directory, "ready.json");
        var result = Path.Combine(directory, "result.json");
        File.Delete(result);
        await File.WriteAllTextAsync(ready, new JsonObject
        {
            ["open"] = start,
            ["redirectUriRegisteredWithGoogle"] = $"http://localhost:{port}/sso/oidc/callback/{ProviderId}",
            ["providerCheck"] = check["Lines"]!.DeepClone(),
            ["waitsUntil"] = DateTimeOffset.Now.AddMinutes(minutes).ToString("HH:mm:ss"),
        }.ToJsonString(indented));
        Console.WriteLine($"─── open {start} in a browser on this machine and sign in to Google ───");

        // The login is done when the server holds a link for this provider: that is
        // written only after the token was exchanged with the secret, validated against
        // Google's keys and an account was created for it.
        JsonNode? link = null;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
        while (link is null && DateTimeOffset.UtcNow < deadline && File.Exists(ready))
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            link = (await _env.PluginConfigAsync())["Links"]!.AsArray()
                .FirstOrDefault(l => l!["ProviderId"]!.GetValue<string>() == ProviderId);
        }

        var log = await _env.ServerLogsAsync();
        var plugin = log.Split('\n')
            .Where(line => line.Contains("Jellyfin.Plugin.Sso", StringComparison.Ordinal))
            .TakeLast(40)
            .ToArray();

        var report = new JsonObject
        {
            ["signedIn"] = link is not null,
            ["pluginLogTail"] = new JsonArray(plugin.Select(l => (JsonNode)l).ToArray()),
        };

        if (link is not null)
        {
            var userId = link["JellyfinUserId"]!.GetValue<string>();
            var user = (await _env.ListUsersAsync())
                .First(u => Guid.Parse(u!["Id"]!.GetValue<string>()) == Guid.Parse(userId))!;
            var sub = link["Sub"]!.GetValue<string>();
            var policy = await _env.UserPolicyAsync(user["Name"]!.GetValue<string>());

            // The subject is somebody's Google account number: its shape is recorded, the
            // number is not.
            report["subject"] = new JsonObject
            {
                ["length"] = sub.Length,
                ["allDigits"] = sub.All(char.IsAsciiDigit),
            };
            report["accountNameFromGoogle"] = user["Name"]!.GetValue<string>();
            report["hasProfileImage"] = user["PrimaryImageTag"] is not null;
            report["groupsSeen"] = (await _env.PluginConfigAsync())["SeenGroups"]!.DeepClone();
            report["policy"] = new JsonObject
            {
                ["IsAdministrator"] = policy["IsAdministrator"]!.DeepClone(),
                ["EnableAllFolders"] = policy["EnableAllFolders"]!.DeepClone(),
                ["EnabledFolders"] = policy["EnabledFolders"]!.DeepClone(),
                ["AuthenticationProviderId"] = policy["AuthenticationProviderId"]!.DeepClone(),
            };
        }

        await File.WriteAllTextAsync(result, report.ToJsonString(indented));

        // Held open a little longer, so the person at the browser can look at the server
        // they were signed in to, and sign in a second time: the second login takes the
        // link instead of creating an account.
        var hold = DateTimeOffset.UtcNow.AddMinutes(link is null ? 0 : 10);
        while (DateTimeOffset.UtcNow < hold && File.Exists(ready))
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        File.Delete(ready);
        Assert.True(link is not null, $"No Google login arrived within {minutes} minutes; see {result}.");
    }
}
