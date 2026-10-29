using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DevKit.Web.Components;

/// <summary>
/// Base for a component with a floating panel that a click, or focus, anywhere outside it closes: the dropdowns, the
/// popover and the command search. It adds app.js's outside-click listener after the render that opens the
/// panel and removes it after the render that closes it, or when the component is disposed, so an open panel
/// has exactly one listener and a closed one has none.
/// </summary>
public abstract class ClickOutsideComponent : ComponentBase, IAsyncDisposable
{
    private const string RegisterCall = "devKit.registerClickOutside";
    private const string UnregisterCall = "devKit.unregisterClickOutside";
    private const string FocusCall = "devKit.focusEl";
    private const string RevealCall = "devKit.revealEl";

    private readonly ClickOutsideListener listener = new();
    private DotNetObjectReference<ClickOutsideComponent>? selfRef;
    private ILogger? log;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;

    /// <summary>The component's outermost element (put @ref="Root" on it); a click inside it is not an outside click.</summary>
    protected ElementReference Root { get; set; }

    /// <summary>Whether the panel is on screen after the current render.</summary>
    protected abstract bool IsPanelOpen { get; }

    /// <summary>
    /// An id for the panel's list that no other panel on the page shares, so the trigger and the search box can point
    /// screen readers at it and at its options.
    /// </summary>
    protected string PanelId { get; } = "pnl" + Guid.NewGuid().ToString("N")[..8];

    // Created per derived type, so each component keeps its own log category.
    private ILogger Log => log ??= LoggerFactory.CreateLogger(GetType());

    /// <summary>Closes the panel after a click outside it.</summary>
    protected abstract Task CloseFromOutsideAsync();

    /// <summary>The derived component's after-render work. The listener is already in step with the panel.</summary>
    protected virtual Task OnAfterRenderedAsync(bool firstRender) => Task.CompletedTask;

    // Sealed so a derived component cannot skip the listener update; it overrides OnAfterRenderedAsync instead.
    // OnAfterRender never runs during prerender, so nothing is registered there and nothing has to be undone.
    protected sealed override async Task OnAfterRenderAsync(bool firstRender)
    {
        switch (listener.Sync(IsPanelOpen))
        {
            case ListenerChange.Add:
                selfRef ??= DotNetObjectReference.Create(this);
                await InvokeJsAsync(RegisterCall, Root, selfRef);
                break;
            case ListenerChange.Remove:
                await InvokeJsAsync(UnregisterCall, Root);
                break;
        }
        await OnAfterRenderedAsync(firstRender);
    }

    /// <summary>Called by app.js on a click or focus outside, after its one-shot listener has removed itself.</summary>
    [JSInvokable]
    public Task CloseDropdown()
    {
        listener.Dropped();
        return IsPanelOpen ? CloseFromOutsideAsync() : Task.CompletedTask;
    }

    /// <summary>Moves focus to an element of the panel, such as its search box.</summary>
    protected Task FocusAsync(ElementReference element) => InvokeJsAsync(FocusCall, element);

    /// <summary>Scrolls the option the arrow keys reached into view; focus stays in the search box, so the browser does not.</summary>
    protected Task RevealAsync(string elementId) => InvokeJsAsync(RevealCall, elementId);

    /// <summary>
    /// Calls a devKit function. A failure only costs the panel its focus or its outside click, so it is logged
    /// at Error and the component carries on. A call made after the page closed is not a failure: nothing is left to
    /// listen, so it is logged at Debug.
    /// </summary>
    protected async Task InvokeJsAsync(string identifier, params object?[] args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException ex)
        {
            Log.LogDebug(ex, "{Component}: {Call} skipped because the page had closed", GetType().Name, identifier);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException)
        {
            Log.LogError(ex, "{Component}: {Call} failed", GetType().Name, identifier);
        }
    }

    /// <summary>Removes a listener that is still on before the .NET reference app.js holds is disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (listener.Release())
        {
            await InvokeJsAsync(UnregisterCall, Root);
        }
        selfRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>What a component has to do with its outside-click listener after a render.</summary>
internal enum ListenerChange
{
    None,
    Add,
    Remove
}

/// <summary>
/// Whether a component's outside-click listener is on, kept apart from the component so it can be tested without
/// a browser. It is used only on the circuit's render context, which runs one call at a time, so it takes no lock.
/// </summary>
internal sealed class ClickOutsideListener
{
    private bool listening;
    private bool released;

    /// <summary>Brings the listener in step with the panel: Add when it opened, Remove when it closed, otherwise None.</summary>
    public ListenerChange Sync(bool open)
    {
        if (released || open == listening)
        {
            return ListenerChange.None;
        }
        listening = open;
        return open ? ListenerChange.Add : ListenerChange.Remove;
    }

    /// <summary>app.js's one-shot listener removed itself after reporting an outside click.</summary>
    public void Dropped() => listening = false;

    /// <summary>Ends the listener's life. True when it is still on and the caller has to remove it.</summary>
    public bool Release()
    {
        var stillListening = listening && !released;
        released = true;
        listening = false;
        return stillListening;
    }
}
