using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sso.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// How many group names the autocomplete remembers. The names come from the provider,
    /// so the list needs an end.
    /// </summary>
    public const int MaxSeenGroups = 500;

    /// <summary>
    /// The default for <see cref="LoginRequestsPerMinute"/>. A login spends two of those
    /// requests — the start and the provider's callback — or three with Quick Connect, so
    /// sixty is twenty logins a minute from one address.
    /// </summary>
    public const int DefaultLoginRequestsPerMinute = 60;

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
    /// Refuse a login that matches no group mapping, instead of creating an account with
    /// no libraries. Off by default, because the provider is the usual place to decide who
    /// may reach Jellyfin at all — switching it on adds a second lock, inside the plugin,
    /// for the case where the provider was the thing configured wrongly.
    ///
    /// It applies to accounts that already exist as well: once the last mapped group is
    /// gone, so is the login. Losing one of several changes what the account can see and
    /// nothing else. Accounts in <see cref="SyncExcludedUserIds"/> are exempt, and local
    /// passwords are untouched, so an operator locked out by a wrong claim name can still
    /// sign in to their own server.
    /// </summary>
    public bool RequireMappedGroup { get; set; }

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
    /// Whether a profile picture may be fetched from an address that is not public and not
    /// the provider's own host — loopback, a private range, a container name.
    ///
    /// Off, because on many identity providers the picture is an attribute the user can
    /// edit: the address is theirs, the request is the server's, and what comes back is
    /// readable as their profile picture. The provider's own host is always allowed, which
    /// is where avatars normally live. Turn this on for a setup that serves them from a
    /// second machine on the same network.
    /// </summary>
    public bool AllowPrivateNetworkPictures { get; set; }

    /// <summary>
    /// Jellyfin users whose permissions are managed by hand. They can still log in via
    /// SSO, but their policy is left exactly as an administrator set it.
    /// </summary>
    public List<Guid> SyncExcludedUserIds { get; set; } = [];

    /// <summary>
    /// How many requests one network address may make per minute to the parts of the login
    /// that need no Jellyfin token: starting a login, the provider's callback, a Quick
    /// Connect approval and a back-channel logout. Everyone behind the same NAT counts as
    /// one address. Zero turns the limit off.
    /// </summary>
    public int LoginRequestsPerMinute { get; set; } = DefaultLoginRequestsPerMinute;

    /// <summary>
    /// Whether the picture the provider publishes becomes the Jellyfin profile picture,
    /// refreshed at every login. Off by default: an avatar somebody chose here is theirs,
    /// and this setting replaces it with the provider's. Users in
    /// <see cref="SyncExcludedUserIds"/> keep whatever they have either way.
    /// </summary>
    public bool SyncProfilePictures { get; set; }
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

    /// <summary>
    /// Claim name for groups. Default "groups" works for Authentik + Authelia.
    ///
    /// A name with dots in it is first looked for as it stands — claim types such as
    /// `urn:zitadel:iam:org:project:roles` are real — and then read as a path into a
    /// nested claim, which reaches Keycloak's own `realm_access.roles` and Entra ID's
    /// `resource_access.&lt;client&gt;.roles` without a mapper at the provider.
    /// </summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>When true, fetch groups from userinfo endpoint instead of the ID token.</summary>
    public bool GroupsFromUserInfo { get; set; }

    /// <summary>
    /// Claim holding the address of the person's picture. A path works here too, the same
    /// way it does for <see cref="GroupsClaim"/>. Only read while profile picture sync is
    /// on.
    /// </summary>
    public string PictureClaim { get; set; } = "picture";

    /// <summary>
    /// Whether the plugin accepts back-channel logout notifications from this provider at
    /// <c>/sso/oidc/backchannel-logout/{id}</c>. On, because the endpoint acts only on a
    /// logout token the provider signed, and a provider only posts one once an
    /// administrator has registered the URL with it.
    /// </summary>
    public bool AcceptBackChannelLogout { get; set; } = true;

    /// <summary>
    /// The signing algorithms a token from this provider may carry. Empty — the default,
    /// and what every release before this one did — means all of the asymmetric ones.
    ///
    /// An operator who knows their provider signs with RS256 can say so, and then a token
    /// signed ES256 by another key out of the same published set is refused. That is the
    /// difference between accepting whatever the provider felt like signing with and
    /// accepting what it was configured to send: without it, the effective strength is
    /// that of the weakest key in the provider's set, chosen by whoever mints the token
    /// rather than by the party checking it.
    ///
    /// Not a forgery defence — every key in that set belongs to the provider. It is what
    /// the OpenID Connect conformance suite asks for in
    /// <c>oidcc-client-test-rp-backchannel-rpinitlogout-wrong-alg</c>, where a client is
    /// expected to refuse an algorithm it was not told to expect. The names are the JWS
    /// ones, <c>RS256</c> and friends; see
    /// <see cref="Oidc.OidcClientFactory.SigningAlgorithmsFor"/> for what a name outside
    /// the asymmetric set does, which is nothing.
    /// </summary>
    public List<string> SigningAlgorithms { get; set; } = [];

    /// <summary>
    /// When true the login button is painted in the provider's own colour, with its mark
    /// in black or white on top. Off, the button looks like Jellyfin's other buttons and
    /// carries the provider's colour on its left edge alone.
    /// </summary>
    public bool BrandedLoginButton { get; set; }

    /// <summary>
    /// The colour that button is painted in, as #rgb or #rrggbb. Empty means the colour
    /// the provider is recognised by. Anything that is not a hex colour is ignored: the
    /// value is written into Jellyfin's custom CSS.
    /// </summary>
    public string ButtonColour { get; set; } = string.Empty;
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
    /// Whether this group may delete media from the server. Off by default, and worth
    /// leaving off for everybody who is not an administrator: a deletion here removes the
    /// file, not a database row.
    /// </summary>
    public bool EnableContentDeletion { get; set; }

    /// <summary>Whether this group may create and edit collections.</summary>
    public bool EnableCollectionManagement { get; set; }

    /// <summary>Whether this group may download and edit subtitles.</summary>
    public bool EnableSubtitleManagement { get; set; }

    /// <summary>Whether this group may download and edit lyrics.</summary>
    public bool EnableLyricManagement { get; set; }

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
