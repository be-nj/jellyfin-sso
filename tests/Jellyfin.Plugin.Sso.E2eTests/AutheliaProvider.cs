using System.Diagnostics;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A real Authelia. Its ID token carries only the standard claims, so the groups have to
/// come from the userinfo endpoint — which makes this the one test that exercises the
/// plugin's GroupsFromUserInfo path against a real provider.
/// </summary>
internal sealed class AutheliaProvider : IRealIdentityProvider
{
    private static readonly string Image =
        TestImages.Resolve("E2E_AUTHELIA_IMAGE", "ghcr.io/authelia/authelia:4.39");

    private const string Password = "authelia-test-password";
    private const string Secret = "insecure-e2e-secret-at-least-32-chars";

    private readonly IContainer _container;

    private AutheliaProvider(IContainer container, string authority, string serverBaseUrl)
    {
        _container = container;
        Authority = authority;
        ServerBaseUrl = serverBaseUrl;
    }

    public string Name => "Authelia";

    public string Authority { get; }

    public string ClientId => "jellyfin";

    public string GroupsClaim => "groups";

    public string[] Scopes => ["openid", "profile", "email", "groups"];

    /// <summary>Authelia keeps the groups out of the ID token; the plugin has to ask userinfo.</summary>
    public bool GroupsFromUserInfo => true;

    /// <summary>
    /// Authelia rejects a plain-HTTP redirect URI for a public client unless the host ends
    /// in "localhost". Chromium resolves any *.localhost name to loopback by itself, so
    /// the browser reaches the published port under a name Authelia accepts.
    /// </summary>
    public string? ServerBaseUrl { get; }

    public RealIdentityUser MemberUser { get; } = new("authelia-member", ["media"]);

    public RealIdentityUser AdminUser { get; } = new("authelia-admin", ["jellyfin-admins"]);

    public RealIdentityUser KidUser { get; } = new("authelia-kid", ["kids"]);

    public static async Task<AutheliaProvider> StartAsync(
        TestCertificate certificate, string workDirectory, string serverBaseUrl, string providerId)
    {
        var redirectUri = $"{serverBaseUrl}/sso/oidc/callback/{providerId}";
        var port = TestCertificate.FreePort();
        var authority = $"https://{TestCertificate.HostName}:{port}";
        var configDirectory = Path.Combine(workDirectory, "authelia");
        Directory.CreateDirectory(configDirectory);

        var passwordHash = HashPassword();
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "users.yml"), UsersYaml(passwordHash));
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "configuration.yml"),
            ConfigurationYaml(authority, redirectUri, await File.ReadAllTextAsync(certificate.PrivateKeyPath)));

        var container = new ContainerBuilder(Image)
            .WithPortBinding(port, 9091)
            .WithBindMount(Path.Combine(configDirectory, "configuration.yml"), "/config/configuration.yml", AccessMode.ReadOnly)
            .WithBindMount(Path.Combine(configDirectory, "users.yml"), "/config/users.yml", AccessMode.ReadOnly)
            .WithBindMount(certificate.CertificatePath, "/config/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(certificate.PrivateKeyPath, "/config/tls.key", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening for TLS connections"))
            .Build();

        await container.StartAsync();
        return new AutheliaProvider(container, authority, serverBaseUrl);
    }

    /// <summary>Authelia's sign-in portal.</summary>
    public string LoginFormSelector => "#username-textfield";

    public async Task SignInAsync(IPage page, RealIdentityUser user)
    {
        await page.FillAsync("#username-textfield", user.Username);
        await page.FillAsync("#password-textfield", Password);
        await page.ClickAsync("#sign-in-button");
    }

    /// <summary>One form with both fields, typed rather than filled.</summary>
    public async Task SignInForTheRecordingAsync(IPage page, RealIdentityUser user)
    {
        await page.WaitForTimeoutAsync(800);
        await page.Locator("#username-textfield").PressSequentiallyAsync(
            user.Username, new LocatorPressSequentiallyOptions { Delay = 55 });
        await page.WaitForTimeoutAsync(300);

        // The password belongs to a container that is thrown away with the test run.
        await page.Locator("#password-textfield").PressSequentiallyAsync(
            Password, new LocatorPressSequentiallyOptions { Delay = 45 });
        await page.WaitForTimeoutAsync(350);
        await page.ClickAsync("#sign-in-button");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Authelia's file backend stores argon2id digests, and its own CLI is the only
    /// sensible way to produce one, so the image is asked for it before the server starts.
    /// </summary>
    private static string HashPassword()
    {
        using var process = Process.Start(new ProcessStartInfo("docker")
        {
            Arguments = $"run --rm --entrypoint authelia {Image} "
                + $"crypto hash generate argon2 --password {Password}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var digest = Regex.Match(output, @"\$argon2id\$[^\s]+").Value;
        if (string.IsNullOrEmpty(digest))
        {
            throw new InvalidOperationException(
                $"Authelia's CLI returned no argon2 digest: {output} {process.StandardError.ReadToEnd()}");
        }

        return digest;
    }

    private static string UsersYaml(string passwordHash) => $"""
        users:
          authelia-member:
            disabled: false
            displayname: 'Authelia Member'
            password: '{passwordHash}'
            email: 'member@example.com'
            groups:
              - 'media'
          authelia-admin:
            disabled: false
            displayname: 'Authelia Admin'
            password: '{passwordHash}'
            email: 'admin@example.com'
            groups:
              - 'jellyfin-admins'
          authelia-kid:
            disabled: false
            displayname: 'Authelia Kid'
            password: '{passwordHash}'
            email: 'kid@example.com'
            groups:
              - 'kids'
        """;

    private static string ConfigurationYaml(string authority, string redirectUri, string signingKeyPem)
    {
        // The key is indented into the YAML block scalar.
        var indentedKey = string.Join(
            "\n", signingKeyPem.TrimEnd().Split('\n').Select(line => "            " + line.TrimEnd()));

        return $"""
        theme: 'light'

        server:
          address: 'tcp://0.0.0.0:9091'
          tls:
            certificate: '/config/tls.crt'
            key: '/config/tls.key'

        log:
          level: 'info'

        identity_validation:
          reset_password:
            jwt_secret: '{Secret}'

        authentication_backend:
          password_reset:
            disable: true
          file:
            path: '/config/users.yml'

        # A test deployment: one factor is enough, there is no second factor configured.
        access_control:
          default_policy: 'one_factor'

        session:
          secret: '{Secret}'
          cookies:
            - domain: '{TestCertificate.HostName}'
              authelia_url: '{authority}'

        storage:
          encryption_key: '{Secret}'
          local:
            path: '/config/db.sqlite3'

        notifier:
          disable_startup_check: true
          filesystem:
            filename: '/config/notification.txt'

        identity_providers:
          oidc:
            hmac_secret: '{Secret}'
            enforce_pkce: 'public_clients_only'
            jwks:
              - key_id: 'e2e'
                algorithm: 'RS256'
                use: 'sig'
                key: |
        {indentedKey}
            clients:
              - client_id: 'jellyfin'
                client_name: 'Jellyfin'
                public: true
                # One factor, and no consent screen: an automated login cannot answer either.
                authorization_policy: 'one_factor'
                consent_mode: 'implicit'
                require_pkce: true
                pkce_challenge_method: 'S256'
                token_endpoint_auth_method: 'none'
                userinfo_signed_response_alg: 'none'
                redirect_uris:
                  - '{redirectUri}'
                scopes:
                  - 'openid'
                  - 'profile'
                  - 'email'
                  - 'groups'
                response_types:
                  - 'code'
                grant_types:
                  - 'authorization_code'
        """;
    }
}
