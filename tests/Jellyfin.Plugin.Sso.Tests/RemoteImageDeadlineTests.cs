using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Sso.Web;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The fetch has a deadline, and the body is part of the fetch.
///
/// A profile picture is fetched while a login waits for it, and the address comes from a
/// claim in somebody's token. The fetcher reads headers first — so that a body nobody
/// wants is never buffered — and the reads that follow had only the caller's cancellation
/// token on them. A host that answers promptly and then says nothing is the shape that
/// exploits: valid headers, a plausible length, and silence.
///
/// Against a real listener rather than a stubbed stream, because the question is what the
/// HTTP stack does with a connection that stalls, not what a fake does when asked to.
/// </summary>
public sealed class RemoteImageDeadlineTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(400);

    /// <summary>Generous: the assertion is "the deadline held", not "it held to the millisecond".</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly RecordingLogger<RemoteImages> _log = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpListener> _listeners = [];

    /// <summary>
    /// A listener that answers with the headers of a picture and then holds the connection
    /// open without sending the body. Returns its address.
    /// </summary>
    private Uri StallingHost(string? headers = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(_stop.Token)
                        .ConfigureAwait(false);
                    await using var stream = client.GetStream();

                    var request = new byte[8 * 1024];
                    await stream.ReadAsync(request, _stop.Token).ConfigureAwait(false);

                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(headers ?? Answer), _stop.Token)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);

                    // And then nothing, for longer than any deadline under test.
                    await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The test is over.
            }
            catch (SocketException)
            {
                // The client gave up first, which is the point.
            }
            catch (IOException)
            {
                // Same.
            }
        });

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}/avatar.png");
    }

    private const string Answer =
        "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 4096\r\n\r\n";

    private RemoteImages Images() => new(_log, handler: null, timeout: Deadline);

    [Fact]
    public async Task A_picture_whose_body_never_arrives_is_abandoned_at_the_deadline()
    {
        var url = StallingHost();
        var images = Images();

        var clock = Stopwatch.StartNew();
        var picture = await images.GetAsync(
            url, maxBytes: 256 * 1024, authority: url, allowPrivateNetworks: true);
        clock.Stop();

        Assert.Null(picture);
        Assert.True(clock.Elapsed < Patience, $"the fetch was still waiting after {clock.Elapsed}");
    }

    /// <summary>The documents the plugin reads from a provider go down the same path.</summary>
    [Fact]
    public async Task A_document_whose_body_never_arrives_is_abandoned_too()
    {
        var url = StallingHost(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 4096\r\n\r\n");
        var images = Images();

        var clock = Stopwatch.StartNew();
        var bytes = await images.GetBytesAsync(
            url, maxBytes: 256 * 1024, authority: url, allowPrivateNetworks: true);
        clock.Stop();

        Assert.Null(bytes);
        Assert.True(clock.Elapsed < Patience, $"the fetch was still waiting after {clock.Elapsed}");
    }

    /// <summary>
    /// A deadline of its own, not the caller's: a login that is still waiting must still
    /// get its answer, and a picture that fails must not fail the login.
    /// </summary>
    [Fact]
    public async Task The_caller_is_not_the_one_who_has_to_cancel()
    {
        var url = StallingHost();
        var images = Images();

        // An uncancelled caller — exactly how the callback calls this.
        var clock = Stopwatch.StartNew();
        var picture = await images.GetAsync(
            url, maxBytes: 256 * 1024, authority: url, allowPrivateNetworks: true,
            ct: CancellationToken.None);
        clock.Stop();

        Assert.Null(picture);
        Assert.True(clock.Elapsed < Patience, $"the fetch was still waiting after {clock.Elapsed}");
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        _stop.Dispose();
    }
}
