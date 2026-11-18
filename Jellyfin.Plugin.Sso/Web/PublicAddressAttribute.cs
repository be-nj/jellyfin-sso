using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// On the pages a browser opens to begin something: with a public address configured,
/// a request that arrived anywhere else is sent to the public address first.
///
/// The provider returns the browser to the public address, because that is the redirect
/// URI it was given, and the cookie that ties the answer to this browser was set on the
/// host the login started at. Started at the LAN address, the login used to end on "this
/// login could not be matched to a state this browser started" — after the person had
/// signed in at the provider. Sent to the public address before anything is set, it runs
/// there from the first request, and ends with the browser signed in there.
///
/// Once, and never in a circle. A proxy that does not pass the public host on makes
/// every request look as if it arrived elsewhere, the redirected one included, so the
/// redirect carries a marker and a request that has it is served where it is.
///
/// A public address that is set and is not an address stops the flow instead: it was
/// set so that the request's headers would not decide where the provider sends the
/// code, and using them after all would be the opposite, in silence.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PublicAddressAttribute : ActionFilterAttribute
{
    /// <summary>The query parameter a redirected request carries.</summary>
    internal const string Marker = "at";

    internal const string MarkerValue = "public";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;
        var at = SsoAddress.For(request);

        if (at.PublicAddressInvalid)
        {
            context.Result = PlainText.ServerError(
                "The public address in the SSO settings is not an address, so no login can "
                + "start. An administrator has to correct it or clear it.");
            return;
        }

        if (Redirect(at, request) is { } target)
        {
            context.Result = new RedirectResult(target);
        }
    }

    /// <summary>
    /// Where to send this request, or null when it is where it should be. The target is
    /// the configured address with the request's own path and query: nothing in it comes
    /// from a header.
    /// </summary>
    internal static string? Redirect(SsoAddress at, HttpRequest request)
    {
        if (!at.Elsewhere
            || !HttpMethods.IsGet(request.Method)
            || request.Query[Marker] == MarkerValue)
        {
            return null;
        }

        var query = new QueryBuilder(
            request.Query.SelectMany(pair => pair.Value, (pair, value) => KeyValuePair.Create(pair.Key, value ?? string.Empty)))
        {
            { Marker, MarkerValue },
        };

        return at.BaseUri + request.Path.ToUriComponent() + query.ToQueryString().ToUriComponent();
    }
}
