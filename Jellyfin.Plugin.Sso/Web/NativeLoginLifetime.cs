using System.Security.Claims;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Brings the tokens Jellyfin mints itself inside the revocation boundary of
/// <see cref="LoginLifetime"/>. The plugin's part of a login ends when it hands over a
/// secret or approves a Quick Connect code; the token comes afterwards, out of Jellyfin's
/// own controllers, and a revocation can land between the two.
///
/// Nothing here waits before the action runs, so a login is never queued behind another
/// login or behind a revocation, and a revocation is never queued behind a login. The
/// check comes after the token exists: if the account was revoked since the action
/// started, the token is taken back here, and if it was revoked later, the revocation's
/// own sweep finds a token that is already in the store.
///
/// Jellyfin's controllers are matched by name, because Jellyfin.Api is not a package a
/// plugin can reference. <see cref="NativeLoginActionsCheck"/> says at startup whether
/// every name below still exists, so a renamed action shows up in the log rather than as
/// a silently missing guarantee.
/// </summary>
public sealed class NativeLoginLifetime(
    ISessionManager sessions,
    IDeviceManager devices,
    SessionRevoker revoker,
    ILogger<NativeLoginLifetime> logger) : IAsyncActionFilter
{
    /// <summary>Jellyfin's claim types, from Jellyfin.Api.Constants.InternalClaimTypes.</summary>
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string TokenClaim = "Jellyfin-Token";
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    internal enum Kind { None, Login, QuickConnectAuthorize }

    /// <summary>Every Jellyfin action this filter has to see, by controller and action name.</summary>
    internal static readonly IReadOnlyList<(string Controller, string Action, Kind Kind)> Guarded =
    [
        ("User", "AuthenticateUserByName", Kind.Login),
        ("User", "AuthenticateUser", Kind.Login),
        ("QuickConnect", "AuthorizeQuickConnect", Kind.QuickConnectAuthorize),
    ];

    internal static Kind Classify(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor descriptor)
        => descriptor is ControllerActionDescriptor action
            ? Guarded.FirstOrDefault(g => g.Controller == action.ControllerName
                && g.Action == action.ActionName).Kind
            : Kind.None;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var kind = Classify(context.ActionDescriptor);
        if (kind == Kind.None)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var before = LoginLifetime.Version;
        var executed = await next().ConfigureAwait(false);

        if (kind == Kind.Login)
            await AfterLoginAsync(executed, before).ConfigureAwait(false);
        else
            await AfterQuickConnectAuthorizeAsync(context, executed, before).ConfigureAwait(false);
    }

    /// <summary>
    /// A password or SSO-secret login that minted a token. The secret was checked against
    /// its own login's version inside the action; what is left is a revocation between
    /// that check and the token, which this sees as a version newer than the one read
    /// before the action.
    /// </summary>
    private async Task AfterLoginAsync(ActionExecutedContext executed, long before)
    {
        if (executed.Result is not ObjectResult
            {
                Value: AuthenticationResult { User: { } user, AccessToken: { Length: > 0 } token },
            }
            || LoginLifetime.IsCurrent(user.Id, before))
        {
            return;
        }

        await sessions.Logout(token).ConfigureAwait(false);
        executed.Result = new UnauthorizedResult();
        logger.LogInformation(
            "Took back the token of a login for user {UserId}: the account was revoked while "
            + "it was being signed in.", user.Id);
    }

    /// <summary>
    /// Jellyfin's own Quick Connect approval, made with a token the caller already holds.
    /// That token was checked before this filter ran, so a version read here cannot tell
    /// whether a revocation removed it in the meantime. The token itself can: after a
    /// sweep of the caller's account has finished, a token that is no longer in the store
    /// was revoked, and so is anything it just authorised.
    /// </summary>
    private async Task AfterQuickConnectAuthorizeAsync(
        ActionExecutingContext context, ActionExecutedContext executed, long before)
    {
        if (executed.Result is not ObjectResult { Value: true })
            return;

        var principal = context.HttpContext.User;
        var callerId = ClaimGuid(principal, UserIdClaim);
        var userId = context.ActionArguments.TryGetValue("userId", out var argument)
            && argument is Guid requested && requested != Guid.Empty
                ? requested
                : callerId;
        if (userId is not { } authorised)
            return;

        var revoked = !LoginLifetime.IsCurrent(authorised, before);
        if (!revoked
            && callerId is { } caller
            && principal.FindFirst(IsApiKeyClaim)?.Value != "true"
            && principal.FindFirst(TokenClaim)?.Value is { Length: > 0 } callerToken)
        {
            using (await LoginLifetime.SweepingAsync(caller).ConfigureAwait(false))
                revoked = devices.GetDevices(new DeviceQuery { AccessToken = callerToken }).TotalRecordCount == 0;
        }

        if (!revoked)
            return;

        // The token Jellyfin minted is not exposed; sweeping the account it was minted for
        // is the only way to reach it, and that account was being revoked anyway.
        await revoker.SweepAsync(authorised).ConfigureAwait(false);
        executed.Result = new UnauthorizedResult();
    }

    private static Guid? ClaimGuid(ClaimsPrincipal principal, string type)
        => Guid.TryParse(principal.FindFirst(type)?.Value, out var id) ? id : null;
}
