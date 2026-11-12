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

    /// <summary>The plugin's id, as Jellyfin's own /Plugins endpoints know it.</summary>
    public const string PluginId = "f5538c3a-1bce-4b8c-b8a5-bc1a0fffdf27";
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
            // And the conformance suite, which runs in its own containers and publishes
            // itself on the Docker host under this name. Inside a container the name
            // resolves to 127.0.0.1, which would be the server itself.
            .WithExtraHost(TestCertificate.ConformanceHostName, "host-gateway")
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
        // Links go through their own endpoint, the way the Links tab does. The plugin's
        // generic save deliberately ignores links in the body — the configuration page
        // posts the snapshot it loaded with, and writing that back resurrected links an
        // administrator had removed — so clearing them here has to mean what it says.
        foreach (var link in (JsonNode.Parse(
            await _admin.GetStringAsync("/sso/admin/links")) as JsonArray)!)
        {
            await RemoveLinkAsync(Guid.Parse(link!["JellyfinUserId"]!.GetValue<string>()));
        }

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
            // Links are ignored by the server and cleared above, through their own
            // endpoint; sent as empty so this body still has the shape the page posts.
            // SeenGroups is a suggestion list and is writable, so this does clear it.
            ["Links"] = new JsonArray(),
            ["SeenGroups"] = new JsonArray(),
            // Every test in this suite reaches the server from the same address, and a
            // suite signs in far more often per minute than a household does. The limit
            // is switched off here and turned on by the test that is about it.
            ["LoginRequestsPerMinute"] = 0,
        };

        var save = await _admin.PostAsJsonAsync("/sso/admin/config", config);
        save.EnsureSuccessStatusCode();

        // What this deployment will send, told to the provider the way an operator tells
        // a real one. With these registered, the fake refuses anything else — which is
        // what makes a wrong redirect URI a failing test rather than a passing one.
        // Every knob on the fake provider, not just the redirect URIs that used to be
        // reset here: a test that leaves one set makes the next class fail for a reason
        // two files away.
        Idp.ResetBehaviour();

        Idp.RegisteredRedirectUris.Clear();
        Idp.RegisteredRedirectUris.Add($"{BaseUrl}/sso/oidc/callback/{ProviderId}");
        Idp.RegisteredRedirectUris.Add($"{BaseUrl}/sso/oidc/callback/{SecondProviderId}");
        Idp.RegisteredRedirectUris.Add($"{BaseUrl}/web/index.html");
    }

    /// <summary>
    /// Registers the addresses a deployment under <paramref name="prefix"/> would send, so
    /// a test can drive the whole flow behind a sub-path the way a proxy presents one.
    /// </summary>
    public void RegisterPrefixedRedirectUris(string prefix)
    {
        Idp.RegisteredRedirectUris.Add($"{BaseUrl}{prefix}/sso/oidc/callback/{ProviderId}");
        Idp.RegisteredRedirectUris.Add($"{BaseUrl}{prefix}/web/index.html");
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
        FakeUser user,
        HttpClient? client = null,
        string? providerId = null,
        string? quickConnectCode = null,
        string? forwardedPrefix = null,
        string? startUrl = null)
    {
        Idp.NextUser = user;
        // On a prefixed walk the loop below follows the chain itself, because it has to
        // rewrite each address and carry the cookie by hand. Leaving the handler to follow
        // redirects would take those hops out of reach and drop the cookie with them.
        client ??= forwardedPrefix is null
            ? TrustingClient()
            : TrustingClient(followRedirects: false, useCookies: false);

        // Redirects are followed by hand: the chain crosses from the server (http) to the
        // provider (https) and back, and HttpClient refuses to follow a downgrade.
        // startUrl is for a walk that begins somewhere the harness did not choose — the
        // link on the provider picker, say, which is the only way to reach the picker's
        // own route rather than the one this method would have built.
        var url = startUrl ?? (providerId, quickConnectCode) switch
        {
            (null, null) => $"{BaseUrl}/sso/oidc/start",
            (null, _) => $"{BaseUrl}/sso/oidc/start/qc?code={quickConnectCode}",
            (_, null) => $"{BaseUrl}/sso/oidc/start/{providerId}",
            _ => $"{BaseUrl}/sso/oidc/start/{providerId}/qc?code={quickConnectCode}",
        };
        // Carried by hand on a prefixed walk, and only there. The plugin scopes its
        // cookie to the path a browser sees — "/jellyfin/sso" — while this harness talks
        // to the server directly and so uses the stripped path. A browser behind a real
        // proxy sees the prefixed path and the scope matches; here it would not, and the
        // cookie the login depends on would silently not be sent.
        var carried = new Dictionary<string, string>(StringComparer.Ordinal);

        HttpResponseMessage response;
        for (var hop = 0; ; hop++)
        {
            // Stand in for the proxy: it strips the prefix from the path and tells the
            // server about it in a header. Without the stripping, the address the plugin
            // hands the provider comes back as a path this server does not serve — which
            // is a proxy's whole job and the reason a sub-path deployment is worth a test.
            var target = url;
            if (forwardedPrefix is not null
                && target.StartsWith(BaseUrl + forwardedPrefix, StringComparison.Ordinal))
            {
                target = BaseUrl + target[(BaseUrl.Length + forwardedPrefix.Length)..];
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (forwardedPrefix is not null && target.StartsWith(BaseUrl, StringComparison.Ordinal))
            {
                // Only on the hops that reach Jellyfin: the provider is a different server
                // and has no business being told about our proxy.
                request.Headers.Add("X-Forwarded-Prefix", forwardedPrefix);

                if (carried.Count > 0)
                {
                    request.Headers.Add(
                        "Cookie",
                        string.Join("; ", carried.Select(c => $"{c.Key}={c.Value}")));
                }
            }

            response = await client.SendAsync(request);

            if (forwardedPrefix is not null
                && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var pair in setCookies
                    .Select(c => c.Split(';', 2)[0])
                    .Select(c => c.Split('=', 2))
                    .Where(c => c.Length == 2))
                {
                    carried[pair[0]] = pair[1];
                }
            }
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

    /// <summary>
    /// An HTTP client that accepts the fake provider's self-signed certificate, and that
    /// can reach the provider under the name the provider publishes.
    ///
    /// That name is <see cref="TestCertificate.HostName"/>, which resolves inside the
    /// Jellyfin container and nowhere else — the browser is launched with a resolver rule
    /// for it, and this is the same rule for the test process. A client that could not
    /// follow a redirect to it was fine for as long as the provider published a second
    /// name for browser-facing endpoints; it stopped being fine when the plugin began
    /// requiring one host for everything it hands something to, which is what every real
    /// provider publishes.
    /// </summary>
    public static HttpClient TrustingClient(bool followRedirects = true, bool useCookies = true)
        => new(new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
            AllowAutoRedirect = followRedirects,
            // A prefixed walk carries cookies by hand, and the handler's own jar would
            // scope them to the stripped path and drop the manual header.
            UseCookies = useCookies,
            ConnectCallback = async (context, ct) =>
            {
                var host = string.Equals(
                    context.DnsEndPoint.Host, TestCertificate.HostName, StringComparison.Ordinal)
                    ? "127.0.0.1"
                    : context.DnsEndPoint.Host;

                var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
                    await socket.ConnectAsync(host, context.DnsEndPoint.Port, ct)
                        .ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
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

    /// <summary>Removes a link the way the Links tab does.</summary>
    public async Task RemoveLinkAsync(Guid jellyfinUserId)
    {
        using var response = await _admin.DeleteAsync($"/sso/admin/links/{jellyfinUserId}");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Posts a configuration as the administrator and hands back the response instead of
    /// insisting on success — for the cases where being refused is the point.
    /// </summary>
    public async Task<(System.Net.HttpStatusCode Status, string Body)> TrySaveConfigAsync(
        Action<JsonNode> change)
    {
        var config = await PluginConfigAsync();
        change(config);
        using var response = await _admin.PostAsJsonAsync("/sso/admin/config", config);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
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

    /// <summary>
    /// Writes a file inside the server container. One provider needs it: a confidential
    /// client's secret is only ever named by path in the plugin's configuration, never
    /// held in it, so the path has to exist on the server's own filesystem.
    /// </summary>
    public Task WriteServerFileAsync(string path, string content)
        => _jellyfin.CopyAsync(System.Text.Encoding.UTF8.GetBytes(content), path);

    public async Task<string> ServerLogsAsync() => (await _jellyfin.GetLogsAsync()).Stdout;

    /// <summary>
    /// Waits until the server's log carries <paramref name="fragment"/>, and returns the
    /// log from <paramref name="skip"/> bytes on.
    ///
    /// A log line is not there the moment the request that caused it has answered. The
    /// server hands it to its own logger, the logger writes it when it writes it, and
    /// Docker passes it on when asked — three steps that have nothing to do with the
    /// response already sent. Reading once, straight after the response, therefore passes
    /// on an idle machine and fails on a busy one: that is what
    /// `Switched_on_with_no_mappings_at_all_turns_everybody_away` did in 36 ms under load,
    /// with the line it wanted arriving afterwards
    /// ([#224](https://github.com/be-nj/jellyfin-sso/issues/224)).
    ///
    /// Asserting on a log is asserting on something asynchronous, so it needs a wait and
    /// not a read. The failure message carries the tail of the log, because "the line was
    /// not there" is useless without what was.
    /// </summary>
    public async Task<string> WaitForServerLogAsync(
        string fragment, int skip = 0, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var logs = string.Empty;

        while (true)
        {
            logs = await ServerLogsAsync();
            var window = logs.Length > skip ? logs[skip..] : string.Empty;

            if (window.Contains(fragment, StringComparison.Ordinal))
            {
                return window;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Fail(
            $"The server log never carried \"{fragment}\" within {timeoutMs / 1000} s.\n"
            + "last 20 lines:\n"
            + string.Join("\n", logs.Split('\n').TakeLast(20)));
        return logs;
    }

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

    /// <summary>
    /// The two libraries the demo needs beside the existing Kids one, each with a cover so
    /// the home screen shows something other than a grey tile. Idempotent: a second call
    /// returns the libraries the first one made.
    /// </summary>
    public async Task<(string Parents, string Kids, string Family)> AddDemoLibrariesAsync()
    {
        var existing = JsonNode.Parse(await _admin.GetStringAsync("/Library/VirtualFolders"))!.AsArray();

        async Task<string> EnsureAsync(string name, string folder)
        {
            var found = existing.FirstOrDefault(f => f!["Name"]!.GetValue<string>() == name);
            if (found is null)
            {
                return await CreateLibraryAsync(name, "movies", $"/media/{folder}", internetProviders: true);
            }

            // The library was made for the ordinary tests, which keep the internet out of
            // their way. The demo wants the posters.
            var id = found["ItemId"]!.GetValue<string>();
            await AllowInternetProvidersAsync(id, name, $"/media/{folder}");
            return id;
        }

        var libraries = (
            Parents: await EnsureAsync("Parents", "parents"),
            Kids: await EnsureAsync("Kids", "kids"),
            Family: await EnsureAsync("Family", "family"));

        await FetchArtworkAsync([libraries.Parents, libraries.Kids, libraries.Family]);
        return libraries;
    }

    /// <summary>
    /// Scans the demo libraries and waits until each film has a picture, which is
    /// Jellyfin asking TMDB on its own account. Falls back to the covers drawn in
    /// tests/fixtures when the artwork does not arrive — a demo without a network should
    /// still look like a media server rather than three grey rectangles.
    /// </summary>
    private async Task FetchArtworkAsync(string[] libraryIds)
    {
        foreach (var id in libraryIds)
        {
            await _admin.PostAsync(
                $"/Items/{id}/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=FullRefresh"
                + "&replaceAllMetadata=true&replaceAllImages=true",
                content: null);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(3_000);
            if (await AllFilmsHavePicturesAsync(libraryIds))
            {
                foreach (var id in libraryIds)
                {
                    await UseFilmPosterAsLibraryCoverAsync(id);
                }

                return;
            }
        }

        foreach (var (id, cover) in libraryIds.Zip(new[] { "parents", "kids", "family" }))
        {
            await SetLibraryCoverAsync(id, cover);
        }
    }

    /// <summary>
    /// Gives a library a picture from the film inside it. A library tile is a wide
    /// rectangle and a film poster is a tall one, so the poster arrives cropped through
    /// the middle — Toy Story becomes a patch of sky. The film's backdrop is already the
    /// right shape; the poster is the fallback for a film that has none.
    /// </summary>
    private async Task UseFilmPosterAsLibraryCoverAsync(string libraryId)
    {
        var items = JsonNode.Parse(await _admin.GetStringAsync(
            $"/Items?parentId={libraryId}&recursive=true&includeItemTypes=Movie&fields=BackdropImageTags"))!;
        var film = items["Items"]!.AsArray().FirstOrDefault();
        if (film is null)
        {
            return;
        }

        var id = film["Id"]!.GetValue<string>();
        var hasBackdrop = film["BackdropImageTags"]?.AsArray().Count > 0;

        var picture = await _admin.GetByteArrayAsync(
            hasBackdrop ? $"/Items/{id}/Images/Backdrop/0" : $"/Items/{id}/Images/Primary");

        var content = new StringContent(Convert.ToBase64String(picture));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var response = await _admin.PostAsync($"/Items/{libraryId}/Images/Primary", content);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> AllFilmsHavePicturesAsync(string[] libraryIds)
    {
        foreach (var id in libraryIds)
        {
            var items = JsonNode.Parse(await _admin.GetStringAsync(
                $"/Items?parentId={id}&recursive=true&includeItemTypes=Movie&fields=PrimaryImageAspectRatio"))!;
            var films = items["Items"]!.AsArray();

            if (films.Count == 0
                || films.Any(f => f!["ImageTags"]?["Primary"] is null))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Turns the metadata providers on for a library that was made without them.</summary>
    private async Task AllowInternetProvidersAsync(string libraryId, string name, string path)
    {
        var response = await _admin.PostAsJsonAsync("/Library/VirtualFolders/LibraryOptions", new
        {
            Id = libraryId,
            LibraryOptions = new
            {
                Enabled = true,
                EnableRealtimeMonitor = false,
                EnableInternetProviders = true,
                PathInfos = new[] { new { Path = path } },
            },
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Uploads one of the covers under tests/fixtures as a library's primary image.
    /// Jellyfin takes the image base64-encoded, with the media type as the content type.
    /// </summary>
    private async Task SetLibraryCoverAsync(string libraryId, string cover)
    {
        var path = Path.Combine(RepoRoot(), "tests", "fixtures", "library-covers", $"{cover}.jpg");
        var content = new StringContent(
            Convert.ToBase64String(await File.ReadAllBytesAsync(path)));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var response = await _admin.PostAsync($"/Items/{libraryId}/Images/Primary", content);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> CreateLibraryAsync(
        string name, string collectionType, string path, bool internetProviders = false)
    {
        var url = $"/Library/VirtualFolders?name={Uri.EscapeDataString(name)}"
            + $"&collectionType={collectionType}&paths={Uri.EscapeDataString(path)}&refreshLibrary=false";

        var response = await _admin.PostAsJsonAsync(url, new
        {
            LibraryOptions = new
            {
                Enabled = true,
                EnableRealtimeMonitor = false,
                // Off for the ordinary tests: they must not depend on somebody else's
                // service being up. The demo turns it on, because it wants the posters.
                EnableInternetProviders = internetProviders,
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

    /// <summary>One film per demo library, named so Jellyfin can identify it.</summary>
    private static readonly (string Library, string Film)[] DemoFilms =
    [
        ("parents", "Die Hard (1988)"),
        ("kids", "Toy Story (1995)"),
        ("family", "Home Alone (1990)"),
    ];

    /// <summary>
    /// A folder per library with a placeholder file each; no real media needed for the
    /// tests. The three the demo uses also get a film-shaped folder with a one-second
    /// black video in it, because Jellyfin only looks a title up — and only fetches a
    /// poster for it — once there is something it recognises as a film.
    /// </summary>
    private static string CreateMediaTree(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "tests", "runs", "e2e", "media");
        foreach (var library in new[] { "movies", "kids", "parents", "family" })
        {
            var dir = Path.Combine(root, library);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "placeholder.txt"), "e2e");
        }

        var blank = Path.Combine(repoRoot, "tests", "fixtures", "media", "blank.mp4");
        foreach (var (library, film) in DemoFilms)
        {
            var dir = Path.Combine(root, library, film);
            Directory.CreateDirectory(dir);
            File.Copy(blank, Path.Combine(dir, film + ".mp4"), overwrite: true);
        }

        return root;
    }

    /// <summary>
    /// The server image follows the Jellyfin.Controller reference, so the container always
    /// matches the ABI the plugin was built against.
    /// </summary>
    private static string ServerImageTag(string repoRoot)
    {
        // The compatibility matrix runs the same build against several servers.
        var pinned = Environment.GetEnvironmentVariable("E2E_JELLYFIN_TAG");
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            return pinned;
        }

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
