using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Headers every answer under /sso carries, whatever the deployment's proxy adds or forgets.
///
/// No page the plugin renders is meant to be shown inside somebody else's page. Only the
/// Quick Connect approval used to say so, and the self-service link page, whose button
/// starts a link for whoever is signed in, could be framed by any site under a decoy (#346).
/// The provider decides the identity, so a stolen click there wins little, but these two
/// headers cost nothing and end the question for every page at once, including the next.
///
/// And no answer is to be read as anything other than the type it states: the login
/// button's script is served as JavaScript and the refusals as plain text, so a browser
/// that sniffed a refusal into markup would be running something the plugin never wrote.
///
/// One exception to keep in mind: OpenID front-channel logout loads the relying party's
/// logout address inside the provider's own page. The plugin has no such endpoint; one
/// added later would need its own framing rule naming the provider.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
internal sealed class ResponseHardeningAttribute : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        var headers = context.HttpContext.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        base.OnResultExecuting(context);
    }
}
