using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A throwaway Jellyfin server with the real plugin ZIP installed, plus a fake identity
/// provider it can reach. Started once for the whole e2e suite because a Jellyfin boot
/// costs about half a minute; <see cref="ResetAsync"/> puts it back to a known state
/// between tests.
/// </summary>
public sealed class JellyfinEnvironment : IAsyncLifetime
{
    private const string PluginVersion = "0.0.0.1";
    private const string AdminUser = "e2e-admin";
    private const string AdminPassword = "e2e-admin-password";
    public const string ClientId = "jellyfin-e2e";
    public const string ProviderId = "e2e-provider";
    public const string SecondProviderId = "e2e-provider-2";

    private readonly string _deviceId = Guid.NewGuid().ToString("N");
    private IContainer _jellyfin = null!;
    private HttpClient _admin = null!;

    public FakeIdentityProvider Idp { get; private set; } = null!;

    /// <summary>The certificate every identity provider in the suite serves, fake or real.</summary>
    public TestCertificate Certificate { get; private set; } = null!;

    /// <summary>Scratch directory for container inputs such as realm and config files.</summary>
    public string WorkDirectory { get; private set; } = string.Empty;

    /// <summary>Base URL of the server as seen from the test process and the browser.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// The same server under a name that ends in "localhost", which some providers insist
    /// on before they accept a plain-HTTP redirect URI.
    /// </summary>
    public string LocalhostBaseUrl => $"http://jellyfin.localhost:{new Uri(BaseUrl).Port}";

    public string AdminToken { get; private set; } = string.Empty;

    /// <summary>Identity of the setup administrator, for seeding a dashboard session.</summary>
    public string AdminUserId { get; private set; } = string.Empty;

    public string AdminUserName => AdminUser;

    public string ServerId { get; private set; } = string.Empty;

    public string MoviesLibraryId { get; private set; } = string.Empty;

    public string KidsLibraryId { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var repoRoot = RepoRoot();
        var pluginDir = BuildPluginPackage(repoRoot);
        var mediaDir = CreateMediaTree(repoRoot);

        WorkDirectory = Path.Combine(repoRoot, "tests", "runs", "e2e");
        Certificate = TestCertificate.Create(Path.Combine(WorkDirectory, "certs"));
        Idp = await FakeIdentityProvider.StartAsync(Certificate);

        _jellyfin = new ContainerBuilder($"jellyfin/jellyfin:{ServerImageTag(repoRoot)}")
            .WithPortBinding(8096, true)
            // Lets the server call the fake provider running in the test process.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithBindMount(pluginDir, $"/config/plugins/SSO Authentication_{PluginVersion}", AccessMode.ReadWrite)
            .WithBindMount(mediaDir, "/media", AccessMode.ReadOnly)
            .WithBindMount(Certificate.CertificatePath, "/usr/local/share/ca-certificates/e2e-idp.crt", AccessMode.ReadOnly)
            // The fake provider serves HTTPS with a self-signed certificate, so the server
            // has to trust it before Jellyfin starts.
            .WithEntrypoint("/bin/sh", "-c")
            // The server resolves the bundled web client relative to its working directory.
            .WithWorkingDirectory("/jellyfin")
            .WithCommand("update-ca-certificates > /dev/null && exec /jellyfin/jellyfin")
            // /System/Info/Public answers long before the server is usable; the startup
            // wizard endpoints reply 503 with Retry-After until it is.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPath("/Startup/User")
                .ForPort(8096)))
            .Build();

        await _jellyfin.StartAsync();

        BaseUrl = $"http://{_jellyfin.Hostname}:{_jellyfin.GetMappedPublicPort(8096)}";
        _admin = new HttpClient { BaseAddress = new Uri(BaseUrl) };

        await CompleteStartupWizardAsync();
        AdminToken = await AuthenticateAdminAsync();
        _admin.DefaultRequestHeaders.Authorization = AuthHeader(AdminToken);

        var me = JsonNode.Parse(await _admin.GetStringAsync("/Users/Me"))!;
        AdminUserId = me["Id"]!.GetValue<string>();
        ServerId = JsonNode.Parse(await _admin.GetStringAsync("/System/Info/Public"))!["Id"]!.GetValue<string>();

        MoviesLibraryId = await CreateLibraryAsync("Movies", "movies", "/media/movies");
        KidsLibraryId = await CreateLibraryAsync("Kids", "movies", "/media/kids");

        await ResetAsync();
    }

    /// <summary>
    /// Deletes everything an earlier test created and writes a known plugin configuration:
    /// group "media" sees Movies, "kids" sees Kids, "jellyfin-admins" is admin.
    /// </summary>
    public async Task ResetAsync()
    {
        foreach (var user in await ListUsersAsync())
        {
            var name = user!["Name"]!.GetValue<string>();
            if (name != AdminUser)
            {
                var response = await _admin.DeleteAsync($"/Users/{user["Id"]!.GetValue<string>()}");
                response.EnsureSuccessStatusCode();
            }
        }

        var config = new JsonObject
        {
            ["Providers"] = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = ProviderId,
                    ["Name"] = "Fake IdP",
                    ["Enabled"] = true,
                    ["Authority"] = Idp.ContainerAuthority,
                    ["ClientId"] = ClientId,
                    ["Scopes"] = new JsonArray { "openid", "profile", "email", "groups" },
                    ["GroupsClaim"] = "groups",
                    ["GroupsFromUserInfo"] = false,
                },
            },
            ["GroupMappings"] = new JsonArray
            {
                Mapping("media", admin: false, MoviesLibraryId),
                Mapping("kids", admin: false, KidsLibraryId),
                Mapping("jellyfin-admins", admin: true),
            },
            ["Links"] = new JsonArray(),
            ["SeenGroups"] = new JsonArray(),
        };

        var save = await _admin.PostAsJsonAsync("/sso/admin/config", config);
        save.EnsureSuccessStatusCode();
    }

    private static JsonObject Mapping(string group, bool admin, params string[] libraryIds)
        => new()
        {
            ["Group"] = group,
            ["IsAdministrator"] = admin,
            ["LibraryIds"] = new JsonArray(libraryIds.Select(id => (JsonNode)id!).ToArray()),
        };

    // ─── SSO logins ───────────────────────────────────────────────────────────

    public sealed record SsoLoginResult(
        System.Net.HttpStatusCode CallbackStatus,
        string? Secret,
        string? Body)
    {
        public bool Succeeded => Secret is not null;
    }

    /// <summary>
    /// Walks the web login the way a browser would: start, IdP authorize redirect, plugin
    /// callback. Returns the ephemeral secret embedded in the callback page, which is what
    /// the page would post to Jellyfin's native auth endpoint.
    /// </summary>
    public async Task<SsoLoginResult> StartWebLoginAsync(
        FakeUser user, HttpClient? client = null, string? providerId = null)
    {
        Idp.NextUser = user;
        client ??= TrustingClient();

        // Redirects are followed by hand: the chain crosses from the server (http) to the
        // provider (https) and back, and HttpClient refuses to follow a downgrade.
        var url = providerId is null
            ? $"{BaseUrl}/sso/oidc/start"
            : $"{BaseUrl}/sso/oidc/start/{providerId}";
        HttpResponseMessage response;
        for (var hop = 0; ; hop++)
        {
            response = await client.GetAsync(url);
            if (response.Headers.Location is null || hop == 10)
            {
                break;
            }

            url = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location.ToString()
                : new Uri(new Uri(url), response.Headers.Location).ToString();
        }

        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return new SsoLoginResult(response.StatusCode, null, body);
        }

        var match = Regex.Match(body, @"Pw:\s*""(?<secret>[^""]+)""");
        return new SsoLoginResult(
            response.StatusCode,
            match.Success ? Regex.Unescape(match.Groups["secret"].Value) : null,
            body);
    }

    /// <summary>Adds a second enabled provider, pointing at the same fake identity provider.</summary>
    public async Task AddSecondProviderAsync()
        => await PatchConfigAsync(config => config["Providers"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = SecondProviderId,
            ["Name"] = "Second IdP",
            ["Enabled"] = true,
            ["Authority"] = Idp.ContainerAuthority,
            ["ClientId"] = ClientId + "-2",
            ["Scopes"] = new JsonArray { "openid", "profile", "email", "groups" },
            ["GroupsClaim"] = "groups",
            ["GroupsFromUserInfo"] = false,
        }));

    /// <summary>Creates a local Jellyfin account and signs it in, as a client would.</summary>
    public async Task<(Guid UserId, string Token)> CreateLocalUserWithTokenAsync(string username)
    {
        const string Password = "local-password";
        var userId = await CreateLocalUserAsync(username, Password);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = username, Pw = Password }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "MediaBrowser",
            $"Client=\"e2e-tests\", Device=\"xunit\", DeviceId=\"{Guid.NewGuid():N}\", Version=\"1.0.0.0\"");

        using var client = TrustingClient();
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (userId, payload["AccessToken"]!.GetValue<string>());
    }

    /// <summary>An Authorization header carrying someone else's session token.</summary>
    public static AuthenticationHeaderValue TokenHeader(string token)
        => new(
            "MediaBrowser",
            $"Client=\"e2e-tests\", Device=\"xunit\", DeviceId=\"{Guid.NewGuid():N}\", "
            + $"Version=\"1.0.0.0\", Token=\"{token}\"");

    /// <summary>An HTTP client that accepts the fake provider's self-signed certificate.</summary>
    public static HttpClient TrustingClient(bool followRedirects = true) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        AllowAutoRedirect = followRedirects,
    });

    /// <summary>Redeems an ephemeral secret on Jellyfin's native auth path.</summary>
    public async Task<HttpResponseMessage> AuthenticateAsync(string username, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = username, Pw = secret }),
        };
        request.Headers.Authorization = AuthHeader(token: null);

        return await new HttpClient().SendAsync(request);
    }

    // ─── Jellyfin queries ─────────────────────────────────────────────────────

    public async Task<JsonArray> ListUsersAsync()
        => (JsonNode.Parse(await _admin.GetStringAsync("/Users")) as JsonArray)!;

    public async Task<JsonNode?> FindUserAsync(string username)
        => (await ListUsersAsync())
            .FirstOrDefault(u => u!["Name"]!.GetValue<string>() == username);

    public async Task<JsonNode> UserPolicyAsync(string username)
    {
        var user = await FindUserAsync(username)
            ?? throw new InvalidOperationException($"No Jellyfin user named '{username}'.");
        return user["Policy"]!;
    }

    public async Task<JsonNode> PluginConfigAsync()
        => JsonNode.Parse(await _admin.GetStringAsync("/sso/admin/config"))!;

    /// <summary>Reads the live plugin configuration, lets the test change it, and saves it.</summary>
    public async Task PatchConfigAsync(Action<JsonNode> change)
    {
        var config = await PluginConfigAsync();
        change(config);
        var response = await _admin.PostAsJsonAsync("/sso/admin/config", config);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The group mapping for a group name, as stored in the live configuration.</summary>
    public static JsonNode Mapping(JsonNode config, string group)
        => config["GroupMappings"]!.AsArray()
            .First(m => m!["Group"]!.GetValue<string>() == group)!;

    public async Task<Guid> CreateLocalUserAsync(string username, string password = "local-password")
    {
        var response = await _admin.PostAsJsonAsync("/Users/New", new { Name = username, Password = password });
        response.EnsureSuccessStatusCode();
        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return Guid.Parse(created["Id"]!.GetValue<string>());
    }

    public async Task<string> ServerLogsAsync() => (await _jellyfin.GetLogsAsync()).Stdout;

    // ─── Setup helpers ────────────────────────────────────────────────────────

    private async Task CompleteStartupWizardAsync()
    {
        var configuration = await _admin.PostAsJsonAsync("/Startup/Configuration", new
        {
            ServerName = "e2e",
            UICulture = "en-US",
            MetadataCountryCode = "US",
            PreferredMetadataLanguage = "en",
        });
        configuration.EnsureSuccessStatusCode();

        // Reading the pending user first is what the real wizard does.
        await _admin.GetAsync("/Startup/User");

        var user = await _admin.PostAsJsonAsync("/Startup/User", new
        {
            Name = AdminUser,
            Password = AdminPassword,
        });
        user.EnsureSuccessStatusCode();

        var complete = await _admin.PostAsync("/Startup/Complete", null);
        complete.EnsureSuccessStatusCode();
    }

    private async Task<string> AuthenticateAdminAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = AdminUser, Pw = AdminPassword }),
        };
        request.Headers.Authorization = AuthHeader(token: null);

        var response = await _admin.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return payload["AccessToken"]!.GetValue<string>();
    }

    private async Task<string> CreateLibraryAsync(string name, string collectionType, string path)
    {
        var url = $"/Library/VirtualFolders?name={Uri.EscapeDataString(name)}"
            + $"&collectionType={collectionType}&paths={Uri.EscapeDataString(path)}&refreshLibrary=false";

        var response = await _admin.PostAsJsonAsync(url, new
        {
            LibraryOptions = new
            {
                Enabled = true,
                EnableRealtimeMonitor = false,
                EnableInternetProviders = false,
                PathInfos = new[] { new { Path = path } },
            },
        });
        response.EnsureSuccessStatusCode();

        var folders = JsonNode.Parse(await _admin.GetStringAsync("/Library/VirtualFolders"))!.AsArray();
        var folder = folders.First(f => f!["Name"]!.GetValue<string>() == name);
        return folder!["ItemId"]!.GetValue<string>();
    }

    private AuthenticationHeaderValue AuthHeader(string? token)
    {
        var parameter = $"Client=\"e2e-tests\", Device=\"xunit\", DeviceId=\"{_deviceId}\", Version=\"1.0.0.0\"";
        if (token is not null)
        {
            parameter += $", Token=\"{token}\"";
        }

        return new AuthenticationHeaderValue("MediaBrowser", parameter);
    }

    /// <summary>Packs the plugin exactly as a release would and unpacks it for mounting.</summary>
    private static string BuildPluginPackage(string repoRoot)
    {
        var zip = Path.Combine(repoRoot, "dist", $"jellyfin-sso_{PluginVersion}.zip");
        Run(repoRoot, "bash", "scripts/package.sh", PluginVersion);

        var target = Path.Combine(repoRoot, "tests", "runs", "e2e", "plugin");
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);
        ZipFile.ExtractToDirectory(zip, target);
        return target;
    }

    /// <summary>Two library folders with a placeholder file each; no real media needed.</summary>
    private static string CreateMediaTree(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "tests", "runs", "e2e", "media");
        foreach (var library in new[] { "movies", "kids" })
        {
            var dir = Path.Combine(root, library);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "placeholder.txt"), "e2e");
        }

        return root;
    }

    /// <summary>
    /// The server image follows the Jellyfin.Controller reference, so the container always
    /// matches the ABI the plugin was built against.
    /// </summary>
    private static string ServerImageTag(string repoRoot)
    {
        var csproj = File.ReadAllText(
            Path.Combine(repoRoot, "Jellyfin.Plugin.Sso", "Jellyfin.Plugin.Sso.csproj"));
        var version = Regex.Match(csproj, @"""Jellyfin\.Controller"" Version=""(?<v>[0-9.]+)""").Groups["v"].Value;
        var parts = version.Split('.');
        return $"{parts[0]}.{parts[1]}";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static void Run(string workingDirectory, string file, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = string.Join(' ', args),
        })!;

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{file} {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose();
        if (_jellyfin is not null)
        {
            await _jellyfin.DisposeAsync();
        }

        if (Idp is not null)
        {
            await Idp.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class JellyfinCollection : ICollectionFixture<JellyfinEnvironment>
{
    public const string Name = "jellyfin";
}
