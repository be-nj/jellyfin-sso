using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Refusals a browser shows as written.
///
/// Every endpoint under /sso can be reached by a person, not only by a client: somebody
/// who lands on a callback URL, or whose provider sent them back with an error, reads the
/// answer. ASP.NET's own <c>BadRequest(string)</c> serialises to a JSON string, so that
/// person reads the message with quotes around it, and a 404 from <c>NotFound(string)</c>
/// reads the same way. These build the plain-text answer instead.
/// </summary>
internal static class PlainText
{
    internal static IActionResult NotFound(string message)
        => Text(StatusCodes.Status404NotFound, message);

    /// <summary>A refusal the caller cannot fix by retrying.</summary>
    internal static IActionResult Forbidden(string message)
        => Text(StatusCodes.Status403Forbidden, message);

    internal static IActionResult BadRequest(string message)
        => Text(StatusCodes.Status400BadRequest, message);

    /// <summary>A collision with another request; trying again is the fix.</summary>
    internal static IActionResult Conflict(string message)
        => Text(StatusCodes.Status409Conflict, message);

    internal static IActionResult Unavailable(string message)
        => Text(StatusCodes.Status503ServiceUnavailable, message);

    private static IActionResult Text(int status, string message)
        => new ContentResult
        {
            StatusCode = status,
            Content = message,
            ContentType = "text/plain",
        };
}
