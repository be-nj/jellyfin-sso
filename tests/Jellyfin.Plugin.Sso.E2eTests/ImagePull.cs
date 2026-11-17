using System.Collections.Concurrent;
using Docker.DotNet;
using DotNet.Testcontainers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Starts a container again when its image did not arrive, and says what the registry
/// answered when it never does.
///
/// Testcontainers pulls a missing image and retries only when the pull call throws. The
/// Docker daemon answers a pull with HTTP 200 and then streams progress, and a pull that
/// fails part-way, a registry rate limit or a broken layer download, reports its error as
/// one more message in that stream. Docker.DotNet passes the message to a progress
/// callback without throwing, and Testcontainers' callback only logs it. So the pull
/// counts as done and the next call, creating the container, fails with "No such image",
/// with the registry's own message nowhere in the output. That was #291: the whole suite
/// against one server failing in eleven seconds on an image tag that exists.
///
/// The container's logger is replaced here with one that keeps those errors, so a start
/// that still fails after the last attempt names them.
/// </summary>
internal static class ImagePull
{
    public const int Attempts = 3;

    /// <summary>
    /// Runs <paramref name="start"/>, which builds the container with the logger it is
    /// handed and starts it, until the image is there or <see cref="Attempts"/> starts
    /// have failed for want of it. Any other failure is not retried.
    /// </summary>
    public static Task RetryAsync(string image, Func<ILogger, Task> start)
        => RetryAsync(image, start, attempt => TimeSpan.FromSeconds(15 * attempt));

    internal static async Task RetryAsync(string image, Func<ILogger, Task> start, Func<int, TimeSpan> delay)
    {
        var log = new RegistryErrors(ConsoleLogger.Instance);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await start(log);
                return;
            }
            catch (DockerImageNotFoundException e)
            {
                if (attempt == Attempts)
                {
                    throw new InvalidOperationException(Explain(image, log.Messages), e);
                }

                await Task.Delay(delay(attempt));
            }
        }
    }

    internal static string Explain(string image, IReadOnlyCollection<string> registry)
        => registry.Count == 0
            ? $"Pulling {image} did not produce the image in {Attempts} attempts, and Docker reported no error while pulling."
            : $"Pulling {image} failed {Attempts} times. Docker said: {string.Join("; ", registry.Select(WithoutLayerId).Distinct())}";

    // Testcontainers writes "ID=<layer>: <error>", and a pull that fails on the manifest
    // or the connection has no layer to name.
    private static string WithoutLayerId(string message)
        => message.StartsWith("ID=(null): ", StringComparison.Ordinal) ? message["ID=(null): ".Length..] : message;

    /// <summary>
    /// Keeps every error Testcontainers logs, which is where a pull's failure ends up, and
    /// passes everything on to the logger Testcontainers would have used otherwise.
    /// </summary>
    private sealed class RegistryErrors(ILogger inner) : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Error || inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                _messages.Enqueue(formatter(state, exception));
            }

            if (inner.IsEnabled(logLevel))
            {
                inner.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
