using DevKit.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The multi-select's Add row, where a page takes values its list does not have (Update status's tags): the arrow keys
/// reach it as the last row, Enter adds the typed text only when it is highlighted or nothing else matches, and an Enter
/// meant to pick a match never adds the partial text as a value of its own.
/// </summary>
public sealed class MultiSelectCreateRowTests
{
    private readonly RecordingJsRuntime _js = new();
    private readonly List<string> _created = new();

    [Fact]
    public async Task Enter_on_a_partial_match_adds_nothing()
    {
        using var harness = CreateHarness();
        var dropdown = await OpenAsync(harness, "merged");

        await PressAsync(harness, dropdown, "Enter");

        Assert.Empty(_created);
    }

    [Fact]
    public async Task The_arrow_keys_reach_the_Add_row_last_and_Enter_adds_the_typed_text()
    {
        using var harness = CreateHarness();
        var dropdown = await OpenAsync(harness, "merged");

        await PressAsync(harness, dropdown, "ArrowDown");
        await PressAsync(harness, dropdown, "ArrowDown");
        var elements = await harness.ElementsAsync(dropdown);
        var addRow = elements.Single(e => e.Attribute("id")?.EndsWith("-new", StringComparison.Ordinal) == true);
        Assert.Equal(addRow.Attribute("id"), elements.Single(IsSearchBox).Attribute("aria-activedescendant"));
        Assert.Contains("gdd-item-hl", addRow.Attribute("class"), StringComparison.Ordinal);

        await PressAsync(harness, dropdown, "Enter");

        Assert.Equal(new[] { "merged" }, _created);
    }

    [Fact]
    public async Task With_nothing_else_matching_Enter_adds_at_once_and_the_row_sits_in_the_list()
    {
        using var harness = CreateHarness();
        var dropdown = await OpenAsync(harness, "QA Done");

        Assert.Contains(await harness.ElementsAsync(dropdown), e => e.Attribute("role") == "listbox");
        await PressAsync(harness, dropdown, "Enter");

        Assert.Equal(new[] { "QA Done" }, _created);
    }

    [Fact]
    public async Task Enter_on_an_existing_tag_typed_in_another_case_ticks_it_and_adds_nothing()
    {
        using var harness = CreateHarness();
        var dropdown = await OpenAsync(harness, "pt merged");

        await PressAsync(harness, dropdown, "Enter");

        Assert.Empty(_created);
        Assert.Contains("PT Merged", _selected);
    }

    private readonly HashSet<string> _selected = new();

    private async Task<int> OpenAsync(ComponentHarness harness, string typed)
    {
        var dropdown = await harness.RenderAsync<MultiSelectDropdown>(new Dictionary<string, object?>
        {
            [nameof(MultiSelectDropdown.Options)] = new List<MultiSelectDropdown.Option> { new() { Value = "PT Merged", Label = "PT Merged" } },
            [nameof(MultiSelectDropdown.Selected)] = _selected,
            [nameof(MultiSelectDropdown.Label)] = "Tags to add",
            [nameof(MultiSelectDropdown.OnCreate)] = EventCallback.Factory.Create<string>(this, _created.Add)
        });
        await harness.RaiseAsync(dropdown, e => e.Attribute("class") == "gdd-trigger", "onclick", new MouseEventArgs());
        await harness.RaiseAsync(dropdown, IsSearchBox, "oninput", new ChangeEventArgs { Value = typed });
        return dropdown;
    }

    private static bool IsSearchBox(ComponentHarness.Element e) => e.Attribute("class") == "gdd-search";

    private static Task PressAsync(ComponentHarness harness, int component, string key) =>
        harness.RaiseAsync(component, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = key });

    private ComponentHarness CreateHarness() => ComponentHarness.Create(services => services.AddSingleton<IJSRuntime>(_js));
}
