using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A real Authentik: PostgreSQL, Redis, the server and the worker, with a small nginx in
/// front that terminates TLS using the suite's certificate. Authentik manages its own
/// certificates through its API and caches the brand that selects them, so proxying is
/// both simpler and deterministic — and authentik derives the issuer from the forwarded
/// host, which keeps the discovery document consistent.
///
/// Everything it needs — groups, users, the OAuth2 provider and the application — is
/// created through its API after startup.
///
/// This is the provider this plugin was written for, so the most important assertion is
/// simply that it still works unattended.
/// </summary>
internal sealed class AuthentikProvider : IRealIdentityProvider
{
    // Authentik publishes no immutable minor tags on ghcr beyond "stable"; the real-idp
    // job is separate so an upstream change here does not read as a plugin regression.
    private static readonly string Image =
        TestImages.Resolve("E2E_AUTHENTIK_IMAGE", "ghcr.io/goauthentik/server:2026.8.4");

    /// <summary>
    /// Also typed into the form by the demo recording, so it is internal rather than
    /// private. It belongs to a container that lives for the length of one test run.
    /// </summary>
    internal const string Password = "authentik-test-password";
    private const string BootstrapToken = "e2e-bootstrap-token-0123456789";
    private const string AppSlug = "jellyfin";

    /// <summary>
    /// A second application, identical to the first except that its provider has no
    /// signing key — the one mistake docs/authentik.md puts in bold. Created here rather
    /// than in the test because it costs two API calls on a container that is already
    /// running, and because a provider is not something a browser can configure.
    /// </summary>
    private const string UnsignedAppSlug = "jellyfin-unsigned";

    private readonly INetwork _network;
    private readonly IContainer[] _containers;

    private AuthentikProvider(INetwork network, IContainer[] containers, string baseUrl)
    {
        _network = network;
        _containers = containers;
        Authority = $"{baseUrl}/application/o/{AppSlug}/";
        UnsignedAuthority = $"{baseUrl}/application/o/{UnsignedAppSlug}/";
    }

    public string Name => "Authentik";

    public string Authority { get; }

    public string ClientId => "jellyfin-e2e-client";

    /// <summary>The issuer of the application whose provider was left without a signing key.</summary>
    internal string UnsignedAuthority { get; }

    /// <summary>The client of that same application.</summary>
    internal string UnsignedClientId => "jellyfin-e2e-unsigned";

    public string GroupsClaim => "groups";

    /// <summary>Authentik's default profile scope mapping already carries the groups.</summary>
    public string[] Scopes => ["openid", "profile", "email"];

    public bool GroupsFromUserInfo => false;

    public string? ServerBaseUrl => null;

    public RealIdentityUser MemberUser { get; } = new("authentik-member", ["media"]);

    public RealIdentityUser AdminUser { get; } = new("authentik-admin", ["jellyfin-admins"]);

    public RealIdentityUser KidUser { get; } = new("authentik-kid", ["kids"]);

    public static async Task<AuthentikProvider> StartAsync(
        TestCertificate certificate, string workDirectory, string redirectUri)
    {
        var port = TestCertificate.FreePort();
        var baseUrl = $"https://{TestCertificate.HostName}:{port}";

        var network = new NetworkBuilder().Build();
        await network.CreateAsync();

        var postgres = new ContainerBuilder("docker.io/library/postgres:16-alpine")
            .WithNetwork(network)
            .WithNetworkAliases("postgresql")
            .WithEnvironment("POSTGRES_DB", "authentik")
            .WithEnvironment("POSTGRES_USER", "authentik")
            .WithEnvironment("POSTGRES_PASSWORD", "authentik")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-U", "authentik"))
            .Build();

        var redis = new ContainerBuilder("docker.io/library/redis:7-alpine")
            .WithNetwork(network)
            .WithNetworkAliases("redis")
            .Build();

        await postgres.StartAsync();
        await redis.StartAsync();

        var server = Authentik("server").WithNetwork(network).WithNetworkAliases("authentik-server").Build();
        var worker = Authentik("worker").WithNetwork(network).Build();
        await server.StartAsync();
        await worker.StartAsync();

        var nginxDirectory = Path.Combine(workDirectory, "authentik-proxy");
        Directory.CreateDirectory(nginxDirectory);
        await File.WriteAllTextAsync(Path.Combine(nginxDirectory, "default.conf"), NginxConfig());

        var proxy = new ContainerBuilder("docker.io/library/nginx:alpine")
            .WithNetwork(network)
            .WithPortBinding(port, 443)
            .WithBindMount(Path.Combine(nginxDirectory, "default.conf"), "/etc/nginx/conf.d/default.conf", AccessMode.ReadOnly)
            .WithBindMount(certificate.CertificatePath, "/certs/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(certificate.PrivateKeyPath, "/certs/tls.key", AccessMode.ReadOnly)
            .Build();
        await proxy.StartAsync();

        var provider = new AuthentikProvider(network, [proxy, worker, server, redis, postgres], baseUrl);

        // The test process cannot resolve host.docker.internal — that name exists for the
        // server container and, through a resolver rule, for the browser.
        await provider.BootstrapAsync(redirectUri, $"https://localhost:{port}");
        return provider;

        static ContainerBuilder Authentik(string command) => new ContainerBuilder(Image)
            .WithCommand(command)
            .WithEnvironment("AUTHENTIK_SECRET_KEY", "e2e-secret-key-not-used-anywhere-else")
            .WithEnvironment("AUTHENTIK_REDIS__HOST", "redis")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", "postgresql")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", "authentik")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", "authentik")
            .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", "authentik")
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_PASSWORD", "akadmin-e2e-password")
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_TOKEN", BootstrapToken)
            .WithEnvironment("AUTHENTIK_BOOTSTRAP_EMAIL", "akadmin@example.com")
            .WithEnvironment("AUTHENTIK_DISABLE_UPDATE_CHECK", "true")
            .WithEnvironment("AUTHENTIK_DISABLE_STARTUP_ANALYTICS", "true")
            .WithEnvironment("AUTHENTIK_ERROR_REPORTING__ENABLED", "false")
            .WithEnvironment("AUTHENTIK_LOG_LEVEL", "warning");
    }

    /// <summary>
    /// Passes the host through unchanged: authentik builds the issuer and every endpoint in
    /// its discovery document from the forwarded host, so this is what keeps the document
    /// pointing at a name both the server container and the browser can reach.
    /// </summary>
    private static string NginxConfig() => """
        server {
            listen 443 ssl;
            server_name _;

            ssl_certificate     /certs/tls.crt;
            ssl_certificate_key /certs/tls.key;

            location / {
                proxy_pass http://authentik-server:9000;
                # $http_host keeps the port; $host drops it, and authentik would then
                # advertise endpoints without it in its discovery document.
                proxy_set_header Host $http_host;
                proxy_set_header X-Forwarded-Proto https;
                proxy_set_header X-Forwarded-Host $http_host;
                proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            }
        }
        """;

    /// <summary>
    /// Authentik's login is a flow with two stages, identification and then the password.
    /// Each stage has to be submitted with its button; pressing Enter leaves the flow
    /// sitting on the stage.
    /// </summary>
    public string LoginFormSelector => "input[name='uidField']";

    /// <summary>
    /// Authentik asks for the name and the password in two stages, so the recording waits
    /// for the second form the same way the test does — it just types instead of filling.
    /// </summary>
    public async Task SignInForTheRecordingAsync(IPage page, RealIdentityUser user)
    {
        var username = page.Locator("input[name='uidField']");
        await page.WaitForTimeoutAsync(800);
        await username.PressSequentiallyAsync(
            user.Username, new LocatorPressSequentiallyOptions { Delay = 55 });
        await page.WaitForTimeoutAsync(350);
        await page.ClickAsync("button[type='submit']");

        await username.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Detached,
            Timeout = 60_000,
        });

        var password = page.Locator("input[name='password']");
        await password.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 60_000,
        });
        await page.WaitForTimeoutAsync(700);

        // The field masks it, and it belongs to a container that is thrown away with the
        // test run — so the recording can show it being typed like any other password.
        await password.PressSequentiallyAsync(
            Password, new LocatorPressSequentiallyOptions { Delay = 45 });
        await page.WaitForTimeoutAsync(300);
        await page.ClickAsync("button[type='submit']");
    }

    public async Task SignInAsync(IPage page, RealIdentityUser user)
    {
        var username = page.Locator("input[name='uidField']");
        await username.FillAsync(user.Username);
        await page.ClickAsync("button[type='submit']");

        // The stages swap out the whole form, so the password field has to be waited for:
        // filling the one the identification stage rendered writes into a detached element
        // and the flow submits an empty password.
        await username.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Detached,
            Timeout = 30_000,
        });

        var password = page.Locator("input[name='password']");
        await password.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 30_000,
        });
        await password.FillAsync(Password);
        await page.ClickAsync("button[type='submit']");
    }

    public async Task<string> DiagnosticsAsync()
    {
        var logs = await _containers[2].GetLogsAsync();
        return string.Join(
            " / ",
            (logs.Stdout + logs.Stderr).Split('\n')
                .Where(l => l.Contains("warning", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("error", StringComparison.OrdinalIgnoreCase))
                .TakeLast(4));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        await _network.DeleteAsync();
    }

    // ─── API bootstrap ────────────────────────────────────────────────────────

    private async Task BootstrapAsync(string redirectUri, string baseUrl)
    {
        using var api = JellyfinEnvironment.TrustingClient();
        api.BaseAddress = new Uri($"{baseUrl}/api/v3/");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", BootstrapToken);

        await WaitUntilReadyAsync(api);

        var groups = new Dictionary<string, string>();
        foreach (var group in new[] { "media", "jellyfin-admins", "kids" })
        {
            groups[group] = await PostAsync(api, "core/groups/", new JsonObject { ["name"] = group });
        }

        foreach (var user in new[] { MemberUser, AdminUser, KidUser })
        {
            var userPk = await PostAsync(api, "core/users/", new JsonObject
            {
                ["username"] = user.Username,
                ["name"] = user.Username,
                ["is_active"] = true,
                ["path"] = "users",
                ["groups"] = new JsonArray(user.Groups.Select(g => (JsonNode)groups[g]!).ToArray()),
            });

            (await api.PostAsJsonAsync($"core/users/{userPk}/set_password/", new { password = Password }))
                .EnsureSuccessStatusCode();
        }

        // Without a signing key authentik does not sign ID tokens with RS256, and the
        // plugin only trusts keys the JWKS endpoint publishes.
        var signingKey = (await GetAsync(api, "crypto/certificatekeypairs/?has_key=true"))["results"]!
            .AsArray()[0]!["pk"]!.GetValue<string>();

        var authorizationFlow = await FlowAsync(api, "default-provider-authorization-implicit-consent");
        var invalidationFlow = await FlowAsync(api, "default-provider-invalidation-flow");

        var mappings = await ScopeMappingsAsync(api);

        var providerPk = await PostAsync(api, "providers/oauth2/", new JsonObject
        {
            ["name"] = "jellyfin-e2e",
            ["client_type"] = "public",
            ["client_id"] = ClientId,
            ["authorization_flow"] = authorizationFlow,
            ["invalidation_flow"] = invalidationFlow,
            ["property_mappings"] = ScopeMappingArray(mappings),
            ["include_claims_in_id_token"] = true,
            ["signing_key"] = signingKey,
            // Recent authentik versions gate the grant types per provider, and a provider
            // created through the API starts with none of them allowed.
            ["grant_types"] = new JsonArray { "authorization_code" },
            ["redirect_uris"] = new JsonArray
            {
                new JsonObject { ["matching_mode"] = "strict", ["url"] = redirectUri },
            },
        });

        await PostAsync(api, "core/applications/", new JsonObject
        {
            ["name"] = "Jellyfin",
            ["slug"] = AppSlug,
            ["provider"] = int.Parse(providerPk, System.Globalization.CultureInfo.InvariantCulture),
        });

        // The same provider with the signing key left out, so a test can walk the login
        // that docs/authentik.md warns about instead of taking the warning on trust.
        var unsignedPk = await PostAsync(api, "providers/oauth2/", new JsonObject
        {
            ["name"] = "jellyfin-e2e-unsigned",
            ["client_type"] = "public",
            ["client_id"] = UnsignedClientId,
            ["authorization_flow"] = authorizationFlow,
            ["invalidation_flow"] = invalidationFlow,
            ["property_mappings"] = ScopeMappingArray(mappings),
            ["include_claims_in_id_token"] = true,
            ["grant_types"] = new JsonArray { "authorization_code" },
            ["redirect_uris"] = new JsonArray
            {
                new JsonObject { ["matching_mode"] = "strict", ["url"] = redirectUri },
            },
        });

        await PostAsync(api, "core/applications/", new JsonObject
        {
            ["name"] = "Jellyfin (no signing key)",
            ["slug"] = UnsignedAppSlug,
            ["provider"] = int.Parse(unsignedPk, System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// Waits until the API answers with the bootstrapped token and the default brand
    /// exists: the worker creates both, a while after the server starts serving.
    /// </summary>
    private static async Task WaitUntilReadyAsync(HttpClient api)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await api.GetAsync("core/brands/");
                if (response.IsSuccessStatusCode)
                {
                    var brands = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["results"]!.AsArray();
                    if (brands.Count > 0)
                    {
                        return;
                    }

                    last = new InvalidOperationException("no brands yet");
                }
                else
                {
                    last = new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception error)
            {
                last = error;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        throw new InvalidOperationException($"Authentik never became ready: {last?.Message}");
    }

    /// <summary>
    /// The property mappings for the scopes this provider asks for. Like the flows, they
    /// are imported in the background, and a provider built from a half-imported set asks
    /// for fewer scopes than it needs — which surfaces much later as a refused userinfo
    /// call rather than as a missing mapping.
    /// </summary>
    /// <summary>
    /// A fresh array of the mapping keys. A JsonNode belongs to one parent, so the two
    /// providers created above cannot be handed the same array.
    /// </summary>
    private static JsonArray ScopeMappingArray(string[] mappings)
        => new(mappings.Select(pk => (JsonNode)pk!).ToArray());

    private async Task<string[]> ScopeMappingsAsync(HttpClient api)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (true)
        {
            var all = (await GetAsync(api, "propertymappings/provider/scope/?page_size=100"))["results"]!
                .AsArray();
            var wanted = all
                .Where(m => Scopes.Contains(m!["scope_name"]!.GetValue<string>()))
                .Select(m => m!["pk"]!.GetValue<string>())
                .ToArray();

            if (wanted.Length == Scopes.Length)
            {
                return wanted;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"Authentik imported only {wanted.Length} of the {Scopes.Length} scope mappings.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Looks up a built-in flow, waiting for it: the worker imports the default blueprints
    /// in the background, so they can still be missing when the API already answers.
    /// </summary>
    private static async Task<string> FlowAsync(HttpClient api, string slug)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (true)
        {
            var flows = (await GetAsync(api, $"flows/instances/?slug={slug}"))["results"]!.AsArray();
            if (flows.Count > 0)
            {
                return flows[0]!["pk"]!.GetValue<string>();
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new InvalidOperationException($"Authentik never imported the flow '{slug}'.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<JsonNode> GetAsync(HttpClient api, string path)
        => JsonNode.Parse(await api.GetStringAsync(path))!;

    /// <summary>Posts a creation request and returns the new object's primary key.</summary>
    private static async Task<string> PostAsync(HttpClient api, string path, JsonNode body)
    {
        var response = await api.PostAsJsonAsync(path, body);
        var payload = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path} failed: {(int)response.StatusCode} {payload}");
        }

        var created = JsonNode.Parse(payload)!;
        return (created["pk"] ?? created["brand_uuid"])!.ToString();
    }
}
