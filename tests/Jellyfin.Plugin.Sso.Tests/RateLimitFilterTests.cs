using Jellyfin.Plugin.Sso.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// What a caller past the limit is actually told. The limiter decides; this is the part
/// that turns a decision into an HTTP answer, and an answer without a Retry-After leaves
/// a well-behaved client guessing.
/// </summary>
public sealed class RateLimitFilterTests
{
    private const int Limit = 3;

    private readonly DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly RateLimitFilter _filter;

    public RateLimitFilterTests()
    {
        var limiter = new LoginRateLimiter(TimeSpan.FromMinutes(1), () => _now, () => Limit);
        _filter = new RateLimitFilter(limiter, NullLogger<RateLimitFilter>.Instance);
    }

    private static ActionExecutingContext Request(string? address)
    {
        var http = new DefaultHttpContext();
        if (address is not null)
        {
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
        }

        return new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: null!);
    }

    private ActionExecutingContext Call(string? address)
    {
        var context = Request(address);
        _filter.OnActionExecuting(context);
        return context;
    }

    [Fact]
    public void A_request_under_the_limit_is_left_alone()
    {
        var context = Call("10.0.0.1");

        // No result set means the action itself runs.
        Assert.Null(context.Result);
    }

    [Fact]
    public void A_request_past_the_limit_is_answered_with_429_and_a_retry_after()
    {
        for (var i = 0; i < Limit; i++)
        {
            Call("10.0.0.1");
        }

        var context = Call("10.0.0.1");

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, result.StatusCode);
        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal("60", context.HttpContext.Response.Headers.RetryAfter);
        Assert.Contains("Try again", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_address_is_counted_on_its_own()
    {
        for (var i = 0; i < Limit; i++)
        {
            Call("10.0.0.1");
        }

        Assert.Null(Call("10.0.0.2").Result);
    }

    [Fact]
    public void A_request_the_server_could_not_place_still_gets_an_answer()
    {
        for (var i = 0; i < Limit; i++)
        {
            Call(null);
        }

        var context = Call(null);

        Assert.IsType<ContentResult>(context.Result);
    }
}
