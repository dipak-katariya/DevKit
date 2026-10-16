using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace DevKit.Tests;

// A test renderer has to subclass Renderer and read its frames, which the framework warns is internal to Blazor.
#pragma warning disable BL0006

/// <summary>
/// Renders a component in-process and raises DOM events at its elements the way the Blazor Server circuit
/// does, so a test drives the real markup and its event wiring without a browser. It knows nothing of the
/// order a browser raises events in: each test raises them in that order itself.
/// </summary>
internal sealed class ComponentHarness : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly FrameRenderer _renderer;

    private ComponentHarness(ServiceProvider services)
    {
        _services = services;
        _renderer = new FrameRenderer(services);
    }

    /// <summary>One element as last rendered: its plain attributes, the events it listens for, and the text inside it.</summary>
    public sealed record Element(
        string Name, IReadOnlyDictionary<string, object?> Attributes, IReadOnlyDictionary<string, ulong> Handlers, string Text)
    {
        public string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    /// <summary>
    /// A harness whose components resolve the given services. JS interop resolves too, but refuses every call, and
    /// logging (ILogger&lt;T&gt; and ILoggerFactory) resolves to loggers that drop everything. A test can register its own
    /// of either to replace it.
    /// </summary>
    public static ComponentHarness Create(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJSRuntime, NoJsRuntime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        register(services);
        return new ComponentHarness(services.BuildServiceProvider());
    }

    /// <summary>Renders the component as a root with the given parameters and returns its id.</summary>
    public Task<int> RenderAsync<TComponent>(IDictionary<string, object?> parameters) where TComponent : IComponent =>
        _renderer.RenderRootAsync(typeof(TComponent), ParameterView.FromDictionary(parameters));

    /// <summary>The component's own elements in document order. Markup rendered by its child components is not included.</summary>
    public Task<List<Element>> ElementsAsync(int componentId) => _renderer.ElementsAsync(componentId);

    /// <summary>
    /// Raises an event at the first matching element and waits for its handler to finish. Returns false when the
    /// element does not listen for that event: a browser raises it all the same, and Blazor never hears of it.
    /// </summary>
    public async Task<bool> RaiseAsync(int componentId, Func<Element, bool> match, string eventName, EventArgs args)
    {
        var element = (await ElementsAsync(componentId)).FirstOrDefault(match)
            ?? throw new InvalidOperationException($"No rendered element matches, so '{eventName}' cannot be raised.");
        if (!element.Handlers.TryGetValue(eventName, out var handlerId))
        {
            return false;
        }

        await _renderer.DispatchAsync(handlerId, args);
        return true;
    }

    /// <summary>Removes a rendered root component and disposes it, the way leaving its page or tab does.</summary>
    public Task CloseAsync(int componentId) => _renderer.CloseAsync(componentId);

    /// <summary>Runs work on the renderer's dispatcher, the way the circuit runs a call from JS into a component.</summary>
    public Task InvokeFromJsAsync(Func<Task> work) => _renderer.InvokeAsync(work);

    public void Dispose()
    {
        _renderer.Dispose();
        _services.Dispose();
    }

    private sealed class FrameRenderer : Renderer
    {
        private Exception? _failure;

        public FrameRenderer(IServiceProvider services) : base(services, NullLoggerFactory.Instance)
        {
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<int> RenderRootAsync(Type componentType, ParameterView parameters)
        {
            var componentId = await Dispatcher.InvokeAsync(async () =>
            {
                var id = AssignRootComponentId(InstantiateComponent(componentType));
                await RenderRootComponentAsync(id, parameters);
                return id;
            });
            ThrowIfFailed();
            return componentId;
        }

        public Task<List<Element>> ElementsAsync(int componentId) =>
            Dispatcher.InvokeAsync(() => ReadElements(GetCurrentRenderTreeFrames(componentId)));

        public async Task DispatchAsync(ulong handlerId, EventArgs args)
        {
            await Dispatcher.InvokeAsync(() => DispatchEventAsync(handlerId, null, args));
            ThrowIfFailed();
        }

        public async Task CloseAsync(int componentId)
        {
            await Dispatcher.InvokeAsync(() => RemoveRootComponent(componentId));
            ThrowIfFailed();
        }

        public async Task InvokeAsync(Func<Task> work)
        {
            await Dispatcher.InvokeAsync(work);
            ThrowIfFailed();
        }

        protected override void HandleException(Exception exception) => _failure ??= exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        private void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                ExceptionDispatchInfo.Throw(_failure);
            }
        }

        private static List<Element> ReadElements(ArrayRange<RenderTreeFrame> frames)
        {
            var elements = new List<Element>();
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType == RenderTreeFrameType.Element)
                {
                    elements.Add(ReadElement(frames.Array, i));
                }
            }
            return elements;
        }

        // An element's own attributes come straight after it, before its first child frame.
        private static Element ReadElement(RenderTreeFrame[] frames, int index)
        {
            var end = index + frames[index].ElementSubtreeLength;
            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            var handlers = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var i = index + 1;
            for (; i < end && frames[i].FrameType == RenderTreeFrameType.Attribute; i++)
            {
                if (frames[i].AttributeEventHandlerId != 0)
                {
                    handlers[frames[i].AttributeName] = frames[i].AttributeEventHandlerId;
                }
                else
                {
                    attributes[frames[i].AttributeName] = frames[i].AttributeValue;
                }
            }

            var text = new StringBuilder();
            for (; i < end; i++)
            {
                if (frames[i].FrameType == RenderTreeFrameType.Text)
                {
                    text.Append(frames[i].TextContent);
                }
                else if (frames[i].FrameType == RenderTreeFrameType.Markup)
                {
                    text.Append(frames[i].MarkupContent);
                }
            }
            return new Element(frames[index].ElementName, attributes, handlers, text.ToString());
        }
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new NotSupportedException($"A component under test called JS '{identifier}'.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
