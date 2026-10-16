using Microsoft.Extensions.Logging;

namespace DevKit.Tests;

/// <summary>Keeps the level and exception of every entry, for tests where the log is the only trace of a failure.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, exception));
        }
    }
}
