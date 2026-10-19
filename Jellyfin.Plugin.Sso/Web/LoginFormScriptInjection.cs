using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Puts a &lt;script&gt; tag into jellyfin-web's index.html as it is served, so the login
/// button can sit inside the login form rather than below it in the branding.
///
/// Jellyfin gives a plugin no hook into the web client, and writing to the web folder on
/// disk breaks on every update and fails outright on a read-only container. Rewriting the
/// response instead is plain ASP.NET Core: an IStartupFilter adds middleware that buffers
/// the one response it cares about. Written from the ASP.NET documentation rather than
/// from any other plugin's source.
///
/// Deliberately timid: it touches nothing but a successful HTML response for index.html,
/// does nothing when the tag is already there, and on any error serves the original bytes
/// unchanged. A login page is worth more than a button on it.
/// </summary>
public sealed class LoginFormScriptInjection : IStartupFilter
{
    internal const string ScriptPath = "/sso/login-button.js";

    private readonly ILogger<LoginFormScriptInjection> _logger;

    public LoginFormScriptInjection(ILogger<LoginFormScriptInjection> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use(async (context, continuePipeline) =>
            {
                if (!WantsIndexHtml(context.Request) || !Enabled())
                {
                    await continuePipeline().ConfigureAwait(false);
                    return;
                }

                // Jellyfin compresses this page when the browser offers to take it
                // compressed, and compressed bytes cannot be edited as text. Asking for
                // it uncompressed costs a few kilobytes on one request per page load.
                var encodings = context.Request.Headers.AcceptEncoding;
                context.Request.Headers.AcceptEncoding = default;

                var original = context.Response.Body;
                using var buffer = new MemoryStream();
                context.Response.Body = buffer;

                try
                {
                    await continuePipeline().ConfigureAwait(false);

                    buffer.Position = 0;
                    var rewritten = await RewriteAsync(context, buffer).ConfigureAwait(false);

                    context.Response.Body = original;
                    buffer.Position = 0;

                    if (rewritten is null)
                    {
                        await buffer.CopyToAsync(original).ConfigureAwait(false);
                        return;
                    }

                    var bytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                    context.Response.ContentLength = bytes.Length;
                    await original.WriteAsync(bytes).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SSO: could not add the login button script; serving the page as it was.");
                    context.Response.Body = original;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original).ConfigureAwait(false);
                }
                finally
                {
                    context.Request.Headers.AcceptEncoding = encodings;
                    context.Response.Body = original;
                }
            });

            next(app);
        };

    private static bool Enabled()
        => Plugin.Instance?.Configuration is { InjectLoginButtonIntoForm: true } config
            && config.Providers.Any(p => p.Enabled);

    private static bool WantsIndexHtml(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The page with the tag added, or null to serve it untouched.</summary>
    private static async Task<string?> RewriteAsync(HttpContext context, Stream body)
    {
        if (context.Response.StatusCode != StatusCodes.Status200OK
            || context.Response.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        using var reader = new StreamReader(body, leaveOpen: true);
        var html = await reader.ReadToEndAsync().ConfigureAwait(false);

        if (html.Contains(ScriptPath, StringComparison.Ordinal))
        {
            return null;
        }

        var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (close < 0)
        {
            return null;
        }

        var prefix = ForwardedPaths.Prefix(
            context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault(),
            context.Request.PathBase.Value);

        var tag = $"<script defer src=\"{prefix}{ScriptPath}\"></script>";
        return html[..close] + tag + html[close..];
    }
}
