using Jellyfin.Plugin.Sso.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// Admin API + UI endpoints.
/// /ui is AllowAnonymous (static HTML/JS only). All data endpoints require admin.
/// </summary>
[ApiController]
[Route("sso/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public AdminController(ILibraryManager libraryManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
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

    [HttpPost("links")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult CreateLink([FromBody] LinkConfig link)
    {
        var config = Plugin.Instance!.Configuration;
        if (config.Links.Any(l =>
                l.ProviderId == link.ProviderId &&
                string.Equals(l.Sub, link.Sub, StringComparison.Ordinal)))
            return Conflict("Link already exists.");
        config.Links.Add(link);
        Plugin.Instance.SaveConfiguration();
        return Ok();
    }

    [HttpDelete("links/{jellyfinUserId:guid}")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult DeleteLink(Guid jellyfinUserId)
    {
        var config = Plugin.Instance!.Configuration;
        var removed = config.Links.RemoveAll(l => l.JellyfinUserId == jellyfinUserId);
        if (removed == 0) return NotFound();
        Plugin.Instance.SaveConfiguration();
        return Ok();
    }

}
