using DevKit.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What the command search tells a screen reader, read from its real markup: a labelled combobox that says whether
/// its results are open, which list they are in and which one the arrow keys have reached.
/// </summary>
public sealed class CommandSearchTests
{
    [Fact]
    public async Task The_search_box_says_whether_its_results_are_open_and_which_one_is_highlighted()
    {
        using var harness = CreateHarness();
        var search = await harness.RenderAsync<CommandSearch>(new Dictionary<string, object?>());

        var box = await SearchBoxAsync(harness, search);
        Assert.Equal("combobox", box.Attribute("role"));
        Assert.Equal("Search tools", box.Attribute("aria-label"));
        Assert.Equal("false", box.Attribute("aria-expanded"));
        Assert.Null(box.Attribute("aria-controls"));
        Assert.Null(box.Attribute("aria-activedescendant"));

        await harness.RaiseAsync(search, IsSearchBox, "onfocus", new FocusEventArgs());
        box = await SearchBoxAsync(harness, search);
        Assert.Equal("true", box.Attribute("aria-expanded"));
        Assert.Equal("cmdSearchList", box.Attribute("aria-controls"));
        Assert.Equal("cmdSearchOpt0", box.Attribute("aria-activedescendant"));
        var elements = await harness.ElementsAsync(search);
        Assert.Contains(elements, e => e.Attribute("id") == "cmdSearchList" && e.Attribute("role") == "listbox");
        Assert.Equal("true", elements.Single(e => e.Attribute("id") == "cmdSearchOpt0").Attribute("aria-selected"));

        await harness.RaiseAsync(search, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = "ArrowDown" });
        box = await SearchBoxAsync(harness, search);
        Assert.Equal("cmdSearchOpt1", box.Attribute("aria-activedescendant"));
        elements = await harness.ElementsAsync(search);
        Assert.Equal("false", elements.Single(e => e.Attribute("id") == "cmdSearchOpt0").Attribute("aria-selected"));
        Assert.Equal("true", elements.Single(e => e.Attribute("id") == "cmdSearchOpt1").Attribute("aria-selected"));
    }

    [Fact]
    public async Task A_search_with_no_match_is_announced_and_points_at_no_result()
    {
        using var harness = CreateHarness();
        var search = await harness.RenderAsync<CommandSearch>(new Dictionary<string, object?>());

        await harness.RaiseAsync(search, IsSearchBox, "oninput", new ChangeEventArgs { Value = "zzz" });

        var box = await SearchBoxAsync(harness, search);
        Assert.Equal("true", box.Attribute("aria-expanded"));
        Assert.Null(box.Attribute("aria-activedescendant"));
        var elements = await harness.ElementsAsync(search);
        Assert.Contains(elements, e => e.Attribute("role") == "status");
        Assert.DoesNotContain(elements, e => e.Attribute("role") == "listbox");
    }

    [Fact]
    public async Task Escape_closes_the_results_and_clears_the_search_so_it_never_reopens_on_old_results()
    {
        using var harness = CreateHarness();
        var search = await harness.RenderAsync<CommandSearch>(new Dictionary<string, object?>());
        await harness.RaiseAsync(search, IsSearchBox, "oninput", new ChangeEventArgs { Value = "zzz" });

        await harness.RaiseAsync(search, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = "Escape" });

        var box = await SearchBoxAsync(harness, search);
        Assert.Equal("false", box.Attribute("aria-expanded"));
        Assert.Equal("", box.Attribute("value"));

        await harness.RaiseAsync(search, IsSearchBox, "onfocus", new FocusEventArgs());
        var elements = await harness.ElementsAsync(search);
        Assert.DoesNotContain(elements, e => e.Attribute("role") == "status");
        Assert.Contains(elements, e => e.Attribute("role") == "listbox");
    }

    [Fact]
    public async Task After_Escape_the_box_keeps_focus_and_a_click_or_the_down_arrow_reopens_the_results()
    {
        using var harness = CreateHarness();
        var search = await harness.RenderAsync<CommandSearch>(new Dictionary<string, object?>());
        await harness.RaiseAsync(search, IsSearchBox, "onfocus", new FocusEventArgs());
        await harness.RaiseAsync(search, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = "Escape" });

        await harness.RaiseAsync(search, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = "ArrowDown" });
        var box = await SearchBoxAsync(harness, search);
        Assert.Equal("true", box.Attribute("aria-expanded"));
        Assert.Equal("cmdSearchOpt0", box.Attribute("aria-activedescendant"));

        await harness.RaiseAsync(search, IsSearchBox, "onkeydown", new KeyboardEventArgs { Key = "Escape" });
        Assert.True(await harness.RaiseAsync(search, IsSearchBox, "onclick", new MouseEventArgs()));
        box = await SearchBoxAsync(harness, search);
        Assert.Equal("true", box.Attribute("aria-expanded"));
    }

    private static bool IsSearchBox(ComponentHarness.Element e) => e.Attribute("id") == "cmdSearch";

    private static async Task<ComponentHarness.Element> SearchBoxAsync(ComponentHarness harness, int search) =>
        (await harness.ElementsAsync(search)).Single(IsSearchBox);

    private static ComponentHarness CreateHarness() => ComponentHarness.Create(services =>
    {
        services.AddSingleton<IJSRuntime>(new RecordingJsRuntime());
        services.AddSingleton<NavigationManager>(new StubNavigation());
    });

    /// <summary>Answers every JS call with nothing: these tests read markup, not the calls the search makes.</summary>

    private sealed class StubNavigation : NavigationManager
    {
        public StubNavigation() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }
}
