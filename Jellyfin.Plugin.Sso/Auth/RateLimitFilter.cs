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
/// The address is the one the connection came from. That is the caller's address only
/// where Jellyfin's own forwarded-headers handling has resolved it, which needs the proxy
/// registered as a known proxy in Jellyfin's network settings. Without that — a proxy in
/// a second container is the ordinary case — every anonymous request from everybody
/// arrives from the proxy's address and shares one bucket, so sixty requests can keep a
/// household from logging in for the rest of the minute, and the provider's back-channel
/// logout posts are refused along with them.
///
/// The alternative is worse: a limit keyed on a header the caller writes is a limit the
/// caller can reset per request. So this stays as it is, the documentation says what a
/// proxy deployment has to configure for the limit to mean one person, and the cap on
/// in-flight logins no longer lets a flood evict anybody — which was the part that could
/// be aimed.
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
