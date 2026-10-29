using Microsoft.JSInterop;

namespace DevKit.Tests;

/// <summary>
/// A JS runtime for components under test: it accepts every call, records it, and can fail one call the way a
/// broken interop call or a closed page does.
/// </summary>
internal sealed class RecordingJsRuntime : IJSRuntime
{
    public List<(string Identifier, object?[] Args)> Calls { get; } = new();

    /// <summary>A call that fails the way a broken interop call does.</summary>
    public string? FailOn { get; set; }

    /// <summary>What <see cref="FailOn"/> throws: a broken call by default, or a page that has closed.</summary>
    public Func<string, Exception> Failure { get; set; } = identifier => new JSException($"{identifier} failed in the browser");

    public int Count(string identifier) => Calls.Count(c => c.Identifier == identifier);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Calls.Add((identifier, args ?? Array.Empty<object?>()));
        return identifier == FailOn
            ? ValueTask.FromException<TValue>(Failure(identifier))
            : ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);
}
