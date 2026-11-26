using System.Net;
using System.Text;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The provider logo is fetched once and remembered for twelve hours, on behalf of an
/// anonymous route. What it remembers has to be what the provider has, not what happened
/// to the visitor whose request started the fetch.
/// </summary>
public sealed class ProviderLogosTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    [Fact]
    public async Task A_visitor_who_gives_up_does_not_leave_the_login_page_without_its_logo()
    {
        // #342: the caller's cancellation used to reach the fetch, was caught as a failure,
        // and "no logo" was cached for twelve hours.
        var brandRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, ct) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/v3/core/brands/current/":
                    brandRequested.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return Answer("application/json", Encoding.UTF8.GetBytes("""{"branding_favicon":"/logo.png"}"""));
                case "/logo.png":
                    return Answer("image/png", Png);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
        using var images = new RemoteImages(NullLogger.Instance, handler, TimeSpan.FromSeconds(5));
        using var logos = new ProviderLogos(images, NullLogger<ProviderLogos>.Instance);
        var provider = new ProviderConfig { Id = "p", Authority = "https://idp.example.com" };

        using var visitor = new CancellationTokenSource();
        var first = logos.GetAsync(provider, visitor.Token);
        await brandRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await visitor.CancelAsync();

        Assert.Null(await first);

        release.SetResult();
        var logo = await logos.GetAsync(provider);

        Assert.NotNull(logo);
        Assert.Equal(Png, logo!.Bytes);
    }

    [Fact]
    public async Task Visitors_arriving_together_share_one_fetch()
    {
        var brandRequests = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v3/core/brands/current/")
            {
                Interlocked.Increment(ref brandRequests);
                await release.Task.WaitAsync(ct);
                return Answer("application/json", Encoding.UTF8.GetBytes("""{"branding_favicon":"/logo.png"}"""));
            }

            return Answer("image/png", Png);
        });
        using var images = new RemoteImages(NullLogger.Instance, handler, TimeSpan.FromSeconds(5));
        using var logos = new ProviderLogos(images, NullLogger<ProviderLogos>.Instance);
        var provider = new ProviderConfig { Id = "p", Authority = "https://idp.example.com" };

        var waiting = Enumerable.Range(0, 5).Select(_ => logos.GetAsync(provider)).ToList();
        release.SetResult();
        var results = await Task.WhenAll(waiting);

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, brandRequests);
    }

    private static HttpResponseMessage Answer(string type, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => answer(request, ct);
    }
}
