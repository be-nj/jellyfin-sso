using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Auth;

/// <summary>
/// Puts <see cref="LoginRateLimiter"/> in front of an action. It goes on the endpoints
/// that can be reached without a Jellyfin token and that cost the server real work, and
/// answers 429 with a Retry-After once a caller is past the limit.
///
/// The address is the one the connection came from, which is what Jellyfin's own
/// forwarded-headers handling has already resolved — a header the caller writes itself is
/// no basis for a limit.
/// </summary>
public sealed class RateLimitFilter : IActionFilter
{
    private readonly LoginRateLimiter _limiter;
    private readonly ILogger<RateLimitFilter> _logger;

    public RateLimitFilter(LoginRateLimiter limiter, ILogger<RateLimitFilter> logger)
    {
        _limiter = limiter;
        _logger = logger;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var caller = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        var decision = _limiter.Check(caller);

        if (decision.Allowed)
        {
            return;
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        context.HttpContext.Response.Headers.RetryAfter =
            seconds.ToString(CultureInfo.InvariantCulture);

        if (decision.FirstRefusal)
        {
            // Once per window: a caller that keeps knocking must not be able to write the
            // log full by knocking.
            _logger.LogWarning(
                "Too many SSO requests from {Caller}; refusing the rest of this minute.",
                caller ?? "an address the server could not read");
        }

        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
            ContentType = "text/plain",
            Content = $"Too many login attempts. Try again in {seconds} seconds.",
        };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // Nothing to do once the action has run.
    }
}
