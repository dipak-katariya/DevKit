using DevKit.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The dialog every Sprint Insights pop-up uses, read from its real markup: it is a named modal dialog, Escape and a
/// click outside ask its owner to close it, and it hands focus to the browser when it opens and back when it closes.
/// </summary>
public sealed class ModalDialogTests
{
    private const string Open = "devKit.openDialog";
    private const string Close = "devKit.closeDialog";

    private readonly RecordingJsRuntime _js = new();

    [Fact]
    public async Task It_is_a_modal_dialog_named_by_its_heading()
    {
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => { }));

        var elements = await harness.ElementsAsync(dialog);
        var panel = elements.Single(e => e.Attribute("role") == "dialog");
        Assert.Equal("true", panel.Attribute("aria-modal"));
        Assert.Equal("-1", panel.Attribute("tabindex"));
        var heading = elements.Single(e => e.Name == "h3");
        Assert.Equal(heading.Attribute("id"), panel.Attribute("aria-labelledby"));
    }

    [Fact]
    public async Task Escape_asks_the_owner_to_close_it_and_other_keys_do_not()
    {
        var closes = 0;
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => closes++));

        await harness.RaiseAsync(dialog, e => e.Attribute("role") == "dialog", "onkeydown", new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(0, closes);

        await harness.RaiseAsync(dialog, e => e.Attribute("role") == "dialog", "onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task A_click_outside_the_dialog_asks_the_owner_to_close_it()
    {
        var closes = 0;
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => closes++));

        await harness.RaiseAsync(dialog, e => e.Attribute("class") == "modal-overlay", "onclick", new MouseEventArgs());

        Assert.Equal(1, closes);
    }

    // Selecting text in a field and letting go over the page around it clicks the overlay; that used to close the
    // dialog and throw the edits away.
    [Fact]
    public async Task A_press_that_starts_inside_and_ends_outside_keeps_it_open()
    {
        var closes = 0;
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => closes++));

        await harness.RaiseAsync(dialog, e => e.Attribute("role") == "dialog", "onmousedown", new MouseEventArgs());
        await harness.RaiseAsync(dialog, e => e.Attribute("class") == "modal-overlay", "onclick", new MouseEventArgs());
        Assert.Equal(0, closes);

        await harness.RaiseAsync(dialog, e => e.Attribute("class") == "modal-overlay", "onclick", new MouseEventArgs());
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task A_click_inside_does_not_stop_the_next_click_outside_from_closing_it()
    {
        var closes = 0;
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => closes++));

        await harness.RaiseAsync(dialog, e => e.Attribute("role") == "dialog", "onmousedown", new MouseEventArgs());
        await harness.RaiseAsync(dialog, e => e.Attribute("role") == "dialog", "onclick", new MouseEventArgs());
        await harness.RaiseAsync(dialog, e => e.Attribute("class") == "modal-overlay", "onclick", new MouseEventArgs());

        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task It_takes_focus_when_it_opens_and_hands_it_back_when_it_closes()
    {
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => { }));

        var opened = Assert.Single(_js.Calls, c => c.Identifier == Open);
        var key = Assert.IsType<string>(opened.Args[1]);
        Assert.Equal(key, (await harness.ElementsAsync(dialog)).Single(e => e.Name == "h3").Attribute("id"));

        await harness.CloseAsync(dialog);

        var closed = Assert.Single(_js.Calls, c => c.Identifier == Close);
        Assert.Equal(key, closed.Args[0]);
    }

    [Fact]
    public async Task A_dialog_that_never_took_focus_does_not_try_to_hand_it_back()
    {
        _js.FailOn = Open;
        using var harness = CreateHarness();
        var dialog = await harness.RenderAsync<ModalDialog>(Parameters(() => { }));

        await harness.CloseAsync(dialog);

        Assert.DoesNotContain(_js.Calls, c => c.Identifier == Close);
    }

    private Dictionary<string, object?> Parameters(Action onClose) => new()
    {
        [nameof(ModalDialog.Title)] = (RenderFragment)(b => b.AddContent(0, "Capacity breakdown")),
        [nameof(ModalDialog.ChildContent)] = (RenderFragment)(b => b.AddContent(0, "Body")),
        [nameof(ModalDialog.OnClose)] = EventCallback.Factory.Create(this, onClose)
    };

    private ComponentHarness CreateHarness() => ComponentHarness.Create(services => services.AddSingleton<IJSRuntime>(_js));

    /// <summary>Keeps every JS call with its arguments; the one named in <see cref="FailOn"/> fails as a broken call does.</summary>
}
