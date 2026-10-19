using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sso.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// How many group names the autocomplete remembers. The names come from the provider,
    /// so the list needs an end.
    /// </summary>
    public const int MaxSeenGroups = 500;

    public List<ProviderConfig> Providers { get; set; } = [];
    public List<GroupMappingConfig> GroupMappings { get; set; } = [];
    public List<LinkConfig> Links { get; set; } = [];
    public List<string> SeenGroups { get; set; } = [];

    /// <summary>
    /// When true, an SSO login never clears an existing administrator flag. The escape
    /// hatch for the case where a group rename at the IdP would otherwise demote the
    /// admins; library access is still synced declaratively.
    /// </summary>
    public bool PreserveAdminPermissions { get; set; }

    /// <summary>
    /// When true, signing out of Jellyfin also ends the session at the identity provider
    /// (RFC 7009 style RP-initiated logout), so the next login asks for credentials again.
    /// </summary>
    public bool SignOutOfIdentityProvider { get; set; }

    /// <summary>
    /// Whether the plugin writes a login button per enabled provider into Jellyfin's
    /// login disclaimer. Off for anyone who places their own button with the JavaScript
    /// Injector and does not want a second one underneath it.
    /// </summary>
    public bool AddLoginButtonToBranding { get; set; } = true;

    /// <summary>
    /// Whether the login button carries the logo the identity provider shows on its own
    /// login page, rather than a stock mark. The server fetches it once and caches it, so
    /// a visitor's browser never reaches the provider before they choose to sign in.
    /// </summary>
    public bool UseProviderLogos { get; set; } = true;

    /// <summary>
    /// Whether the plugin adds a script tag to jellyfin-web's index.html so the login
    /// button can sit inside the login form. Off by default: it rewrites a response that
    /// belongs to Jellyfin, which is a bigger step than writing the branding field.
    /// </summary>
    public bool InjectLoginButtonIntoForm { get; set; }

    /// <summary>
    /// Allow users to link their own Jellyfin account to an SSO identity from the
    /// self-service page, instead of an administrator doing it for them.
    /// </summary>
    public bool AllowSelfServiceLinking { get; set; } = true;

    /// <summary>
    /// Jellyfin users whose permissions are managed by hand. They can still log in via
    /// SSO, but their policy is left exactly as an administrator set it.
    /// </summary>
    public List<Guid> SyncExcludedUserIds { get; set; } = [];
}

public class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>OIDC issuer URL — used for discovery (/.well-known/openid-configuration).</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;
    public OidcClientType ClientType { get; set; } = OidcClientType.Public;

    /// <summary>
    /// Env var name (prefix "env:") or absolute file path (prefix "file:") containing
    /// the client_secret. Never the literal secret. Only used when ClientType=Confidential.
    /// </summary>
    public string? ConfidentialSecretRef { get; set; }

    /// <summary>
    /// Requested scopes. Starts empty on purpose: XmlSerializer appends saved items to a
    /// pre-filled list instead of replacing it, which duplicated the defaults on every
    /// config load. Use <see cref="EffectiveScopes"/> when building requests.
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Distinct configured scopes, or openid/profile/email when none are set.</summary>
    public IEnumerable<string> EffectiveScopes()
    {
        var scopes = Scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList();
        return scopes.Count > 0 ? scopes : ["openid", "profile", "email"];
    }

    /// <summary>Claim name for groups. Default "groups" works for Authentik + Authelia.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>When true, fetch groups from userinfo endpoint instead of the ID token.</summary>
    public bool GroupsFromUserInfo { get; set; }

    /// <summary>
    /// When true the login button is painted in the provider's own colour, with its mark
    /// in black or white on top. Off, the button looks like Jellyfin's other buttons and
    /// carries the provider's colour on its left edge alone.
    /// </summary>
    public bool BrandedLoginButton { get; set; }
}

public enum OidcClientType
{
    Public,
    Confidential,
}

public class GroupMappingConfig
{
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Library GUIDs (stable ItemId values from ILibraryManager.GetVirtualFolders()).
    /// Stored as strings to survive XML serialization round-trips cleanly.
    /// </summary>
    public List<string> LibraryIds { get; set; } = [];

    public bool IsAdministrator { get; set; }

    public bool EnableLiveTvAccess { get; set; }

    public bool EnableLiveTvManagement { get; set; }

    public bool EnableContentDownloading { get; set; }

    /// <summary>
    /// Highest parental rating this group may watch. Null means no limit; a user in
    /// several groups gets the most permissive of them.
    /// </summary>
    public int? MaxParentalRating { get; set; }
}

/// <summary>
/// Maps one OIDC identity (provider + sub) to one Jellyfin user.
/// Created by JIT provisioning or a manual admin action. Never auto-matched by username.
/// </summary>
public class LinkConfig
{
    public string ProviderId { get; set; } = string.Empty;
    public string Sub { get; set; } = string.Empty;
    public Guid JellyfinUserId { get; set; }
}
