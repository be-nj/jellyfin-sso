using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// The admin page is plain JavaScript loaded into Jellyfin's dashboard, so nothing but a
/// browser notices a typo in it. This drives the real page: it renders inside the real
/// dashboard, a mapping edited there round-trips through the plugin's API, and the safety
/// settings are actually saved.
/// </summary>
[Collection(JellyfinCollection.Name)]
public sealed class ConfigPageTests : IAsyncLifetime
{
    private readonly JellyfinEnvironment _env;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private readonly List<TracedContext> _traced = [];

    public ConfigPageTests(JellyfinEnvironment env)
    {
        _env = env;
    }

    public async Task InitializeAsync()
    {
        await _env.ResetAsync();
        _playwright = await BrowserSupport.InstallAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        // The traces are written here, because the helper below hands a page to the test
        // and keeps the context alive for as long as the test needs it.
        foreach (var traced in _traced)
        {
            await traced.DisposeAsync();
        }

        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task The_admin_page_saves_a_group_mapping_with_its_permissions()
    {
        var page = await OpenConfigPageAsync();

        await page.ClickAsync("#AddMapping");
        var card = page.Locator(".sso-mapping").Last;
        await card.Locator(".sso-group-input").FillAsync("set-from-ui");
        // Jellyfin styles its checkboxes with an overlay, so the input itself is not
        // clickable in the ordinary sense.
        await card.Locator(".sso-livetv-cb").CheckAsync(new LocatorCheckOptions { Force = true });
        await card.Locator(".sso-subtitles-cb").CheckAsync(new LocatorCheckOptions { Force = true });
        await card.Locator(".sso-rating-input").FillAsync("12");

        await page.ClickAsync("#Save");

        // Read it back through the API: the page is only trustworthy if this matches.
        var mapping = await WaitForMappingAsync("set-from-ui");
        Assert.True(mapping["EnableLiveTvAccess"]!.GetValue<bool>());
        Assert.True(mapping["EnableSubtitleManagement"]!.GetValue<bool>());
        Assert.False(mapping["EnableContentDownloading"]!.GetValue<bool>());
        // The one nobody ticked, and the one that must never be ticked by accident.
        Assert.False(mapping["EnableContentDeletion"]!.GetValue<bool>());
        Assert.Equal(12, mapping["MaxParentalRating"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_admin_page_saves_the_safety_settings()
    {
        var page = await OpenConfigPageAsync();

        await page.CheckAsync("#PreserveAdminPermissions", new PageCheckOptions { Force = true });
        // The exclusion list is built from the server's users, so the setup admin is there.
        await page.Locator($".sso-excl-cb[value=\"{_env.AdminUserId}\"]")
            .CheckAsync(new LocatorCheckOptions { Force = true });

        await page.ClickAsync("#Save");

        await WaitForConfigAsync(config =>
            config["PreserveAdminPermissions"]!.GetValue<bool>()
            && config["SyncExcludedUserIds"]!.AsArray().Count == 1);
    }

    [Fact]
    public async Task The_admin_page_shows_the_stored_mappings()
    {
        var page = await OpenConfigPageAsync();

        // ResetAsync configures media, kids and jellyfin-admins.
        Assert.Equal(3, await page.Locator(".sso-mapping").CountAsync());
        var groups = await page.Locator(".sso-group-input").EvaluateAllAsync<string[]>(
            "inputs => inputs.map(i => i.value)");
        Assert.Contains("media", groups);
        Assert.Contains("jellyfin-admins", groups);
    }

    [Fact]
    public async Task The_admin_page_adds_a_second_provider()
    {
        var page = await OpenConfigPageAsync();
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");

        await page.ClickAsync("#AddProvider");
        var card = page.Locator(".sso-provider").Last;
        await card.Locator(".sso-provider-name").FillAsync("Second IdP");
        await card.Locator(".sso-provider-authority").FillAsync("https://idp.example.com/app/o/jf");
        await card.Locator(".sso-provider-client").FillAsync("second-client");

        await page.ClickAsync("#Save");

        var config = await WaitForConfigAsync(c => c["Providers"]!.AsArray().Count == 2);
        var added = config["Providers"]!.AsArray()
            .First(p => p!["Name"]!.GetValue<string>() == "Second IdP")!;
        // New providers are saved disabled; the administrator turns them on when the
        // identity provider side is finished.
        Assert.False(added["Enabled"]!.GetValue<bool>());
        Assert.Equal("second-client", added["ClientId"]!.GetValue<string>());
        Assert.Equal("groups", added["GroupsClaim"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_provider_can_be_saved_with_nothing_but_a_name()
    {
        // The documented first step: save a provider to learn its id, because the
        // identity provider's redirect URI has to contain it before anything else works.
        var page = await OpenConfigPageAsync();
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");

        await page.ClickAsync("#AddProvider");
        await page.Locator(".sso-provider").Last.Locator(".sso-provider-name").FillAsync("Half filled");

        await page.ClickAsync("#Save");

        var config = await WaitForConfigAsync(c => c["Providers"]!.AsArray()
            .Any(p => p!["Name"]!.GetValue<string>() == "Half filled"));
        var saved = config["Providers"]!.AsArray()
            .First(p => p!["Name"]!.GetValue<string>() == "Half filled")!;
        Assert.False(string.IsNullOrWhiteSpace(saved["Id"]!.GetValue<string>()));

        // Disabled until it is finished: an enabled half-configured provider would put a
        // button on everyone's login page that leads nowhere.
        Assert.False(saved["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_card_shows_the_redirect_uri_the_provider_needs()
    {
        var page = await OpenConfigPageAsync();
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");

        var text = await page.Locator(".sso-provider").First.Locator(".sso-copy-list").InnerTextAsync();

        Assert.Contains($"{_env.BaseUrl}/sso/oidc/callback/{JellyfinEnvironment.ProviderId}", text);
        Assert.Contains(JellyfinEnvironment.ProviderId, text);
    }

    [Fact]
    public async Task Scopes_can_be_set_for_a_provider_that_needs_an_extra_one()
    {
        var page = await OpenConfigPageAsync();
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");

        // Authelia only reports groups when the groups scope is requested.
        await page.Locator(".sso-provider").First.Locator(".sso-provider-scopes")
            .FillAsync("openid profile email groups");
        await page.ClickAsync("#Save");

        var config = await WaitForConfigAsync(c => c["Providers"]!.AsArray()[0]!["Scopes"]!
            .AsArray().Any(s => s!.GetValue<string>() == "groups"));
        Assert.Equal(
            ["openid", "profile", "email", "groups"],
            config["Providers"]!.AsArray()[0]!["Scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task The_admin_page_saves_the_session_settings()
    {
        var page = await OpenConfigPageAsync();
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");

        await page.CheckAsync("#SignOutOfIdentityProvider", new PageCheckOptions { Force = true });
        await page.UncheckAsync("#AllowSelfServiceLinking", new PageUncheckOptions { Force = true });

        await page.ClickAsync("#Save");

        await WaitForConfigAsync(config =>
            config["SignOutOfIdentityProvider"]!.GetValue<bool>()
            && !config["AllowSelfServiceLinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_admin_page_shows_the_configured_provider()
    {
        var page = await OpenConfigPageAsync();
        await page.ClickAsync(".sso-tab[data-tab=\"provider\"]");

        var card = page.Locator(".sso-provider").First;
        Assert.Equal("Fake IdP", await card.Locator(".sso-provider-name").InputValueAsync());
        Assert.Equal(JellyfinEnvironment.ClientId, await card.Locator(".sso-provider-client").InputValueAsync());
    }

    /// <summary>
    /// Jellyfin's dashboard loads only the page element out of configPage.html, so a
    /// stylesheet in that file's &lt;head&gt; never reaches the DOM and the page renders
    /// with raw browser widgets. This catches that the day it happens again.
    /// </summary>
    [Fact]
    public async Task The_pages_own_stylesheet_reaches_the_dashboard()
    {
        var page = await OpenConfigPageAsync();

        var applied = await page.EvaluateAsync<bool>(
            """
            () => {
              const tab = document.querySelector('.sso-tab');
              if (!tab) return false;
              // The stylesheet gives every tab a 2px bottom border; the browser default
              // for a <button> has none.
              return getComputedStyle(tab).borderBottomWidth === '2px';
            }
            """);

        Assert.True(applied, "configPage.html's stylesheet did not reach the rendered page.");
    }

    /// <summary>
    /// Opens the plugin's page inside the dashboard with a seeded admin session, and fails
    /// with the page's own console output if the page never initialises.
    /// </summary>
    private async Task<IPage> OpenConfigPageAsync([CallerMemberName] string name = "config-page")
    {
        var traced = await TracedContext.StartAsync(
            _browser,
            new BrowserNewContextOptions
            {
                BaseURL = _env.BaseUrl,
                IgnoreHTTPSErrors = true,
            },
            name);
        _traced.Add(traced);
        var context = traced.Context;

        var credentials = new JsonObject
        {
            ["Servers"] = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = _env.ServerId,
                    ["AccessToken"] = _env.AdminToken,
                    ["UserId"] = _env.AdminUserId,
                    ["UserName"] = _env.AdminUserName,
                    ["ManualAddress"] = _env.BaseUrl,
                    ["LastConnectionMode"] = 2,
                    ["DateLastAccessed"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            },
        };

        await context.AddInitScriptAsync(
            "window.localStorage.setItem('jellyfin_credentials', "
            + JsonSerializer.Serialize(credentials.ToJsonString()) + ");"
            + "window.localStorage.setItem('_deviceId2', 'e2e-config-page');");

        var page = await context.NewPageAsync();

        // Jellyfin's web client routes plugin pages by display name, not by GUID.
        await BrowserSupport.GotoAsync(page, "/web/index.html#/configurationpage?name=SSO%20Authentication");

        try
        {
            // The mappings panel starts hidden behind its tab, so wait for the markup, not
            // for visibility, and only then switch to the tab the settings live on.
            await page.WaitForSelectorAsync("#GroupMappings", new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = BrowserSupport.WebClientTimeout,
            });
            // The mapping cards are rendered after the config request resolves.
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('.sso-mapping').length > 0",
                null,
                new PageWaitForFunctionOptions { Timeout = BrowserSupport.WebClientTimeout });

            await page.ClickAsync(".sso-tab[data-tab=\"mappings\"]");
            await page.WaitForSelectorAsync("#GroupMappings", new PageWaitForSelectorOptions { Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            await BrowserSupport.CaptureAsync(page, "config-page");
            Assert.Fail(
                "The plugin's configuration page did not initialise.\n"
                + $"url:     {page.Url}\n"
                + $"console: {string.Join(" | ", PageConsole.Of(page))}");
        }

        return page;
    }

    private async Task<JsonNode> WaitForMappingAsync(string group)
    {
        var config = await WaitForConfigAsync(c => c["GroupMappings"]!.AsArray()
            .Any(m => m!["Group"]!.GetValue<string>() == group));
        return JellyfinEnvironment.Mapping(config, group);
    }

    /// <summary>Polls the plugin config until the page's save has landed.</summary>
    private async Task<JsonNode> WaitForConfigAsync(Func<JsonNode, bool> saved)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var config = await _env.PluginConfigAsync();
            if (saved(config))
            {
                return config;
            }

            await Task.Delay(500);
        }

        Assert.Fail("The configuration page never saved the expected configuration.");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// The secret reference names where the secret is; it must never hold one. There is no
    /// field for it on the page, so it arrives as hand-written JSON — which is exactly how
    /// a literal secret used to end up in the plugin's configuration file.
    /// </summary>
    [Theory]
    [InlineData("s3cret-not-a-reference")]
    [InlineData("ENV:JELLYFIN_SSO_SECRET")]
    [InlineData("vault:secret/sso")]
    public async Task A_secret_reference_that_holds_the_secret_is_refused(string secretRef)
    {
        var result = await _env.TrySaveConfigAsync(config =>
        {
            var provider = config["Providers"]!.AsArray()[0]!;
            provider["ClientType"] = "Confidential";
            provider["ConfidentialSecretRef"] = secretRef;
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, result.Status);
        Assert.Contains("must name where the secret is", result.Body);

        // Nothing was saved, so the value never reached the configuration file.
        var saved = await _env.PluginConfigAsync();
        Assert.Null(saved["Providers"]!.AsArray()[0]!["ConfidentialSecretRef"]?.GetValue<string?>());
    }


    /// <summary>
    /// A library is named by its Jellyfin id, and the permission sync parses those ids on
    /// every login. One that is not a GUID therefore broke every SSO login on the server
    /// with a 500 — from a value only this endpoint can introduce. It is refused here, and
    /// the sync skips one that got in some other way.
    /// </summary>
    [Theory]
    [InlineData("not-a-library-id")]
    [InlineData("")]
    [InlineData("12345")]
    public async Task A_library_id_that_is_not_one_is_refused_at_the_door(string libraryId)
    {
        var result = await _env.TrySaveConfigAsync(config =>
            JellyfinEnvironment.Mapping(config, "media")["LibraryIds"] =
                new System.Text.Json.Nodes.JsonArray { libraryId });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, result.Status);
        Assert.Contains("is not a library id", result.Body);

        var saved = await _env.PluginConfigAsync();
        Assert.DoesNotContain(
            libraryId,
            JellyfinEnvironment.Mapping(saved, "media")["LibraryIds"]!.AsArray()
                .Select(id => id!.GetValue<string>()));
    }

    /// <summary>
    /// A signing algorithm the plugin does not recognise is dropped on the way to the
    /// validator, and a provider whose list is nothing but such names falls back to every
    /// asymmetric algorithm — more than whoever typed it asked for. Refused here, because
    /// the page is the only place somebody is around to be told.
    /// </summary>
    [Theory]
    [InlineData("RSA256")]
    [InlineData("none")]
    [InlineData("HS256")]
    public async Task A_signing_algorithm_the_plugin_does_not_accept_is_refused(string algorithm)
    {
        var result = await _env.TrySaveConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["SigningAlgorithms"] =
                new JsonArray { algorithm });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, result.Status);
        Assert.Contains("is not a signing algorithm this plugin accepts", result.Body);

        var saved = await _env.PluginConfigAsync();
        Assert.Empty(saved["Providers"]!.AsArray()[0]!["SigningAlgorithms"]!.AsArray());
    }

    /// <summary>
    /// And the one almost every provider uses saves, so the refusal above is about the
    /// name rather than about the field.
    /// </summary>
    [Fact]
    public async Task The_algorithm_a_provider_signs_with_can_be_pinned()
    {
        var result = await _env.TrySaveConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["SigningAlgorithms"] =
                new JsonArray { "RS256" });

        Assert.Equal(System.Net.HttpStatusCode.OK, result.Status);

        var saved = await _env.PluginConfigAsync();
        Assert.Equal(
            "RS256",
            Assert.Single(saved["Providers"]!.AsArray()[0]!["SigningAlgorithms"]!.AsArray())!
                .GetValue<string>());

        // Put back, so the login tests that follow run against the default shape.
        await _env.TrySaveConfigAsync(config =>
            config["Providers"]!.AsArray()[0]!["SigningAlgorithms"] = new JsonArray());
    }

    /// <summary>
    /// The page posts the configuration it loaded with, and both the links and the seen
    /// groups change underneath it — a login links an account, the Links tab removes one.
    /// Saving a provider therefore used to write that snapshot back: a removed link
    /// reappeared and the identity it belonged to signed into that account again. The
    /// server keeps its own lists whatever the body says.
    /// </summary>
    [Fact]
    public async Task Saving_the_configuration_cannot_rewrite_the_links()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-keepme", "keepmeuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var link = Assert.Single((await _env.PluginConfigAsync())["Links"]!.AsArray());
        var sub = link!["Sub"]!.GetValue<string>();

        // A save that carries a link nobody asked for, and drops the one that exists.
        var result = await _env.TrySaveConfigAsync(config =>
        {
            config["Links"] = new JsonArray(new JsonObject
            {
                ["ProviderId"] = JellyfinEnvironment.ProviderId,
                ["Sub"] = "sub-invented",
                ["JellyfinUserId"] = Guid.NewGuid().ToString(),
            });
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, result.Status);

        var saved = await _env.PluginConfigAsync();
        var kept = Assert.Single(saved["Links"]!.AsArray());
        Assert.Equal(sub, kept!["Sub"]!.GetValue<string>());
    }

    /// <summary>
    /// The other direction, which is what an administrator would actually hit: remove a
    /// link, then save a provider from the page that still remembers it.
    /// </summary>
    [Fact]
    public async Task A_link_removed_while_the_page_is_open_stays_removed()
    {
        var login = await _env.StartWebLoginAsync(
            new FakeUser("sub-gone", "goneuser", ["media"]));
        Assert.True(login.Succeeded, login.Body);

        var stale = await _env.PluginConfigAsync();
        var userId = Guid.Parse(
            stale["Links"]!.AsArray()[0]!["JellyfinUserId"]!.GetValue<string>());

        await _env.RemoveLinkAsync(userId);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());

        // The page saves with the links it loaded with, which still contain the removed one.
        var result = await _env.TrySaveConfigAsync(config => config["Links"] = stale["Links"]!.DeepClone());

        Assert.Equal(System.Net.HttpStatusCode.OK, result.Status);
        Assert.Empty((await _env.PluginConfigAsync())["Links"]!.AsArray());
    }

}
