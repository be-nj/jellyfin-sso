using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public AdminController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        LoginBranding branding,
        LoginRateLimiter limiter)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _branding = branding;
        _limiter = limiter;
    }

    // ─── Config ──────────────────────────────────────────────────────────────

    [HttpGet("config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetConfig() => Ok(Plugin.Instance!.Configuration);

    [HttpPost("config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult SaveConfig([FromBody] PluginConfiguration config)
    {
        // A confidential client's secret is named here, not stored here. The page has no
        // field for it, so this arrives by hand-edited JSON — and a pasted literal secret
        // would be written to the plugin's configuration file, which the documentation
        // promises never happens. Refusing it at the door is what makes that true.
        foreach (var provider in config.Providers.Where(
            p => !string.IsNullOrWhiteSpace(p.ConfidentialSecretRef)))
        {
            if (!provider.ConfidentialSecretRef!.StartsWith("env:", StringComparison.Ordinal)
                && !provider.ConfidentialSecretRef.StartsWith("file:", StringComparison.Ordinal))
            {
                return BadRequest(
                    $"Provider '{provider.Id}': ConfidentialSecretRef must name where the "
                    + "secret is, as 'env:VARIABLE' or 'file:/path', not hold the secret "
                    + "itself. Nothing was saved.");
            }
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

        return Ok();
    }

    [HttpDelete("links/{jellyfinUserId:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult DeleteLink(Guid jellyfinUserId)
    {
        var config = Plugin.Instance!.Configuration;

        lock (Plugin.ConfigurationLock)
        {
            var removed = config.Links.RemoveAll(l => l.JellyfinUserId == jellyfinUserId);
            if (removed == 0)
            {
                return NotFound("No link found for this user.");
            }

            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

}
