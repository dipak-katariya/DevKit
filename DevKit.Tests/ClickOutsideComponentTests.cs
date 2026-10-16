using DevKit.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The dropdowns, the popover and the command search driven through their real markup, with the JS calls they
/// make recorded: each open panel asks app.js for one outside-click listener and each closed one for none.
/// </summary>
public sealed class ClickOutsideComponentTests
{
    private const string Register = "devKit.registerClickOutside";
    private const string Unregister = "devKit.unregisterClickOutside";
    private const string Focus = "devKit.focusEl";
    private const string CommandKey = "devKit.registerCommandKey";

    private static readonly Dictionary<string, object?> DropdownParameters = new()
    {
        [nameof(GlassDropdown.Options)] = new List<GlassDropdown.DropdownOption>
        {
            new() { Value = "a", Label = "Alpha" },
            new() { Value = "b", Label = "Beta" }
        }
    };

    private readonly RecordingJsRuntime _js = new();
    private readonly RecordingLoggerFactory _logs = new();

    [Fact]
    public async Task A_dropdown_listens_while_open_and_stops_when_closed()
    {
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(DropdownParameters);
        Assert.Equal(0, _js.Count(Register));

        await ClickTriggerAsync(harness, dropdown);
        Assert.Equal(1, _js.Count(Register));
        Assert.Equal(1, _js.Count(Focus));

        await ClickTriggerAsync(harness, dropdown);
        Assert.Equal(1, _js.Count(Register));
        Assert.Equal(1, _js.Count(Unregister));
    }

    [Fact]
    public async Task Choosing_an_option_closes_the_dropdown_and_removes_its_listener()
    {
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(DropdownParameters);

        await ClickTriggerAsync(harness, dropdown);
        Assert.True(await harness.RaiseAsync(dropdown, e => e.Attribute("title") == "Beta", "onclick", new MouseEventArgs()));

        Assert.DoesNotContain(await harness.ElementsAsync(dropdown), IsPanel);
        Assert.Equal(1, _js.Count(Unregister));
    }

    [Fact]
    public async Task An_outside_click_closes_the_dropdown_and_the_next_opening_listens_again()
    {
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(DropdownParameters);
        await ClickTriggerAsync(harness, dropdown);

        await harness.InvokeFromJsAsync(() => ListenerOwner().CloseDropdown());

        Assert.DoesNotContain(await harness.ElementsAsync(dropdown), IsPanel);
        // app.js's listener removed itself before calling CloseDropdown, so nothing is left to remove.
        Assert.Equal(0, _js.Count(Unregister));

        await ClickTriggerAsync(harness, dropdown);
        Assert.Equal(2, _js.Count(Register));
    }

    [Fact]
    public async Task Disposing_an_open_dropdown_removes_its_listener_and_releases_its_reference()
    {
        var harness = CreateHarness();
        DotNetObjectReference<ClickOutsideComponent> reference;
        try
        {
            var dropdown = await harness.RenderAsync<GlassDropdown>(DropdownParameters);
            await ClickTriggerAsync(harness, dropdown);
            reference = RegisteredReference();
        }
        finally
        {
            harness.Dispose();
        }

        Assert.Equal(1, _js.Count(Unregister));
        Assert.Throws<ObjectDisposedException>(() => reference.Value);
    }

    [Fact]
    public async Task A_failed_listener_call_is_logged_at_Error_and_the_dropdown_still_opens()
    {
        _js.FailOn = Register;
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(DropdownParameters);

        await ClickTriggerAsync(harness, dropdown);

        Assert.Contains(await harness.ElementsAsync(dropdown), IsPanel);
        var entry = Assert.Single(_logs.Logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<JSException>(entry.Exception);
    }

    [Fact]
    public async Task A_listener_call_after_the_page_closed_is_logged_at_Debug_not_as_a_failure()
    {
        _js.FailOn = Register;
        _js.Failure = _ => new JSDisconnectedException("The circuit has disconnected.");
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(DropdownParameters);

        await ClickTriggerAsync(harness, dropdown);

        var entry = Assert.Single(_logs.Logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.IsType<JSDisconnectedException>(entry.Exception);
    }

    [Fact]
    public async Task An_outside_click_asks_the_popover_owner_to_close_it()
    {
        bool? requested = null;
        using var harness = CreateHarness();
        await harness.RenderAsync<Popover>(new Dictionary<string, object?>
        {
            [nameof(Popover.Open)] = true,
            [nameof(Popover.OpenChanged)] = EventCallback.Factory.Create<bool>(this, open => requested = open),
            [nameof(Popover.Trigger)] = (RenderFragment)(builder => builder.AddContent(0, "Branch setup"))
        });
        Assert.Equal(1, _js.Count(Register));

        await harness.InvokeFromJsAsync(() => ListenerOwner().CloseDropdown());

        Assert.False(requested);
    }

    [Fact]
    public async Task The_command_search_listens_only_while_its_results_are_open()
    {
        using var harness = CreateHarness(services => services.AddSingleton<NavigationManager>(new StubNavigation()));
        var search = await harness.RenderAsync<CommandSearch>(new Dictionary<string, object?>());
        Assert.Equal(1, _js.Count(CommandKey));
        Assert.Equal(0, _js.Count(Register));

        await harness.RaiseAsync(search, IsSearchBox, "onfocus", new FocusEventArgs());
        Assert.Equal(1, _js.Count(Register));

        await harness.RaiseAsync(search, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, _js.Count(Unregister));
        Assert.Equal(1, _js.Count(CommandKey));
    }

    private static bool IsPanel(ComponentHarness.Element e) => e.Attribute("class") == "gdd-panel";

    private static bool IsSearchBox(ComponentHarness.Element e) => e.Attribute("id") == "cmdSearch";

    private static Task ClickTriggerAsync(ComponentHarness harness, int dropdown) =>
        harness.RaiseAsync(dropdown, e => e.Attribute("class") == "gdd-trigger", "onclick", new MouseEventArgs());

    private ComponentHarness CreateHarness(Action<IServiceCollection>? register = null) => ComponentHarness.Create(services =>
    {
        services.AddSingleton<IJSRuntime>(_js);
        services.AddSingleton<ILoggerFactory>(_logs);
        register?.Invoke(services);
    });

    private DotNetObjectReference<ClickOutsideComponent> RegisteredReference() =>
        (DotNetObjectReference<ClickOutsideComponent>)_js.Calls.Last(c => c.Identifier == Register).Args[1]!;

    private ClickOutsideComponent ListenerOwner() => RegisteredReference().Value;

    private sealed class RecordingJsRuntime : IJSRuntime
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

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public RecordingLogger<ClickOutsideComponent> Logger { get; } = new();

        public ILogger CreateLogger(string categoryName) => Logger;

        public void AddProvider(ILoggerProvider provider) =>
            throw new NotSupportedException("The recording factory takes no providers.");

        public void Dispose()
        {
            // Holds nothing that needs releasing; the recorded entries stay readable after the harness is gone.
        }
    }

    private sealed class StubNavigation : NavigationManager
    {
        public StubNavigation() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }
}
