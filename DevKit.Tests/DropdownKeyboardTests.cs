using DevKit.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The dropdowns and the popover driven by the keyboard alone, read from their real markup: each trigger is a named
/// button that says whether its list is open, the arrow keys move through the options, Enter chooses, and Escape
/// closes and hands focus back to the trigger.
/// </summary>
public sealed class DropdownKeyboardTests
{
    private const string Focus = "devKit.focusEl";
    private const string FocusFirst = "devKit.focusFirst";
    private const string Reveal = "devKit.revealEl";

    private readonly RecordingJsRuntime _js = new();

    [Fact]
    public async Task A_dropdown_trigger_is_a_named_button_that_says_whether_its_list_is_open()
    {
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(Single(value: "b"));

        var trigger = await TriggerAsync(harness, dropdown);
        Assert.Equal("button", trigger.Name);
        Assert.Equal("button", trigger.Attribute("type"));
        Assert.Equal("Area path: Beta", trigger.Attribute("aria-label"));
        Assert.Equal("listbox", trigger.Attribute("aria-haspopup"));
        Assert.Equal("false", trigger.Attribute("aria-expanded"));

        await ClickTriggerAsync(harness, dropdown);

        trigger = await TriggerAsync(harness, dropdown);
        Assert.Equal("true", trigger.Attribute("aria-expanded"));
        var box = await SearchBoxAsync(harness, dropdown);
        Assert.Equal("combobox", box.Attribute("role"));
        Assert.Equal("Area path", box.Attribute("aria-label"));
        var list = (await harness.ElementsAsync(dropdown)).Single(e => e.Attribute("role") == "listbox");
        Assert.Equal(list.Attribute("id"), box.Attribute("aria-controls"));
    }

    [Fact]
    public async Task A_disabled_dropdown_has_a_disabled_trigger()
    {
        using var harness = CreateHarness();
        var parameters = Single(value: "");
        parameters[nameof(GlassDropdown.Disabled)] = true;
        var dropdown = await harness.RenderAsync<GlassDropdown>(parameters);

        Assert.NotNull((await TriggerAsync(harness, dropdown)).Attribute("disabled"));
    }

    [Fact]
    public async Task The_arrows_walk_the_All_row_then_the_options_and_Enter_chooses_and_hands_focus_back()
    {
        string? chosen = null;
        using var harness = CreateHarness();
        var parameters = Single(value: "");
        parameters[nameof(GlassDropdown.OnChange)] =
            EventCallback.Factory.Create<ChangeEventArgs>(this, e => chosen = e.Value?.ToString());
        var dropdown = await harness.RenderAsync<GlassDropdown>(parameters);
        await ClickTriggerAsync(harness, dropdown);

        await PressAsync(harness, dropdown, "ArrowDown");
        var allRow = await HighlightedAsync(harness, dropdown);
        Assert.Equal("All areas", allRow.Text.Trim());
        Assert.Equal("true", allRow.Attribute("aria-selected"));

        await PressAsync(harness, dropdown, "ArrowDown");
        await PressAsync(harness, dropdown, "ArrowDown");
        await PressAsync(harness, dropdown, "ArrowDown");
        Assert.Equal("Beta", (await HighlightedAsync(harness, dropdown)).Attribute("title"));

        await PressAsync(harness, dropdown, "ArrowUp");
        Assert.Equal("Alpha", (await HighlightedAsync(harness, dropdown)).Attribute("title"));
        Assert.True(_js.Count(Reveal) >= 4);

        await PressAsync(harness, dropdown, "Enter");

        Assert.Equal("a", chosen);
        Assert.DoesNotContain(await harness.ElementsAsync(dropdown), IsSearchBox);
        Assert.Equal(2, _js.Count(Focus));
    }

    [Fact]
    public async Task Escape_closes_the_dropdown_and_hands_focus_back_to_its_trigger()
    {
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<GlassDropdown>(Single(value: "a"));
        await ClickTriggerAsync(harness, dropdown);
        Assert.Equal(1, _js.Count(Focus));

        await PressAsync(harness, dropdown, "Escape");

        Assert.Equal("false", (await TriggerAsync(harness, dropdown)).Attribute("aria-expanded"));
        Assert.DoesNotContain(await harness.ElementsAsync(dropdown), IsSearchBox);
        Assert.Equal(2, _js.Count(Focus));
    }

    [Fact]
    public async Task The_multi_select_ticks_the_highlighted_option_with_Enter_and_stays_open()
    {
        var selected = new HashSet<string> { "s1" };
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<MultiSelectDropdown>(new Dictionary<string, object?>
        {
            [nameof(MultiSelectDropdown.Options)] = new List<MultiSelectDropdown.Option>
            {
                new() { Value = "s1", Label = "Sprint 1" },
                new() { Value = "s2", Label = "Sprint 2" }
            },
            [nameof(MultiSelectDropdown.Selected)] = selected,
            [nameof(MultiSelectDropdown.Label)] = "Sprint"
        });
        Assert.Equal("Sprint: Sprint 1", (await TriggerAsync(harness, dropdown)).Attribute("aria-label"));
        await ClickTriggerAsync(harness, dropdown);

        var list = (await harness.ElementsAsync(dropdown)).Single(e => e.Attribute("role") == "listbox");
        Assert.Equal("true", list.Attribute("aria-multiselectable"));
        Assert.Equal("true", (await OptionAsync(harness, dropdown, "Sprint 1")).Attribute("aria-selected"));
        Assert.Equal("false", (await OptionAsync(harness, dropdown, "Sprint 2")).Attribute("aria-selected"));

        await PressAsync(harness, dropdown, "ArrowDown");
        await PressAsync(harness, dropdown, "ArrowDown");
        await PressAsync(harness, dropdown, "Enter");

        Assert.Equal(new[] { "s1", "s2" }, selected.Order());
        Assert.Contains(await harness.ElementsAsync(dropdown), IsSearchBox);
        Assert.Equal("true", (await OptionAsync(harness, dropdown, "Sprint 2")).Attribute("aria-selected"));

        await PressAsync(harness, dropdown, "Escape");
        Assert.DoesNotContain(await harness.ElementsAsync(dropdown), IsSearchBox);
        Assert.Equal(2, _js.Count(Focus));
    }

    [Fact]
    public async Task A_multi_select_search_with_no_match_is_a_status_line_not_an_empty_list()
    {
        using var harness = CreateHarness();
        var dropdown = await harness.RenderAsync<MultiSelectDropdown>(new Dictionary<string, object?>
        {
            [nameof(MultiSelectDropdown.Options)] = new List<MultiSelectDropdown.Option> { new() { Value = "s1", Label = "Sprint 1" } }
        });
        await ClickTriggerAsync(harness, dropdown);

        await harness.RaiseAsync(dropdown, IsSearchBox, "oninput", new ChangeEventArgs { Value = "zzz" });

        // The line has nothing dynamic in it, so Blazor sends it as markup rather than as an element of its own.
        var elements = await harness.ElementsAsync(dropdown);
        Assert.Contains(elements, e => e.Text.Contains("role=\"status\">No matches", StringComparison.Ordinal));
        Assert.DoesNotContain(elements, e => e.Attribute("role") == "listbox");
    }

    [Fact]
    public async Task A_branch_picker_has_its_clear_button_beside_the_trigger_and_Enter_picks_the_first_match()
    {
        var values = new List<string>();
        using var harness = CreateHarness();
        var picker = await harness.RenderAsync<BranchPicker>(new Dictionary<string, object?>
        {
            [nameof(BranchPicker.Branches)] = new List<string> { "main", "release/1", "release/2" },
            [nameof(BranchPicker.Value)] = "main",
            [nameof(BranchPicker.Label)] = "QA branch",
            [nameof(BranchPicker.ValueChanged)] = EventCallback.Factory.Create<string>(this, values.Add)
        });

        Assert.Equal("QA branch: main", (await TriggerAsync(harness, picker)).Attribute("aria-label"));
        var clear = (await harness.ElementsAsync(picker)).Single(e => e.Attribute("class") == "bpk-clear");
        Assert.Equal("Clear QA branch", clear.Attribute("aria-label"));

        await ClickTriggerAsync(harness, picker);
        await harness.RaiseAsync(picker, IsSearchBox, "oninput", new ChangeEventArgs { Value = "release" });
        await PressAsync(harness, picker, "Enter");

        Assert.Equal("release/1", Assert.Single(values));
        Assert.Equal(2, _js.Count(Focus));
    }

    [Fact]
    public async Task Escape_inside_a_popover_asks_its_owner_to_close_it_and_focuses_its_trigger()
    {
        var requests = new List<bool>();
        using var harness = CreateHarness();
        var popover = await harness.RenderAsync<Popover>(new Dictionary<string, object?>
        {
            [nameof(Popover.Open)] = true,
            [nameof(Popover.OpenChanged)] = EventCallback.Factory.Create<bool>(this, requests.Add),
            [nameof(Popover.Trigger)] = (RenderFragment)(builder => builder.AddContent(0, "Branch setup"))
        });

        await harness.RaiseAsync(popover, IsKeyListener, "onkeydown", new KeyboardEventArgs { Key = "Tab" });
        Assert.Empty(requests);

        await harness.RaiseAsync(popover, IsKeyListener, "onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.False(Assert.Single(requests));
        Assert.Equal(1, _js.Count(FocusFirst));
    }

    private static Dictionary<string, object?> Single(string value) => new()
    {
        [nameof(GlassDropdown.Options)] = new List<GlassDropdown.DropdownOption>
        {
            new() { Value = "a", Label = "Alpha" },
            new() { Value = "b", Label = "Beta" }
        },
        [nameof(GlassDropdown.Value)] = value,
        [nameof(GlassDropdown.EmptyLabel)] = "All areas",
        [nameof(GlassDropdown.Label)] = "Area path"
    };

    private static bool IsTrigger(ComponentHarness.Element e) => e.Attribute("class") == "gdd-trigger";

    private static bool IsSearchBox(ComponentHarness.Element e) => e.Attribute("class") == "gdd-search";

    private static bool IsKeyListener(ComponentHarness.Element e) => e.Handlers.ContainsKey("onkeydown");

    private static async Task<ComponentHarness.Element> TriggerAsync(ComponentHarness harness, int component) =>
        (await harness.ElementsAsync(component)).Single(IsTrigger);

    private static async Task<ComponentHarness.Element> SearchBoxAsync(ComponentHarness harness, int component) =>
        (await harness.ElementsAsync(component)).Single(IsSearchBox);

    private static async Task<ComponentHarness.Element> OptionAsync(ComponentHarness harness, int component, string title) =>
        (await harness.ElementsAsync(component)).Single(e => e.Attribute("role") == "option" && e.Attribute("title") == title);

    /// <summary>The option the search box points screen readers at, which is also the one drawn highlighted.</summary>
    private static async Task<ComponentHarness.Element> HighlightedAsync(ComponentHarness harness, int component)
    {
        var elements = await harness.ElementsAsync(component);
        var id = elements.Single(IsSearchBox).Attribute("aria-activedescendant");
        var option = elements.Single(e => e.Attribute("id") == id);
        Assert.Contains("gdd-item-hl", option.Attribute("class"));
        return option;
    }

    private static Task ClickTriggerAsync(ComponentHarness harness, int component) =>
        harness.RaiseAsync(component, IsTrigger, "onclick", new MouseEventArgs());

    private static Task PressAsync(ComponentHarness harness, int component, string key) =>
        harness.RaiseAsync(component, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = key });

    private ComponentHarness CreateHarness() => ComponentHarness.Create(services => services.AddSingleton<IJSRuntime>(_js));

    /// <summary>Answers every JS call with nothing and keeps a list of them, so a test can see where focus was sent.</summary>
}
