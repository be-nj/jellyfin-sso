using System.Net;
using Docker.DotNet;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// <see cref="ImagePull"/> without Docker: the start it is handed logs what Testcontainers
/// logs when a pull fails part-way, the way Testcontainers logs it, then throws what
/// creating the container throws.
/// </summary>
public class ImagePullTests
{
    private const string Image = "jellyfin/jellyfin:12.0";
    private const string RateLimit = "toomanyrequests: You have reached your unauthenticated pull rate limit";

    private static readonly Func<int, TimeSpan> NoPause = _ => TimeSpan.Zero;

    // Testcontainers' TraceProgress asks whether errors are wanted before it logs one,
    // and writes the layer id in front, "(null)" when the error names no layer.
    private static void PullError(ILogger log, string? layer, string error)
    {
        if (log.IsEnabled(LogLevel.Error))
        {
            log.LogError("ID={ID}: {Error}", layer, error);
        }
    }

    private static DockerImageNotFoundException NoSuchImage(string image)
        => new(HttpStatusCode.NotFound, $"{{\"message\":\"No such image: {image}\"}}\n");

    private static Task FailedPull(ILogger log)
    {
        PullError(log, "a1b2c3", RateLimit);
        throw NoSuchImage(Image);
    }

    [Fact]
    public async Task A_start_whose_image_did_not_arrive_is_tried_again()
    {
        var starts = 0;

        await ImagePull.RetryAsync(
            Image, log => ++starts == 1 ? FailedPull(log) : Task.CompletedTask, NoPause, new Recorder());

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task There_is_a_pause_after_each_failed_attempt_and_none_after_the_last()
    {
        var pauses = new List<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImagePull.RetryAsync(
            Image,
            FailedPull,
            attempt =>
            {
                pauses.Add(attempt);
                return TimeSpan.Zero;
            },
            new Recorder()));

        Assert.Equal([1, 2], pauses);
    }

    [Fact]
    public async Task A_start_that_never_gets_its_image_says_what_docker_said()
    {
        var starts = 0;

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImagePull.RetryAsync(
            Image,
            log =>
            {
                starts++;
                return FailedPull(log);
            },
            NoPause,
            new Recorder()));

        Assert.Equal(ImagePull.Attempts, starts);
        Assert.Contains(Image, e.Message, StringComparison.Ordinal);
        Assert.Contains($"ID=a1b2c3: {RateLimit}", e.Message, StringComparison.Ordinal);
        Assert.IsType<DockerImageNotFoundException>(e.InnerException);
    }

    [Fact]
    public async Task The_same_error_from_every_attempt_is_said_once_and_without_an_empty_layer_id()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImagePull.RetryAsync(
            Image,
            log =>
            {
                PullError(log, null, "short read: expected 300270 bytes but got 4096: unexpected EOF");
                throw NoSuchImage(Image);
            },
            NoPause,
            new Recorder()));

        Assert.DoesNotContain("ID=(null)", e.Message, StringComparison.Ordinal);
        Assert.Single(e.Message.Split("short read")[1..]);
    }

    [Fact]
    public async Task The_image_named_is_the_one_docker_missed_which_need_not_be_the_one_started()
    {
        // Testcontainers starts its resource reaper inside the first container's start.
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImagePull.RetryAsync(
            Image, _ => throw NoSuchImage("testcontainers/ryuk:0.12.0"), NoPause, new Recorder()));

        Assert.Contains("No such image: testcontainers/ryuk:0.12.0", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_that_reported_nothing_is_said_to_have_reported_nothing()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => ImagePull.RetryAsync(
            Image, _ => throw NoSuchImage(Image), NoPause, new Recorder()));

        Assert.Contains("reported no error while pulling", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_error_is_kept_even_when_the_logger_underneath_wants_nothing()
    {
        var silent = new Recorder { Minimum = LogLevel.None };

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ImagePull.RetryAsync(Image, FailedPull, NoPause, silent));

        Assert.Contains(RateLimit, e.Message, StringComparison.Ordinal);
        Assert.Empty(silent.Lines);
    }

    [Fact]
    public async Task What_testcontainers_logs_still_reaches_the_logger_it_would_have_used()
    {
        var underneath = new Recorder();

        await ImagePull.RetryAsync(
            Image,
            log =>
            {
                log.LogInformation("Docker container {Id} created", "0f3a");
                PullError(log, "a1b2c3", RateLimit);
                return Task.CompletedTask;
            },
            NoPause,
            underneath);

        Assert.Equal(["Docker container 0f3a created", $"ID=a1b2c3: {RateLimit}"], underneath.Lines);
    }

    [Fact]
    public async Task Any_other_failure_is_not_retried()
    {
        var starts = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => ImagePull.RetryAsync(
            Image,
            _ =>
            {
                starts++;
                throw new TimeoutException();
            },
            NoPause,
            new Recorder()));

        Assert.Equal(1, starts);
    }

    /// <summary>Stands in for the logger Testcontainers would have used.</summary>
    private sealed class Recorder : ILogger
    {
        public LogLevel Minimum { get; init; } = LogLevel.Information;

        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= Minimum && Minimum != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
