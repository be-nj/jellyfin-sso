using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// A real Keycloak, imported from a realm file at startup. Keycloak does not send a
/// groups claim unless a group membership mapper says so, which is the single most common
/// reason this plugin sees no groups — so the realm here configures one, and the tests
/// prove the claim actually arrives.
/// </summary>
internal sealed class KeycloakProvider : IRealIdentityProvider
{
    private const string Realm = "jellyfin";
    private const string Password = "keycloak-test-password";

    private static readonly string Image =
        TestImages.Resolve("E2E_KEYCLOAK_IMAGE", "quay.io/keycloak/keycloak:26.5");

    /// <summary>
    /// The same realm, configured the two ways docs/keycloak.md warns against, so the
    /// warnings can be walked instead of trusted: one client whose group mapper sends the
    /// full path, and one with no group mapper at all.
    /// </summary>
    internal const string FullPathClientId = "jellyfin-full-path";

    /// <summary>A client with no group mapper, left with whatever Keycloak sends by default.</summary>
    internal const string NoMapperClientId = "jellyfin-no-mapper";

    /// <summary>
    /// A client with no group mapper but with the realm-roles mapper switched on for the
    /// ID token, which is the arrangement docs/keycloak.md describes as the alternative to
    /// a group mapper.
    /// </summary>
    internal const string RealmRolesClientId = "jellyfin-realm-roles";

    private readonly IContainer _container;

    private KeycloakProvider(IContainer container, int port)
    {
        _container = container;
        Authority = $"https://{TestCertificate.HostName}:{port}/realms/{Realm}";
    }

    public string Name => "Keycloak";

    public string Authority { get; }

    public string ClientId => "jellyfin";

    public string GroupsClaim => "groups";

    // Keycloak has no "groups" scope unless somebody creates one, and asking for an unknown
    // scope fails the whole authorization. The group mapper puts the claim in the ID token.
    public string[] Scopes => ["openid", "profile", "email"];

    public bool GroupsFromUserInfo => false;

    public string? ServerBaseUrl => null;

    /// <summary>Users and groups come from the realm import, so they are known up front.</summary>
    public RealIdentityUser MemberUser { get; } = new("kc-member", ["media"]);

    public RealIdentityUser AdminUser { get; } = new("kc-admin", ["jellyfin-admins"]);

    public RealIdentityUser KidUser { get; } = new("kc-kid", ["kids"]);

    public static async Task<KeycloakProvider> StartAsync(TestCertificate certificate, string workDirectory)
    {
        var port = TestCertificate.FreePort();
        var importDirectory = Path.Combine(workDirectory, "keycloak-import");
        Directory.CreateDirectory(importDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(importDirectory, "realm.json"),
            RealmJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var container = new ContainerBuilder(Image)
            .WithPortBinding(port, 8443)
            .WithBindMount(importDirectory, "/opt/keycloak/data/import", AccessMode.ReadOnly)
            .WithBindMount(certificate.CertificatePath, "/etc/keycloak-tls/tls.crt", AccessMode.ReadOnly)
            .WithBindMount(certificate.PrivateKeyPath, "/etc/keycloak-tls/tls.key", AccessMode.ReadOnly)
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
            .WithEnvironment("KC_HTTPS_CERTIFICATE_FILE", "/etc/keycloak-tls/tls.crt")
            .WithEnvironment("KC_HTTPS_CERTIFICATE_KEY_FILE", "/etc/keycloak-tls/tls.key")
            // One name for everybody: the issuer and every endpoint in the discovery
            // document must match what the plugin validates and what the browser can open.
            .WithEnvironment("KC_HOSTNAME", $"https://{TestCertificate.HostName}:{port}")
            .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "false")
            .WithCommand("start-dev", "--import-realm")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("started in"))
            .Build();

        await container.StartAsync();
        return new KeycloakProvider(container, port);
    }

    /// <summary>Keycloak's login form.</summary>
    public string LoginFormSelector => "#username";

    public async Task SignInAsync(IPage page, RealIdentityUser user)
    {
        await page.FillAsync("#username", user.Username);
        await page.FillAsync("#password", Password);
        await page.ClickAsync("#kc-login");
    }

    /// <summary>One form with both fields, typed rather than filled.</summary>
    public async Task SignInForTheRecordingAsync(IPage page, RealIdentityUser user)
    {
        await page.WaitForTimeoutAsync(800);
        await page.Locator("#username").PressSequentiallyAsync(
            user.Username, new LocatorPressSequentiallyOptions { Delay = 55 });
        await page.WaitForTimeoutAsync(300);

        // The password belongs to a container that is thrown away with the test run.
        await page.Locator("#password").PressSequentiallyAsync(
            Password, new LocatorPressSequentiallyOptions { Delay = 45 });
        await page.WaitForTimeoutAsync(350);
        await page.ClickAsync("#kc-login");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static JsonNode RealmJson() => new JsonObject
    {
        ["realm"] = Realm,
        // Keycloak prints the realm's display name across the top of its login page, and
        // falls back to the realm's own name. The realm is called "jellyfin" because that
        // is what it is for — which made Keycloak's page say JELLYFIN in Keycloak's own
        // letters, so the recording looked as if the login had never left Jellyfin.
        ["displayName"] = "Keycloak",
        ["enabled"] = true,
        ["sslRequired"] = "none",
        ["groups"] = new JsonArray
        {
            new JsonObject { ["name"] = "media" },
            new JsonObject { ["name"] = "jellyfin-admins" },
            new JsonObject { ["name"] = "kids" },
        },
        // Realm roles with the same names as the groups. docs/keycloak.md says a client
        // without the group mapper still sends these, nested as realm_access.roles, and
        // that typing that path into the plugin works — a claim no test held.
        ["roles"] = new JsonObject
        {
            ["realm"] = new JsonArray
            {
                new JsonObject { ["name"] = "media" },
                new JsonObject { ["name"] = "jellyfin-admins" },
                new JsonObject { ["name"] = "kids" },
            },
        },
        ["clients"] = new JsonArray
        {
            Client("jellyfin", GroupMapper(fullPath: false)),
            // "Full group path on" is the second warning in the guide: the claim then
            // reads "/media", and a mapping that says "media" matches nothing.
            Client(FullPathClientId, GroupMapper(fullPath: true)),
            // No mapper at all, which is what a client gets when somebody follows a
            // guide written for another provider.
            Client(NoMapperClientId, null),
            // Realm roles instead of groups, in the ID token, which Keycloak's own
            // built-in mapper does not do until somebody switches it on.
            Client(RealmRolesClientId, RealmRolesMapper()),
        },
        ["users"] = new JsonArray
        {
            User("kc-member", "media"),
            User("kc-admin", "jellyfin-admins"),
            User("kc-kid", "kids"),
        },
    };

    /// <summary>
    /// A public client with PKCE, as docs/keycloak.md's first step describes, carrying the
    /// group mapper it is given — or none, which is a client Keycloak leaves to its
    /// default scopes.
    /// </summary>
    private static JsonObject Client(string clientId, JsonObject? groupMapper)
    {
        var client = new JsonObject
        {
            ["clientId"] = clientId,
            ["enabled"] = true,
            ["publicClient"] = true,
            ["standardFlowEnabled"] = true,
            ["directAccessGrantsEnabled"] = false,
            // A test realm: the callback port is only known once Jellyfin is up.
            ["redirectUris"] = new JsonArray { "*" },
            ["webOrigins"] = new JsonArray { "*" },
            ["attributes"] = new JsonObject
            {
                ["pkce.code.challenge.method"] = "S256",
            },
        };

        if (groupMapper is not null)
        {
            client["protocolMappers"] = new JsonArray { groupMapper };
        }

        return client;
    }

    /// <summary>
    /// The Group Membership mapper from step 2 of the guide. <paramref name="fullPath"/>
    /// is the switch the guide tells the reader to turn off, because with it on the claim
    /// reads "/media" and a mapping has to spell it that way.
    /// </summary>
    private static JsonObject GroupMapper(bool fullPath) => new()
    {
        ["name"] = "groups",
        ["protocol"] = "openid-connect",
        ["protocolMapper"] = "oidc-group-membership-mapper",
        ["config"] = new JsonObject
        {
            ["claim.name"] = "groups",
            ["full.path"] = fullPath ? "true" : "false",
            ["id.token.claim"] = "true",
            ["access.token.claim"] = "true",
            ["userinfo.token.claim"] = "true",
        },
    };

    /// <summary>
    /// Keycloak's own realm-roles mapper, with "Add to ID token" on. It ships in the
    /// built-in "roles" client scope with that switch off, which is why a client without
    /// a group mapper sends this plugin nothing it can use: realm_access reaches the
    /// access token, and the plugin reads the ID token.
    /// </summary>
    private static JsonObject RealmRolesMapper() => new()
    {
        ["name"] = "realm roles",
        ["protocol"] = "openid-connect",
        ["protocolMapper"] = "oidc-usermodel-realm-role-mapper",
        ["config"] = new JsonObject
        {
            // A dot in the name is a nesting instruction, so this arrives as
            // { "realm_access": { "roles": [...] } } — the shape the guide names.
            ["claim.name"] = "realm_access.roles",
            ["jsonType.label"] = "String",
            ["multivalued"] = "true",
            ["id.token.claim"] = "true",
            ["access.token.claim"] = "true",
            ["userinfo.token.claim"] = "false",
        },
    };

    private static JsonObject User(string username, string group) => new()
    {
        ["username"] = username,
        ["enabled"] = true,
        ["emailVerified"] = true,
        ["email"] = $"{username}@example.com",
        // Keycloak interrupts the login with "Update Account Information" when a profile
        // field is missing, which no automated login can answer.
        ["firstName"] = "Test",
        ["lastName"] = "User",
        ["requiredActions"] = new JsonArray(),
        ["credentials"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "password",
                ["value"] = Password,
                ["temporary"] = false,
            },
        },
        ["groups"] = new JsonArray { $"/{group}" },
        // The same name as a realm role, for the client that has no group mapper.
        ["realmRoles"] = new JsonArray { group },
    };
}
