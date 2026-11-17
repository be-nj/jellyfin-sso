using System.Net;
using Docker.DotNet;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// <see cref="ImagePull"/> without Docker: the start it is handed logs what Testcontainers
/// logs when a pull fails part-way, then throws what creating the container throws.
/// </summary>
public class ImagePullTests
{
    private const string Image = "jellyfin/jellyfin:12.0";

    private static Task FailedPull(ILogger log)
    {
        log.LogError("ID={ID}: {Error}", "a1b2c3", "toomanyrequests: You have reached your unauthenticated pull rate limit");
        throw new DockerImageNotFoundException(HttpStatusCode.NotFound, $"{{\"message\":\"No such image: {Image}\"}}");
    }

    [Fact]
    public async Task A_start_whose_image_did_not_arrive_is_tried_again()
    {
        var starts = 0;

        await ImagePull.RetryAsync(Image, log => ++starts == 1 ? FailedPull(log) : Task.CompletedTask, _ => TimeSpan.Zero);

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task A_start_that_never_gets_its_image_says_what_docker_said()
    {
        var starts = 0;

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImagePull.RetryAsync(Image, log => { starts++; return FailedPull(log); }, _ => TimeSpan.Zero));

        Assert.Equal(ImagePull.Attempts, starts);
        Assert.Contains(Image, e.Message, StringComparison.Ordinal);
        Assert.Contains("toomanyrequests", e.Message, StringComparison.Ordinal);
        Assert.IsType<DockerImageNotFoundException>(e.InnerException);
    }

    [Fact]
    public async Task Any_other_failure_is_not_retried()
    {
        var starts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ImagePull.RetryAsync(Image, _ => { starts++; throw new TimeoutException(); }, _ => TimeSpan.Zero));

        Assert.Equal(1, starts);
    }
}
