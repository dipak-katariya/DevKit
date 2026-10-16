using System.Net;
using System.Text.Json;
using DevKit.Web.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The Size Check grid's inline size editor, driven through its real markup against a recorded TFS. A browser
/// raises input on every keystroke, and keydown before change, so each test raises events in that order.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed partial class SizeCheckPanelTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string Sprint = @"\Contoso\Iteration\Sprint 1";
    private const string WorkItemPath = "/tfs/DefaultCollection/Contoso/_apis/wit/workitems/1001";
    private const string SizeFieldPath = "/fields/Microsoft.VSTS.Scheduling.Size";
    private const int RequirementId = 1001;
    private const double TypedSize = 8;

    private const string RequirementJson = """
        {"value":[{"id":1001,"fields":{"System.Title":"Login page","System.State":"Active",
        "System.AreaPath":"Contoso\\Web","System.IterationPath":"Contoso\\Sprint 1",
        "System.WorkItemType":"Requirement","Microsoft.VSTS.Scheduling.Size":5}}]}
        """;

    private static readonly TfsArea Area = new() { Name = "Web", Path = @"\Contoso\Area\Web", Project = Project };

    private readonly RecordingTfsHandler _tfs = new() { Respond = AnswerSprint };
    private readonly RecordingLogger<SizeCheckPanel> _log = new();
    private readonly HttpClient _http;
    private readonly ComponentHarness _harness;

    public SizeCheckPanelTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = CollectionUrl;
        var api = new TfsApiService(_http, settings, NullLogger<TfsApiService>.Instance);
        _harness = ComponentHarness.Create(services =>
        {
            services.AddSingleton(api);
            services.AddSingleton<ILogger<SizeCheckPanel>>(_log);
        });
    }

    public void Dispose()
    {
        _harness.Dispose();
        _http.Dispose();
        _tfs.Dispose();
    }

    [Fact]
    public async Task Enter_saves_the_size_just_typed()
    {
        var panel = await OpenSizeEditorAsync();

        await TypeAsync(panel, "8");
        await PressAsync(panel, "Enter");

        Assert.Equal(TypedSize, SavedSize());
        Assert.Contains(await _harness.ElementsAsync(panel), e => e.Text.Contains("Updated #1001 size to 8", StringComparison.Ordinal));
        Assert.DoesNotContain(await _harness.ElementsAsync(panel), IsSizeInput);
    }

    [Fact]
    public async Task The_check_button_saves_the_size_just_typed()
    {
        var panel = await OpenSizeEditorAsync();

        await TypeAsync(panel, "8");
        // Pressing the button takes focus from the input, so the browser raises change before click.
        await _harness.RaiseAsync(panel, IsSizeInput, "onchange", new ChangeEventArgs { Value = "8" });
        Assert.True(await _harness.RaiseAsync(panel, IsSaveButton, "onclick", new MouseEventArgs()));

        Assert.Equal(TypedSize, SavedSize());
    }

    [Fact]
    public async Task Escape_closes_the_editor_without_writing()
    {
        var panel = await OpenSizeEditorAsync();

        await TypeAsync(panel, "8");
        await PressAsync(panel, "Escape");

        Assert.DoesNotContain(_tfs.Sent, s => s.Method == "PATCH");
        Assert.DoesNotContain(await _harness.ElementsAsync(panel), IsSizeInput);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    public async Task A_size_below_zero_or_one_the_input_cannot_read_is_never_written(string typed)
    {
        // A number input reports text it cannot read as "".
        var panel = await OpenSizeEditorAsync();

        await TypeAsync(panel, typed);
        await PressAsync(panel, "Enter");

        Assert.DoesNotContain(_tfs.Sent, s => s.Method == "PATCH");
        Assert.Contains(await _harness.ElementsAsync(panel), e => e.Text.Contains("Invalid size value", StringComparison.Ordinal));
        Assert.Contains(await _harness.ElementsAsync(panel), IsSizeInput);
    }

    private static bool IsSizeInput(ComponentHarness.Element e) => e.Name == "input" && e.Attribute("type") == "number";

    private static bool IsSaveButton(ComponentHarness.Element e) => e.Name == "button" && e.Text.Contains('✓');

    /// <summary>Loads the sprint and opens the editor on its one requirement, whose size in TFS is 5.</summary>
    private async Task<int> OpenSizeEditorAsync()
    {
        var panel = await LoadSprintAsync();

        Assert.True(await _harness.RaiseAsync(panel, e => e.Attribute("title") == "Click to edit", "onclick", new MouseEventArgs()));
        Assert.Contains(await _harness.ElementsAsync(panel), IsSizeInput);
        return panel;
    }

    /// <summary>Renders the panel on the sprint and presses Load Sprint.</summary>
    private async Task<int> LoadSprintAsync()
    {
        var panel = await _harness.RenderAsync<SizeCheckPanel>(new Dictionary<string, object?>
        {
            [nameof(SizeCheckPanel.Area)] = Area,
            [nameof(SizeCheckPanel.Sprint)] = Sprint
        });

        Assert.True(await _harness.RaiseAsync(
            panel, e => e.Name == "button" && e.Text.Contains("Load Sprint", StringComparison.Ordinal), "onclick", new MouseEventArgs()));
        return panel;
    }

    /// <summary>Clears the field, then types one character at a time: the browser raises input for each, and change only on blur.</summary>
    private async Task TypeAsync(int panel, string typed)
    {
        await _harness.RaiseAsync(panel, IsSizeInput, "oninput", new ChangeEventArgs { Value = "" });
        for (var length = 1; length <= typed.Length; length++)
        {
            await _harness.RaiseAsync(panel, IsSizeInput, "oninput", new ChangeEventArgs { Value = typed[..length] });
        }
    }

    private async Task PressAsync(int panel, string key) =>
        Assert.True(await _harness.RaiseAsync(panel, IsSizeInput, "onkeydown", new KeyboardEventArgs { Key = key }));

    private double SavedSize()
    {
        var patch = Assert.Single(_tfs.Sent, s => s.Method == "PATCH");
        Assert.Equal(WorkItemPath, patch.Path);
        using var body = JsonDocument.Parse(patch.Body ?? "[]");
        var operation = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(SizeFieldPath, operation.GetProperty("path").GetString());
        return operation.GetProperty("value").GetDouble();
    }

    private static (HttpStatusCode Status, string Json) AnswerSprint(RecordingTfsHandler.Request sent) => sent.Method switch
    {
        "POST" when sent.Body?.Contains("WorkItemLinks", StringComparison.Ordinal) == true => (HttpStatusCode.OK, """{"workItemRelations":[]}"""),
        "POST" => (HttpStatusCode.OK, $$"""{"workItems":[{"id":{{RequirementId}}}]}"""),
        "GET" => (HttpStatusCode.OK, RequirementJson),
        _ => (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(RequirementId, 2))
    };
}
