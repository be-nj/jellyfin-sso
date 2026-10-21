using System.Net;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
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

        var images = new RemoteImages(
            new RecordingLogger<RemoteImages>(), _handler, TimeSpan.FromSeconds(5));

        return new ProfilePictureSync(
            _userManager, _providerManager, serverConfiguration, images, _log);
    }

    private Task<int> SavedImages()
        => Task.FromResult(_providerManager.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IProviderManager.SaveImage)));

    // ─── What it will fetch at all ────────────────────────────────────────────

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

    [Fact]
    public async Task A_picture_that_fails_to_be_written_leaves_the_old_one_alone()
    {
        var user = _userManager.GetUserById(_userId)!;
        user.ProfileImage = new ImageInfo("/old/profile.png");
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);
        _providerManager
            .When(p => p.SaveImage(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>()))
            .Throw(new IOException("disk full"));

        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        // Not cleared, not repointed at a file that was never written.
        await _userManager.DidNotReceiveWithAnyArgs().ClearProfileImageAsync(default!);
        Assert.Equal("/old/profile.png", user.ProfileImage!.Path);
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

        // No throw, nothing written, and the account keeps whatever it had. What the
        // fetch itself refuses is RemoteImagesTests' business.
        await Sync().SyncAsync(_userId, "https://idp.example.com/avatar.png");

        Assert.Equal(0, await SavedImages());
        Assert.Null(_userManager.GetUserById(_userId)!.ProfileImage);
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

    /// <summary>
    /// A body that reports how much of it was read, so a test can tell "refused it" from
    /// "read all of it and then refused it".
    /// </summary>
    private sealed class CountingStream : MemoryStream
    {
        private readonly Action<int> _counted;

        public CountingStream(byte[] bytes, Action<int> counted)
            : base(bytes)
        {
            _counted = counted;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            _counted(read);
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            _counted(read);
            return ValueTask.FromResult(read);
        }
    }

    /// <summary>A provider's answer, without a provider.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _type = "image/png";
        private byte[] _body = [];
        private bool _chunked;
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

        /// <summary>A body with no declared length, as a chunked response has.</summary>
        public void RespondChunked(string contentType, int bytes)
        {
            _status = HttpStatusCode.OK;
            _type = contentType;
            _body = new byte[bytes];
            _chunked = true;
            _throw = null;
        }

        /// <summary>How much of the body the client actually pulled.</summary>
        public long BytesDelivered { get; private set; }

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
                Content = _chunked
                    ? new StreamContent(new CountingStream(_body, n => BytesDelivered += n))
                    : new ByteArrayContent(_body),
            };

            if (_chunked)
            {
                // StreamContent over a MemoryStream still knows its length; a real
                // chunked reply does not, and that is the case under test.
                response.Content.Headers.ContentLength = null;
            }
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_type);
            return Task.FromResult(response);
        }
    }
}
