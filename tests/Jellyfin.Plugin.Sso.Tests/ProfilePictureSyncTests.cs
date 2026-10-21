using System.Net;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The profile picture is the one thing this plugin fetches from an address a token hands
/// it, which makes the rules around that fetch the interesting part: what it refuses, how
/// much it will read, and that a login still succeeds when the picture does not arrive.
/// </summary>
public sealed class ProfilePictureSyncTests : IDisposable
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    private readonly PluginHarness _harness = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly IProviderManager _providerManager = Substitute.For<IProviderManager>();
    private readonly RecordingLogger<ProfilePictureSync> _log = new();
    private readonly Handler _handler = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly string _root = Directory.CreateTempSubdirectory("sso-pictures-").FullName;

    public ProfilePictureSyncTests()
    {
        _harness.Configuration.SyncProfilePictures = true;

        var user = new User(
            "ben",
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");
        _userManager.GetUserById(_userId).Returns(user);
    }

    private ProfilePictureSync Sync()
    {
        var paths = Substitute.For<IServerApplicationPaths>();
        paths.UserConfigurationDirectoryPath.Returns(_root);
        var serverConfiguration = Substitute.For<IServerConfigurationManager>();
        serverConfiguration.ApplicationPaths.Returns(paths);

        return new ProfilePictureSync(_userManager, _providerManager, serverConfiguration, _log, _handler);
    }

    private Task<int> SavedImages()
        => Task.FromResult(_providerManager.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IProviderManager.SaveImage)));

    // ─── What it will fetch at all ────────────────────────────────────────────

    [Theory]
    [InlineData("https://idp.example.com/avatar.png", true)]
    [InlineData("http://idp.example.com/avatar.png", true)]
    // A picture claim is a string somebody else chose. These would have the server read
    // its own disk, or its own metadata service, on their behalf.
    [InlineData("file:///etc/shadow", false)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", false)]
    [InlineData("/avatars/ben.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_absolute_http_address_is_fetched(string? url, bool expected)
        => Assert.Equal(expected, ProfilePictureSync.Fetchable(url, out _));

    [Fact]
    public async Task Nothing_happens_while_the_setting_is_off()
    {
        _harness.Configuration.SyncProfilePictures = false;
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);

        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Equal(0, _handler.Requests);
        Assert.Equal(0, await SavedImages());
    }

    [Fact]
    public async Task An_excluded_user_keeps_the_picture_they_chose()
    {
        _harness.Configuration.SyncExcludedUserIds.Add(_userId);
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);

        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Equal(0, _handler.Requests);
    }

    // ─── What comes back ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_picture_the_provider_serves_becomes_the_profile_picture()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);

        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Equal(1, await SavedImages());
        var user = _userManager.GetUserById(_userId)!;
        Assert.NotNull(user.ProfileImage);
        Assert.EndsWith("profile.png", user.ProfileImage!.Path, StringComparison.Ordinal);
        Assert.Contains("ben", user.ProfileImage.Path, StringComparison.Ordinal);
        await _userManager.Received(1).UpdateUserAsync(user);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    public async Task Anything_that_is_not_an_image_Jellyfin_reads_is_left_alone(string type)
    {
        _handler.Respond(HttpStatusCode.OK, type, Png);

        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar");

        Assert.Equal(0, await SavedImages());
    }

    [Fact]
    public async Task A_picture_too_large_to_be_an_avatar_is_left_alone()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", new byte[ProfilePictureSync.MaxBytes + 1]);

        await Sync().SyncAsync(_userId, "https://idp.example.com/huge.png");

        Assert.Equal(0, await SavedImages());
    }

    [Fact]
    public async Task A_provider_that_answers_with_an_error_changes_nothing()
    {
        _handler.Respond(HttpStatusCode.NotFound, "image/png", []);

        await Sync().SyncAsync(_userId, "https://idp.example.com/gone.png");

        Assert.Equal(0, await SavedImages());
    }

    [Fact]
    public async Task Jellyfins_own_image_pipeline_throwing_does_not_fail_the_login()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);
        _providerManager
            .When(p => p.SaveImage(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>()))
            .Throw(new ArgumentException("not an image this server will take"));

        // No throw: the account keeps the picture it had and the login carries on.
        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Contains(_log.Warnings, w => w.Contains("profile picture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_that_cannot_be_reached_does_not_fail_the_login()
    {
        _handler.Throw(new HttpRequestException("no route to host"));

        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Equal(0, await SavedImages());
        Assert.Contains(_log.Warnings, w => w.Contains("profile picture", StringComparison.Ordinal));
    }

    // ─── Doing it twice ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_picture_on_the_next_login_is_not_written_again()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);
        var sync = Sync();

        await sync.SyncAsync(_userId, "https://idp.example.com/avatar.png");
        await sync.SyncAsync(_userId, "https://idp.example.com/avatar.png");

        // Fetched both times — the provider is the one who knows whether it changed — but
        // written once, so the image cache and the client's tag stay put.
        Assert.Equal(2, _handler.Requests);
        Assert.Equal(1, await SavedImages());
    }

    [Fact]
    public async Task A_new_picture_replaces_the_one_on_the_account()
    {
        var sync = Sync();
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);
        await sync.SyncAsync(_userId, "https://idp.example.com/avatar.png");

        _handler.Respond(HttpStatusCode.OK, "image/jpeg", [1, 2, 3, 4, 5]);
        await sync.SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Equal(2, await SavedImages());
        await _userManager.Received(1).ClearProfileImageAsync(Arg.Any<User>());
        Assert.EndsWith(
            "profile.jpg", _userManager.GetUserById(_userId)!.ProfileImage!.Path, StringComparison.Ordinal);
    }

    // ─── Where it writes ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("ben")]
    [InlineData("../../etc/cron.d")]
    [InlineData("/etc/cron.d")]
    [InlineData("..")]
    [InlineData(".")]
    public void The_picture_is_written_under_Jellyfins_own_user_folder(string username)
    {
        var user = new User(
            username,
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");

        var path = Sync().PicturePath(user, ".png");

        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        Assert.EndsWith("profile.png", path, StringComparison.Ordinal);
        Assert.DoesNotContain("..", path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_cannot_be_a_folder_falls_back_to_the_users_id()
    {
        var user = new User(
            "/",
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");

        Assert.Contains(user.Id.ToString("N"), Sync().PicturePath(user, ".jpg"), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _harness.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>A provider's answer, without a provider.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _type = "image/png";
        private byte[] _body = [];
        private Exception? _throw;

        public int Requests { get; private set; }

        public void Respond(HttpStatusCode status, string contentType, byte[] body)
        {
            _status = status;
            _type = contentType;
            _body = body;
            _throw = null;
        }

        public void Throw(Exception ex) => _throw = ex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (_throw is not null)
            {
                return Task.FromException<HttpResponseMessage>(_throw);
            }

            var response = new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_type);
            return Task.FromResult(response);
        }
    }
}
