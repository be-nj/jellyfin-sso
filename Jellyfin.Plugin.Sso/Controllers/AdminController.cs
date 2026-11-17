using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// The API behind the plugin's configuration page. Every endpoint requires an
/// administrator; the page itself is served by Jellyfin, not from here.
/// </summary>
[ApiController]
[Route("sso/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly LoginBranding _branding;
    private readonly LoginRateLimiter _limiter;
    private readonly SessionRevoker _revoker;
    private readonly ILogger<AdminController> _logger;
    private readonly RefusedSignIns _refused;
    private readonly ProviderCheck _check;

    public AdminController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        LoginBranding branding,
        LoginRateLimiter limiter,
        SessionRevoker revoker,
        ILogger<AdminController> logger,
        ProviderCheck check,
        RefusedSignIns? refused = null)
    {
        _check = check;
        _refused = refused ?? new RefusedSignIns();
        _libraryManager = libraryManager;
        _userManager = userManager;
        _branding = branding;
        _limiter = limiter;
        _revoker = revoker;
        _logger = logger;
    }

    // ─── Config ──────────────────────────────────────────────────────────────

    [HttpGet("config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetConfig() => Ok(Plugin.Instance!.Configuration);

    [HttpPost("config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult SaveConfig([FromBody] PluginConfiguration config)
    {
        if (ConfigurationRules.Problem(config) is { } problem)
        {
            return BadRequest(problem + " Nothing was saved.");
        }

        lock (Plugin.ConfigurationLock)
        {
            // Links are not the page's to send. They have their own endpoints, they change
            // while a page is open — a login links an account, an administrator removes a
            // link on the other tab — and the page posts the snapshot it loaded with.
            // Saving a provider therefore used to write that snapshot back: a link removed
            // on the Links tab reappeared and the identity it belonged to signed into that
            // account again; a link created while the page was open vanished, and that
            // person's next login was refused as a username collision until somebody
            // linked them by hand. Whatever arrives in the body, the live links win.
            //
            // SeenGroups is different and stays writable. It is a list of group names the
            // plugin has been told about, for the suggestions on this page; a save from a
            // stale page loses a name or two, and the next login that carries them puts
            // them back. Nothing decides anything on it.
            config.Links = Plugin.Instance!.Configuration.Links;

            // Inside the lock, because this replaces the whole configuration object and
            // every other writer mutates the live one under this lock. Outside it, a
            // self-service unlink could remove a link from an object that had just been
            // swapped away, report success, and leave the link in place.
            Plugin.Instance.UpdateConfiguration(config);
        }

        // The login buttons live in Jellyfin's branding, so they are rewritten here —
        // this is the only place a provider is ever enabled, renamed or disabled.
        _branding.Apply();

        // An administrator who raises the request limit is usually doing it because
        // somebody is being turned away right now, so the counts start again here.
        _limiter.Reset();
        return Ok();
    }

    /// <summary>The settings file and what it decides; an empty object when there is none.</summary>
    [HttpGet("settings-file")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetSettingsFile()
    {
        var file = Plugin.Instance!.SettingsFile;
        return Ok(file is null ? new { } : new { file.Path, file.Managed, file.Error });
    }

    // ─── Libraries ───────────────────────────────────────────────────────────

    [HttpGet("libraries")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetLibraries()
    {
        var folders = _libraryManager.GetVirtualFolders();
        return Ok(folders.Select(f => new { f.Name, ItemId = f.ItemId }));
    }

    // ─── Jellyfin users (for the manual-link dropdown) ────────────────────────

    [HttpGet("users")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetUsers()
    {
        var linkedIds = Plugin.Instance!.Configuration.Links
            .Select(l => l.JellyfinUserId).ToHashSet();
        return Ok(_userManager.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new { Id = u.Id, Name = u.Username, Linked = linkedIds.Contains(u.Id) })
            .ToList());
    }

    // ─── Seen groups ──────────────────────────────────────────────────────────

    [HttpGet("seen-groups")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetSeenGroups()
        => Ok(Plugin.Instance!.Configuration.SeenGroups.OrderBy(g => g));

    /// <summary>
    /// Checks one provider as it is saved: discovery, keys, and a confidential client's
    /// secret reference. The provider is named by id and read from the configuration —
    /// the request carries no address, so this is not a way to make the server fetch one.
    /// </summary>
    [HttpPost("providers/{providerId}/check")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> CheckProvider(string providerId, CancellationToken ct)
    {
        var provider = Plugin.Instance!.Configuration.Providers
            .FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal));

        return provider is null
            ? NotFound("No such provider. Save it first.")
            : Ok(await _check.RunAsync(provider, ct).ConfigureAwait(false));
    }

    // ─── Links ───────────────────────────────────────────────────────────────

    [HttpGet("links")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetLinks()
    {
        var config = Plugin.Instance!.Configuration;

        // Materialised inside the lock. Select is lazy, so this used to be enumerated by
        // MVC while serialising the response — after the action had returned, with no lock
        // held, over a list another request may be adding to.
        lock (Plugin.ConfigurationLock)
        {
            return Ok(config.Links.Select(l => new
            {
                l.ProviderId,
                l.Sub,
                l.JellyfinUserId,
                Username = _userManager.GetUserById(l.JellyfinUserId)?.Username ?? "(deleted)",
            }).ToList());
        }
    }

    /// <summary>
    /// Links an identity to a Jellyfin account by hand. Everything here is checked,
    /// because a link is what decides whose account an identity reaches: a link to a user
    /// Jellyfin no longer has, or a second identity on one account, turns the next
    /// unlink into a guess.
    /// </summary>
    [HttpPost("links")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult CreateLink([FromBody] LinkConfig link)
    {
        var config = Plugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(link.Sub))
        {
            return BadRequest("A subject is required.");
        }

        if (!config.Providers.Any(p => string.Equals(p.Id, link.ProviderId, StringComparison.Ordinal)))
        {
            return NotFound("No such provider.");
        }

        if (_userManager.GetUserById(link.JellyfinUserId) is null)
        {
            return NotFound("No such Jellyfin user.");
        }

        lock (Plugin.ConfigurationLock)
        {
            if (config.Links.Any(l =>
                    l.ProviderId == link.ProviderId &&
                    string.Equals(l.Sub, link.Sub, StringComparison.Ordinal)))
            {
                return Conflict("A link for this provider and subject already exists.");
            }

            if (config.Links.Any(l =>
                    l.ProviderId == link.ProviderId && l.JellyfinUserId == link.JellyfinUserId))
            {
                return Conflict("That Jellyfin account is already linked for this provider.");
            }

            config.Links.Add(link);
            Plugin.Instance.SaveConfiguration();
        }

        _refused.Forget(link.ProviderId, link.Sub);
        return Ok();
    }

    /// <summary>
    /// The sign-ins recently refused for want of a link, for the Account Links tab. An
    /// identity that has been linked since — here, or from the self-service page — is
    /// left out, so the list is always what is still to do.
    /// </summary>
    [HttpGet("refused-sign-ins")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetRefusedSignIns()
    {
        var config = Plugin.Instance!.Configuration;
        var recent = _refused.Recent();

        lock (Plugin.ConfigurationLock)
        {
            return Ok(recent
                .Where(r => !config.Links.Any(l =>
                    l.ProviderId == r.ProviderId
                    && string.Equals(l.Sub, r.Sub, StringComparison.Ordinal)))
                .Select(r => new
                {
                    r.ProviderId,
                    ProviderName = config.Providers
                        .FirstOrDefault(p => p.Id == r.ProviderId)?.Name ?? r.ProviderId,
                    r.Sub,
                    r.Name,
                    Reason = r.Reason == RefusedSignInReason.NameTaken ? "name-taken" : "no-account",
                    r.At,
                })
                .ToList());
        }
    }

    /// <summary>Empties that list — after a burst of strangers at an open provider, say.</summary>
    [HttpDelete("refused-sign-ins")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult ClearRefusedSignIns()
    {
        _refused.Clear();
        return NoContent();
    }

    /// <summary>
    /// Removes every link of one Jellyfin account, and ends its sessions.
    ///
    /// The sessions have to go with it, for the reason the self-service unlink already
    /// says: back-channel logout finds an account through its link and nothing else, so an
    /// account that has been unlinked keeps tokens the identity provider can no longer
    /// end, and Jellyfin does not expire them. An administrator unlinking somebody is
    /// usually cutting off their access, which is the one reading under which leaving the
    /// sessions running is worst — and the self-service endpoint was fixed for this while
    /// the administrator's was not.
    /// </summary>
    [HttpDelete("links/{jellyfinUserId:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> DeleteLink(Guid jellyfinUserId, CancellationToken ct)
    {
        var unlinked = await new Unlinking(_revoker, _logger)
            .RemoveAsync(jellyfinUserId, providerId: null, ct).ConfigureAwait(false);

        return unlinked ? Ok() : NotFound("No link found for this user.");
    }

}
