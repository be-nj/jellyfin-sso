using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

// Regression tests for the November 10 security review.
public sealed class SecurityRegressionTests
{
    [Fact]
    public async Task Unlink_must_invalidate_unspent_bridge_credentials()
    {
        using var harness = new PluginHarness();
        var account = Guid.NewGuid();
        harness.Configuration.Links.Add(new LinkConfig { ProviderId = "p", Sub = "s", JellyfinUserId = account });
        var secrets = new EphemeralSecretStore();
        var credential = secrets.Issue(account, "alice");
        var revoker = new SessionRevoker(Substitute.For<ISessionManager>(), NullLogger<SessionRevoker>.Instance);
        await new Unlinking(revoker, NullLogger.Instance).RemoveAsync(account, "p", default);
        Assert.Empty(harness.Configuration.Links);
        Assert.Null(secrets.Consume(credential));
    }

    [Fact]
    public async Task Backchannel_logout_must_invalidate_pending_quick_connect_approvals()
    {
        using var harness = new PluginHarness();
        var account = Guid.NewGuid();
        harness.Configuration.Links.Add(new LinkConfig { ProviderId = "p", Sub = "s", JellyfinUserId = account });
        var approvals = new QuickConnectApprovalStore();
        var token = approvals.Issue(account, "alice", "123456");
        var revoker = new SessionRevoker(Substitute.For<ISessionManager>(), NullLogger<SessionRevoker>.Instance);
        Assert.Equal(1, await revoker.RevokeAsync("p", "s"));
        Assert.Null(approvals.Consume(token));
    }

    [Fact]
    public async Task Jit_and_self_service_link_must_not_create_two_owners_for_one_identity()
    {
        using var harness = new PluginHarness();
        var users = Substitute.For<IUserManager>();
        var created = new User("new-user", "local", "reset");
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeCreate = new TaskCompletionSource<User>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = Substitute.For<ISsoUserPersistence>();
        persistence.CreateAsync("new-user", Arg.Any<CancellationToken>()).Returns(_ => { createEntered.SetResult(); return resumeCreate.Task; });
        var resolve = new UserProvisioner(users, NullLogger<UserProvisioner>.Instance, persistence)
            .ResolveAsync(new ProviderConfig { Id = "p" }, new OidcIdentity("subject", "new-user", []));
        await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var decision = SelfServiceLinks.Attach(harness.Configuration, "p", "subject", Guid.NewGuid());
        Assert.Equal(SelfServiceLinkOutcome.Write, decision.Outcome);
        resumeCreate.SetResult(created);
        await Assert.ThrowsAsync<LoginRaceException>(() => resolve);
        await users.Received(1).DeleteUserAsync(created.Id);
        Assert.Single(harness.Configuration.Links, l => l.ProviderId == "p" && l.Sub == "subject");
    }

    [Fact]
    public async Task Discovery_must_check_the_jwks_host_before_sending_the_request()
    {
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request => {
            requests.Add(request.RequestUri!.Host);
            var json = request.RequestUri.Host == "idp.example"
                ? "{\"issuer\":\"https://idp.example\",\"jwks_uri\":\"https://internal.example/keys\"}"
                : "{\"keys\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetDiscoveryDocumentAsync(new ProviderConfig {
            Id = "p", Authority = "https://idp.example"
        }));
        Assert.DoesNotContain("internal.example", requests);
    }

    [Fact]
    public async Task Shared_authentik_endpoint_paths_are_allowed_on_the_configured_origin()
    {
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var json = request.RequestUri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? """{"issuer":"https://idp.example/application/o/jellyfin/","jwks_uri":"https://idp.example/application/o/keys/","token_endpoint":"https://idp.example/application/o/token/","authorization_endpoint":"https://login.example/authorize"}"""
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);

        var document = await factory.GetDiscoveryDocumentAsync(new ProviderConfig
            { Id = "authentik", Authority = "https://idp.example/application/o/jellyfin/" });

        Assert.False(document.IsError);
        Assert.Equal(["/application/o/jellyfin/.well-known/openid-configuration", "/application/o/keys/"], requests);
    }

    [Fact]
    public async Task Endpoints_the_plugin_never_calls_may_live_on_another_host()
    {
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        using var client = new HttpClient(new Handler(request =>
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? """{"issuer":"https://idp.example","jwks_uri":"https://idp.example/keys","token_endpoint":"https://idp.example/token","device_authorization_endpoint":"https://device.example/device","introspection_endpoint":"https://idp.example:8443/introspect","check_session_iframe":"https://session.example/iframe"}"""
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);

        var document = await factory.GetDiscoveryDocumentAsync(new ProviderConfig { Id = "p", Authority = "https://idp.example" });

        Assert.False(document.IsError, document.Error);
    }

    [Fact]
    public async Task An_authority_typed_with_capitals_matches_endpoints_published_the_same_way()
    {
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        using var client = new HttpClient(new Handler(request =>
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? """{"issuer":"https://IdP.example","jwks_uri":"https://IdP.example/keys","token_endpoint":"https://IdP.example/token"}"""
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);

        var document = await factory.GetDiscoveryDocumentAsync(new ProviderConfig { Id = "p", Authority = "https://IdP.example" });

        Assert.False(document.IsError, document.Error);
    }

    [Fact]
    public async Task A_token_endpoint_on_another_host_is_still_refused()
    {
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.Host);
            var json = request.RequestUri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? """{"issuer":"https://idp.example","jwks_uri":"https://idp.example/keys","token_endpoint":"https://attacker.example/token"}"""
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.GetDiscoveryDocumentAsync(new ProviderConfig { Id = "p", Authority = "https://idp.example" }));
        Assert.DoesNotContain("attacker.example", requests);
    }

    [Fact]
    public void A_whole_configuration_saved_through_jellyfin_keeps_the_live_links()
    {
        using var harness = new PluginHarness();
        var live = new LinkConfig { ProviderId = "p", Sub = "s", JellyfinUserId = Guid.NewGuid() };
        harness.Configuration.Links.Add(live);

        harness.Plugin.UpdateConfiguration(new PluginConfiguration
        {
            Links = [new LinkConfig { ProviderId = "p", Sub = "other", JellyfinUserId = Guid.NewGuid() }],
        });

        Assert.Equal([live], harness.Plugin.Configuration.Links);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
