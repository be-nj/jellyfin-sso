using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Every answer under /sso refuses to be framed and to be sniffed (#346). The headers are
/// set in one place, so the test is that the place is reached by every controller, and
/// that it sets what it says.
/// </summary>
public sealed class ResponseHardeningTests
{
    [Fact]
    public void Every_controller_of_the_plugin_carries_the_headers()
    {
        var controllers = typeof(Plugin).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(controllers);
        Assert.All(controllers, controller => Assert.True(
            controller.GetCustomAttributes(typeof(ResponseHardeningAttribute), inherit: true).Length > 0,
            $"{controller.Name} answers without the hardening headers."));
    }

    [Fact]
    public void A_page_may_not_be_framed_or_read_as_another_type()
    {
        var http = new DefaultHttpContext();
        var context = new ResultExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [],
            new ContentResult { Content = "<p>page</p>", ContentType = "text/html" },
            controller: new object());

        new ResponseHardeningAttribute().OnResultExecuting(context);

        Assert.Equal("nosniff", http.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", http.Response.Headers["X-Frame-Options"]);
        Assert.Equal("frame-ancestors 'none'", http.Response.Headers["Content-Security-Policy"]);
    }
}
