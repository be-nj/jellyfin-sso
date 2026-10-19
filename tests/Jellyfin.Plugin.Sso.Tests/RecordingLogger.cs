using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Keeps what was logged so a test can assert on it. Several of this plugin's safety nets
/// do nothing except write a line an administrator needs — an untested log line is a
/// feature nobody has checked.
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<string> Warnings => Messages(LogLevel.Warning);

    public IReadOnlyList<string> Messages(LogLevel level)
        => _lines.Where(l => l.Level == level).Select(l => l.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _lines.Add((logLevel, formatter(state, exception)));
}
