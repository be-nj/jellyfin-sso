using System.Net;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The one place the plugin fetches a picture from an address somebody else chose — a
/// provider's brand document for the login button, a claim in a token for a profile
/// picture. Neither address is trustworthy, so these are the rules that make the fetch
/// safe, tested here rather than twice over in each caller.
/// </summary>
public sealed class RemoteImagesTests
{
    private const int Cap = 256 * 1024;
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    private readonly Handler _handler = new();
    private readonly RecordingLogger<RemoteImages> _log = new();

    private RemoteImages Images() => new(_log, _handler, TimeSpan.FromSeconds(5));

    private static Uri Url(string path = "/picture.png") => new("https://idp.example.com" + path);

    // ─── What it will fetch at all ────────────────────────────────────────────

    [Theory]
    [InlineData("https://idp.example.com/avatar.png", true)]
    [InlineData("http://idp.example.com/avatar.png", true)]
    // A claim or a brand document is somebody else's text. These would have the server
    // read its own disk, or its own metadata service, on their behalf.
    [InlineData("file:///etc/shadow", false)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", false)]
    [InlineData("/avatars/ben.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_an_absolute_http_address_is_fetched(string? url, bool expected)
        => Assert.Equal(expected, RemoteImages.Fetchable(url, out _));

    // ─── What comes back ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_picture_comes_back_with_the_type_the_server_gave_it()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", Png);

        var image = await Images().GetAsync(Url(), Cap);

        Assert.NotNull(image);
        Assert.Equal(Png, image!.Bytes);
        Assert.Equal("image/png", image.ContentType);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("image/x-icon")]
    public async Task The_types_Jellyfin_reads_are_accepted(string type)
    {
        _handler.Respond(HttpStatusCode.OK, type, Png);

        Assert.NotNull(await Images().GetAsync(Url(), Cap));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    // SVG is a document that can carry script, not a picture. The logo fetch used to take
    // anything beginning with "image/", which included this one.
    [InlineData("image/svg+xml")]
    public async Task Anything_else_is_refused(string type)
    {
        _handler.Respond(HttpStatusCode.OK, type, Png);

        Assert.Null(await Images().GetAsync(Url(), Cap));
    }

    [Fact]
    public async Task A_body_larger_than_asked_for_is_refused()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", new byte[Cap + 1]);

        Assert.Null(await Images().GetAsync(Url(), Cap));
    }

    [Fact]
    public async Task A_body_that_never_says_how_long_it_is_is_still_capped()
    {
        // A chunked response carries no Content-Length, so a cap read off the header
        // bounds nothing — and reading the response whole buffers it all first.
        _handler.RespondChunked("image/png", Cap * 8);

        Assert.Null(await Images().GetAsync(Url(), Cap));

        Assert.True(
            _handler.BytesDelivered < Cap * 4,
            $"Read {_handler.BytesDelivered} bytes of a {Cap * 8} byte body before giving up.");
    }

    [Fact]
    public async Task An_error_is_not_a_picture()
    {
        _handler.Respond(HttpStatusCode.NotFound, "image/png", []);

        Assert.Null(await Images().GetAsync(Url(), Cap));
    }

    [Fact]
    public async Task An_empty_body_is_not_a_picture()
    {
        _handler.Respond(HttpStatusCode.OK, "image/png", []);

        Assert.Null(await Images().GetAsync(Url(), Cap));
    }

    [Fact]
    public async Task A_host_that_is_not_there_returns_nothing_rather_than_throwing()
    {
        _handler.Throw(new HttpRequestException("no route to host"));

        Assert.Null(await Images().GetAsync(Url(), Cap));
    }

    [Fact]
    public async Task The_caller_going_away_is_not_swallowed()
    {
        _handler.Throw(new TaskCanceledException());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Images().GetAsync(Url(), Cap, cancelled.Token));
    }

    /// <summary>A server's answer, without a server.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _type = "image/png";
        private byte[] _body = [];
        private bool _chunked;
        private Exception? _throw;

        public long BytesDelivered { get; private set; }

        public void Respond(HttpStatusCode status, string contentType, byte[] body)
        {
            _status = status;
            _type = contentType;
            _body = body;
            _chunked = false;
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
                // A MemoryStream knows its length; a real chunked reply does not, and
                // that is the case under test.
                response.Content.Headers.ContentLength = null;
            }

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_type);
            return Task.FromResult(response);
        }
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
}
