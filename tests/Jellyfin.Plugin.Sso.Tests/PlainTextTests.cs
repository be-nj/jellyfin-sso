using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Every endpoint under /sso can be reached by a person, not only by a client: somebody
/// who lands on a callback URL, or whose provider sent them back with an error, reads the
/// answer. ASP.NET's own BadRequest(string) serialises to a JSON string, so that person
/// read the message with quotes around it.
/// </summary>
public sealed class PlainTextTests
{
    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public void A_refusal_is_the_message_itself_under_the_status_it_belongs_to(int status)
    {
        var result = (ContentResult)(status switch
        {
            400 => PlainText.BadRequest("Provider mismatch."),
            403 => PlainText.Forbidden("Provider mismatch."),
            404 => PlainText.NotFound("Provider mismatch."),
            _ => PlainText.Unavailable("Provider mismatch."),
        });

        Assert.Equal(status, result.StatusCode);
        Assert.Equal("Provider mismatch.", result.Content);
        Assert.Equal("text/plain", result.ContentType);
    }
}
