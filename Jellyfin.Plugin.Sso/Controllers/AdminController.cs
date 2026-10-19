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

    public AdminController(
        ILibraryManager libraryManager, IUserManager userManager, LoginBranding branding)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _branding = branding;
    }

    // ─── Config ──────────────────────────────────────────────────────────────

    [HttpGet("config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult GetConfig() => Ok(Plugin.Instance!.Configuration);

    [HttpPost("config")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult SaveConfig([FromBody] PluginConfiguration config)
    {
        Plugin.Instance!.UpdateConfiguration(config);

        // The login buttons live in Jellyfin's branding, so they are rewritten here —
        // this is the only place a provider is ever enabled, renamed or disabled.
        _branding.Apply();
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
        return Ok(config.Links.Select(l => new
        {
            l.ProviderId,
            l.Sub,
            l.JellyfinUserId,
            Username = _userManager.GetUserById(l.JellyfinUserId)?.Username ?? "(deleted)",
        }));
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
