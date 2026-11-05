using System.Diagnostics;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A real Tinyauth, as its own OIDC provider.
///
/// It is the odd one out in three ways, and each of them is a sentence in docs/tinyauth.md
/// that would otherwise be hearsay:
///
///   * It will not be a public client. The discovery document offers only
///     client_secret_basic and client_secret_post, so the plugin has to hold a secret —
///     the only provider in this suite that exercises ClientType=Confidential.
///   * It has no TLS listener of its own and refuses an issuer that is not https, so it
///     only runs behind a reverse proxy. This rig puts nginx in front of it, which is what
///     every Tinyauth deployment does anyway.
///   * Its local username-and-password users have no groups at all. Not an empty list in
///     the token: no groups claim, in the ID token or at userinfo, even when the groups
///     scope is requested and granted. The groups claim only carries anything for users
///     who came from LDAP or from an upstream provider.
///
/// So the group mappings are keyed on preferred_username here, which is the workaround the
/// guide recommends: a Tinyauth username becomes a group of one, and the mapping grants it
/// what that one person should see. The users are named after the suite's mappings for
/// exactly that reason — "media" is a person here, not a group.
/// </summary>
internal sealed class TinyauthProvider : IRealIdentityProvider
{
    private static readonly string Image =
        TestImages.Resolve("E2E_TINYAUTH_IMAGE", "ghcr.io/tinyauthapp/tinyauth:v5.2.0");

    private static readonly string ProxyImage =
        TestImages.Resolve("E2E_NGINX_IMAGE", "nginx:1.27-alpine");

    private const string Password = "tinyauth-test-password";

    /// <summary>
    /// Test-only, and never written to the plugin's configuration: the plugin is given the
    /// path of a file holding it, which is the only form it accepts.
    /// </summary>
    private const string ClientSecretValue = "ta-insecure-e2e-client-secret-at-least-32-chars";

    private readonly IContainer _tinyauth;
    private readonly IContainer _proxy;
    private readonly INetwork _network;

    private TinyauthProvider(
        IContainer tinyauth, IContainer proxy, INetwork network, string authority)
    {
        _tinyauth = tinyauth;
        _proxy = proxy;
        _network = network;
        Authority = authority;
    }

    public string Name => "Tinyauth";

    public string Authority { get; }

    public string ClientId => "e2e-jellyfin-client";

    /// <summary>
    /// Not "groups", which is empty for these users whatever the plugin asks for. The
    /// username is the only claim that can carry a per-person mapping.
    /// </summary>
    public string GroupsClaim => "preferred_username";

    /// <summary>
    /// The groups scope is requested even though nothing comes back under it: that is what
    /// an operator following Tinyauth's own documentation would configure, and the test
    /// asserting the claim is empty should be asserting it about that configuration.
    /// </summary>
    public string[] Scopes => ["openid", "profile", "email", "groups"];

    /// <summary>Tinyauth puts the profile claims in the ID token, so userinfo is not needed.</summary>
    public bool GroupsFromUserInfo => false;

    public bool Confidential => true;

    /// <summary>
    /// Where the secret is in the server container, written there by
    /// <see cref="JellyfinEnvironment.WriteServerFileAsync"/> before the provider card
    /// names it.
    /// </summary>
    public string? ConfidentialSecretRef => $"file:{SecretPathInServer}";

    internal const string SecretPathInServer = "/config/tinyauth-client-secret";

    internal static string Secret => ClientSecretValue;

    /// <summary>The suite's mapped groups, as usernames. See the class comment.</summary>
    public RealIdentityUser MemberUser { get; } = new("media", ["media"]);

    public RealIdentityUser AdminUser { get; } = new("jellyfin-admins", ["jellyfin-admins"]);

    public RealIdentityUser KidUser { get; } = new("kids", ["kids"]);

    /// <summary>
    /// Tinyauth derives its cookie domain from the issuer and refuses one that is not
    /// shaped like a domain — "localhost" is rejected outright — so the environment's own
    /// base URL is fine for Jellyfin and only the provider needs the dotted name.
    /// </summary>
    public string? ServerBaseUrl => null;

    public string LoginFormSelector => "input[name='username']";

    public static async Task<TinyauthProvider> StartAsync(
        TestCertificate certificate, string workDirectory, string redirectUri)
    {
        var port = TestCertificate.FreePort();
        var authority = $"https://{TestCertificate.HostName}:{port}";
        var directory = Path.Combine(workDirectory, "tinyauth");
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, "nginx.conf"), NginxConf(port));

        var network = new NetworkBuilder().Build();
        await network.CreateAsync();

        var tinyauth = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithNetworkAliases("tinyauth")
            .WithEnvironment("TINYAUTH_APPURL", authority)
            .WithEnvironment("TINYAUTH_AUTH_USERS", await UsersAsync())
            // nginx is the only thing that reaches it, and it is the thing that sets the
            // forwarded headers Tinyauth reads.
            .WithEnvironment("TINYAUTH_AUTH_TRUSTEDPROXIES", "0.0.0.0/0")
            .WithEnvironment("TINYAUTH_LABELPROVIDER", "none")
            .WithEnvironment("TINYAUTH_ANALYTICS_ENABLED", "false")
            .WithEnvironment("TINYAUTH_OIDC_CLIENTS_JELLYFIN_CLIENTID", "e2e-jellyfin-client")
            .WithEnvironment("TINYAUTH_OIDC_CLIENTS_JELLYFIN_CLIENTSECRET", ClientSecretValue)
            .WithEnvironment("TINYAUTH_OIDC_CLIENTS_JELLYFIN_TRUSTEDREDIRECTURIS", redirectUri)
            .WithEnvironment("TINYAUTH_OIDC_CLIENTS_JELLYFIN_NAME", "Jellyfin")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Starting server on"))
            .Build();

        await tinyauth.StartAsync();

        // Started second on purpose: nginx resolves its upstream once, at startup, and
        // exits if the name is not there yet.
        var proxy = new ContainerBuilder(ProxyImage)
            .WithNetwork(network)
            .WithPortBinding(port, port)
            .WithBindMount(Path.Combine(directory, "nginx.conf"), "/etc/nginx/nginx.conf", AccessMode.ReadOnly)
            .WithBindMount(certificate.CertificatePath, "/etc/nginx/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(certificate.PrivateKeyPath, "/etc/nginx/tls.key", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("start worker process"))
            .Build();

        await proxy.StartAsync();

        return new TinyauthProvider(tinyauth, proxy, network, authority);
    }

    public Task PrepareServerAsync(JellyfinEnvironment environment)
        => environment.WriteServerFileAsync(SecretPathInServer, ClientSecretValue);

    public async Task SignInAsync(IPage page, RealIdentityUser user)
    {
        await page.FillAsync("input[name='username']", user.Username);
        await page.FillAsync("input[name='password']", Password);
        await page.ClickAsync("button[type='submit']");

        // "Continue to Jellyfin?" — Tinyauth asks for consent the first time an account
        // authorizes a client, and remembers the answer afterwards. So it appears once per
        // person and not at all on the logins after that, which is why the wait is short
        // and a timeout is not a failure.
        var authorize = page.Locator("button:has-text('Authorize')");
        try
        {
            await authorize.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
            await authorize.ClickAsync();
        }
        catch (TimeoutException)
        {
            // Already consented, or the flow went straight back to Jellyfin.
        }
    }

    public async Task<string> DiagnosticsAsync()
    {
        var logs = await _tinyauth.GetLogsAsync();
        var proxy = await _proxy.GetLogsAsync();
        return $"tinyauth:\n{logs.Stdout}\n{logs.Stderr}\nnginx:\n{proxy.Stderr}";
    }

    public async ValueTask DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _tinyauth.DisposeAsync();
        await _network.DisposeAsync();
    }

    /// <summary>
    /// Tinyauth stores bcrypt digests and its own CLI is the only sensible way to produce
    /// one, so the image is asked for them before the server starts — the same bargain
    /// Authelia's argon2 hashes are made with.
    /// </summary>
    private static async Task<string> UsersAsync()
    {
        var users = await Task.WhenAll(
            HashAsync("media"), HashAsync("jellyfin-admins"), HashAsync("kids"));
        return string.Join(",", users);
    }

    private static Task<string> HashAsync(string username) => Task.Run(() =>
    {
        using var process = Process.Start(new ProcessStartInfo("docker")
        {
            Arguments = $"run --rm {Image} user create --username {username} --password {Password}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // The CLI prints the line inside a coloured YAML fragment.
        var entry = Regex.Match(output, $@"{Regex.Escape(username)}:\$2[aby]\$[^\s\x1b]+").Value;
        if (string.IsNullOrEmpty(entry))
        {
            throw new InvalidOperationException(
                $"Tinyauth's CLI returned no bcrypt digest for {username}: {output} "
                + process.StandardError.ReadToEnd());
        }

        return entry;
    });

    /// <summary>
    /// TLS termination and the forwarded headers, which is all Tinyauth needs in front of
    /// it. It reads X-Forwarded-Host to decide which app a request is for, so the header
    /// is set here rather than passed through from the client.
    /// </summary>
    private static string NginxConf(int port) => $$"""
        events {}

        # nginx only says it is listening at notice level, and the image's default error
        # log is warn and a file. The wait strategy needs that line, so both change.
        error_log /dev/stdout notice;

        http {
          server {
            listen {{port}} ssl;
            ssl_certificate     /etc/nginx/tls.crt;
            ssl_certificate_key /etc/nginx/tls.key;

            location / {
              proxy_pass http://tinyauth:3000;
              proxy_set_header Host $host;
              proxy_set_header X-Forwarded-Host $host;
              proxy_set_header X-Forwarded-Proto https;
              proxy_set_header X-Forwarded-For $remote_addr;
              proxy_set_header X-Real-IP $remote_addr;
            }
          }
        }
        """;
}
