using System.Security.Claims;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The tokens Jellyfin mints itself, after the plugin's part of a login is done. The
/// revocation they race must win without ever waiting for them.
/// </summary>
public sealed class NativeLoginLifetimeTests
{
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly IDeviceManager _devices = Substitute.For<IDeviceManager>();
    private readonly SessionRevoker _revoker;
    private readonly NativeLoginLifetime _filter;

    public NativeLoginLifetimeTests()
    {
        _revoker = new SessionRevoker(_sessions, NullLogger<SessionRevoker>.Instance);
        _filter = new NativeLoginLifetime(
            _sessions, _devices, _revoker, NullLogger<NativeLoginLifetime>.Instance);
    }

    [Theory]
    [InlineData("AuthenticateUserByName")]
    [InlineData("AuthenticateUser")]
    public async Task A_token_minted_while_its_account_was_revoked_is_taken_back(string action)
    {
        using var harness = new PluginHarness();
        var userId = Guid.NewGuid();
        var executing = Executing("User", action);

        var executed = await Run(executing, async () =>
        {
            // The revocation lands after the secret was checked, before the token exists.
            await _revoker.RevokeUserAsync(userId);
            return Minted(executing, userId, "fresh-token");
        });

        await _sessions.Received(1).Logout("fresh-token");
        Assert.IsType<UnauthorizedResult>(executed.Result);
    }

    [Fact]
    public async Task A_login_nobody_revoked_keeps_its_token()
    {
        var userId = Guid.NewGuid();
        var executing = Executing("User", "AuthenticateUserByName");

        var executed = await Run(executing, () => Task.FromResult(Minted(executing, userId, "kept")));

        await _sessions.DidNotReceiveWithAnyArgs().Logout(default(string)!);
        Assert.IsType<ObjectResult>(executed.Result);
    }

    [Fact]
    public async Task Revoking_somebody_else_does_not_touch_this_login()
    {
        using var harness = new PluginHarness();
        var userId = Guid.NewGuid();
        var executing = Executing("User", "AuthenticateUserByName");

        var executed = await Run(executing, async () =>
        {
            await _revoker.RevokeUserAsync(Guid.NewGuid());
            return Minted(executing, userId, "kept");
        });

        await _sessions.DidNotReceiveWithAnyArgs().Logout(default(string)!);
        Assert.IsType<ObjectResult>(executed.Result);
    }

    /// <summary>
    /// The regression the first version of this guard introduced: it held one gate around
    /// every native login, and a revocation waited in the same queue. A flood of password
    /// attempts kept a back-channel logout waiting until the provider gave up.
    /// </summary>
    [Fact]
    public async Task A_back_channel_logout_does_not_wait_for_a_login_in_progress()
    {
        using var harness = new PluginHarness();
        var userId = Guid.NewGuid();
        harness.Configuration.Links.Add(new LinkConfig { ProviderId = "p", Sub = "s", JellyfinUserId = userId });
        var executing = Executing("User", "AuthenticateUserByName");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var login = Run(executing, async () =>
        {
            entered.SetResult();
            await finish.Task;
            return Minted(executing, userId, "late-token");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            // Completes while the login is still holding on to its action.
            Assert.Equal(1, await _revoker.RevokeAsync("p", "s").WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            finish.SetResult();
        }

        var executed = await login.WaitAsync(TimeSpan.FromSeconds(5));
        await _sessions.Received(1).Logout("late-token");
        Assert.IsType<UnauthorizedResult>(executed.Result);
    }

    [Fact]
    public async Task A_quick_connect_approval_made_with_a_token_swept_meanwhile_is_swept_again()
    {
        var userId = Guid.NewGuid();
        var executing = Executing("QuickConnect", "AuthorizeQuickConnect", Caller(userId, "caller-token"));
        _devices.GetDevices(Arg.Is<DeviceQuery>(q => q.AccessToken == "caller-token"))
            .Returns(new QueryResult<Device>([]));

        var executed = await Run(executing, () => Task.FromResult(Approved(executing)));

        await _sessions.Received(1).RevokeUserTokens(userId, null);
        Assert.IsType<UnauthorizedResult>(executed.Result);
    }

    [Fact]
    public async Task A_quick_connect_approval_made_with_a_live_token_stands()
    {
        var userId = Guid.NewGuid();
        var executing = Executing("QuickConnect", "AuthorizeQuickConnect", Caller(userId, "caller-token"));
        _devices.GetDevices(Arg.Is<DeviceQuery>(q => q.AccessToken == "caller-token"))
            .Returns(new QueryResult<Device>([new Device(userId, "app", "1", "tv", "device-1")]));

        var executed = await Run(executing, () => Task.FromResult(Approved(executing)));

        await _sessions.DidNotReceiveWithAnyArgs().RevokeUserTokens(default, default!);
        Assert.IsType<ObjectResult>(executed.Result);
    }

    [Fact]
    public async Task A_quick_connect_approval_for_an_account_revoked_meanwhile_is_swept_again()
    {
        using var harness = new PluginHarness();
        var userId = Guid.NewGuid();
        var executing = Executing("QuickConnect", "AuthorizeQuickConnect", Caller(userId, "caller-token"));

        var executed = await Run(executing, async () =>
        {
            await _revoker.RevokeUserAsync(userId);
            return Approved(executing);
        });

        // Once by the revocation itself, once more for the token minted after its sweep.
        await _sessions.Received(2).RevokeUserTokens(userId, null);
        Assert.IsType<UnauthorizedResult>(executed.Result);
    }

    [Fact]
    public async Task An_api_key_approving_quick_connect_is_not_mistaken_for_a_revoked_token()
    {
        var userId = Guid.NewGuid();
        var executing = Executing("QuickConnect", "AuthorizeQuickConnect", Caller(userId, "api-key", apiKey: true));

        var executed = await Run(executing, () => Task.FromResult(Approved(executing)));

        _devices.DidNotReceiveWithAnyArgs().GetDevices(default!);
        Assert.IsType<ObjectResult>(executed.Result);
    }

    [Fact]
    public async Task Other_actions_pass_through_untouched()
    {
        var executing = Executing("Library", "GetItems");
        var executed = await Run(executing, () => Task.FromResult(new ActionExecutedContext(executing, [], new object())));
        Assert.Null(executed.Result);
        _devices.DidNotReceiveWithAnyArgs().GetDevices(default!);
    }

    [Fact]
    public void The_filter_and_its_startup_check_are_registered()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

        using var provider = services.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        Assert.Contains(filters.OfType<TypeFilterAttribute>(), f => f.ImplementationType == typeof(NativeLoginLifetime));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(NativeLoginActionsCheck));
    }

    [Fact]
    public async Task The_startup_check_names_a_missing_jellyfin_action()
    {
        var logger = new RecordingLogger<NativeLoginActionsCheck>();
        var actions = Substitute.For<IActionDescriptorCollectionProvider>();
        actions.ActionDescriptors.Returns(new ActionDescriptorCollection(
            [new ControllerActionDescriptor { ControllerName = "User", ActionName = "AuthenticateUserByName" }], 1));

        await new NativeLoginActionsCheck(actions, logger).StartAsync(default);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("User.AuthenticateUser,", warning + ",", StringComparison.Ordinal);
        Assert.Contains("QuickConnect.AuthorizeQuickConnect", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_startup_check_is_quiet_when_every_action_exists()
    {
        var logger = new RecordingLogger<NativeLoginActionsCheck>();
        var actions = Substitute.For<IActionDescriptorCollectionProvider>();
        actions.ActionDescriptors.Returns(new ActionDescriptorCollection(
            NativeLoginLifetime.Guarded
                .Select(g => (ActionDescriptor)new ControllerActionDescriptor { ControllerName = g.Controller, ActionName = g.Action })
                .ToList(), 1));

        await new NativeLoginActionsCheck(actions, logger).StartAsync(default);

        Assert.Empty(logger.Warnings);
        Assert.Contains(logger.Messages(LogLevel.Information), m => m.StartsWith(NativeLoginActionsCheck.AllPresent, StringComparison.Ordinal));
    }

    [Fact]
    public void No_credential_store_may_outlive_the_revocation_records()
    {
        var tooLong = LoginLifetime.RevocationLifetime;
        Assert.Throws<ArgumentOutOfRangeException>(() => new EphemeralSecretStore(tooLong));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickConnectApprovalStore(tooLong));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Oidc.PendingStateStore(tooLong));
        // And the defaults are inside it.
        _ = new EphemeralSecretStore();
        _ = new QuickConnectApprovalStore();
        _ = new Oidc.PendingStateStore(NullLogger<Oidc.PendingStateStore>.Instance);
    }

    private async Task<ActionExecutedContext> Run(
        ActionExecutingContext executing, Func<Task<ActionExecutedContext>> action)
    {
        ActionExecutedContext? executed = null;
        await _filter.OnActionExecutionAsync(executing, async () => executed = await action());
        return executed!;
    }

    private static ActionExecutingContext Executing(string controller, string action, ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        if (user is not null)
            http.User = user;
        var context = new ActionContext(http, new RouteData(),
            new ControllerActionDescriptor { ControllerName = controller, ActionName = action }, new ModelStateDictionary());
        return new ActionExecutingContext(context, [], new Dictionary<string, object?>(), new object());
    }

    private static ClaimsPrincipal Caller(Guid userId, string token, bool apiKey = false)
        => new(new ClaimsIdentity(
        [
            new Claim("Jellyfin-UserId", userId.ToString()),
            new Claim("Jellyfin-Token", token),
            new Claim("Jellyfin-IsApiKey", apiKey ? "true" : "false"),
        ], "Test"));

    private static ActionExecutedContext Minted(ActionExecutingContext executing, Guid userId, string token)
        => new(executing, [], new object())
        {
            Result = new ObjectResult(new AuthenticationResult
            {
                User = new UserDto { Id = userId },
                AccessToken = token,
            }),
        };

    private static ActionExecutedContext Approved(ActionExecutingContext executing)
        => new(executing, [], new object()) { Result = new ObjectResult(true) };
}
