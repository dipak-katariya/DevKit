using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DevKit.Tests;

/// <summary>
/// Keeps every entry's level, rendered message, exception and named properties, and every scope opened, for
/// tests that check what a log line says, and what it must never say.
/// </summary>
internal sealed class MessageRecordingLogger<T> : ILogger<T>
{
    private readonly List<Entry> _entries = new();
    private readonly List<string> _scopes = new();

    /// <param name="Properties">The structured values by name, the message template under "{OriginalFormat}" included.</param>
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Properties)
    {
        /// <summary>The rendered message and every property value, as one text to search.</summary>
        public string AllText => Message + "\n" + string.Join("\n", Properties.Select(p => $"{p.Key}={p.Value}"));
    }

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Every scope opened, as its text and its named values; a scope can name a URL as much as a line can.</summary>
    public IReadOnlyList<string> Scopes
    {
        get
        {
            lock (_scopes)
            {
                return _scopes.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var text = state + "\n" + string.Join("\n", Pairs(state).Select(p => $"{p.Key}={p.Value}"));
        lock (_scopes)
        {
            _scopes.Add(text);
        }
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in Pairs(state))
        {
            properties[key] = value;
        }

        var entry = new Entry(logLevel, formatter(state, exception), exception, properties);
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private static IEnumerable<KeyValuePair<string, object?>> Pairs<TState>(TState state) =>
        state as IEnumerable<KeyValuePair<string, object?>> ?? [];
}

/// <summary>
/// Hands each log category its own <see cref="MessageRecordingLogger{T}"/>, for tests that check what a whole
/// pipeline logs, whoever writes it.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, MessageRecordingLogger<RecordingLoggerProvider>> _loggers = new(StringComparer.Ordinal);

    /// <summary>The loggers handed out so far, by category.</summary>
    public IReadOnlyDictionary<string, MessageRecordingLogger<RecordingLoggerProvider>> ByCategory => _loggers;

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, _ => new MessageRecordingLogger<RecordingLoggerProvider>());

    public void Dispose()
    {
        // The recording loggers hold nothing to release, and a test may read them after the container is disposed.
    }
}
