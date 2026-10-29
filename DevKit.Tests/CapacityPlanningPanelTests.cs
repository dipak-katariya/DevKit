using System.Net;
using DevKit.Web.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What the Capacity Planning tab shows after another area or sprint is picked, driven through its real markup
/// against a recorded TFS: what the last load found belongs to the pair it read, not to the one picked now.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class CapacityPlanningPanelTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string SprintOne = @"\Contoso\Iteration\Sprint 1";
    private const string SprintTwo = @"\Contoso\Iteration\Sprint 2";
    private const string Broken = """{"message":"Internal error."}""";

    private static readonly TfsArea Area = new() { Name = "Web", Path = @"\Contoso\Area\Web", Project = Project };

    private readonly RecordingTfsHandler _tfs = new() { Respond = _ => (HttpStatusCode.InternalServerError, Broken) };
    private readonly HttpClient _http;
    private readonly ComponentHarness _harness;

    public CapacityPlanningPanelTests()
    {
        _http = new HttpClient(_tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = CollectionUrl;
        var api = new TfsApiService(_http, settings, NullLogger<TfsApiService>.Instance);
        _harness = ComponentHarness.Create(services => services.AddSingleton(api).AddSingleton(settings));
    }

    public void Dispose()
    {
        _harness.Dispose();
        _http.Dispose();
        _tfs.Dispose();
    }

    // The last sprint's failure used to stay under a newly picked sprint, reading as that sprint's.
    [Fact]
    public async Task What_a_load_found_shows_only_while_its_area_and_sprint_are_picked()
    {
        var panel = await _harness.RenderAsync<CapacityPlanningPanel>(Selection(SprintOne));
        Assert.True(await _harness.RaiseAsync(
            panel, e => e.Name == "button" && e.Attribute("class") == "cfg-load-btn", "onclick", new MouseEventArgs()));
        Assert.True(await ShowsLoadFailureAsync(panel));

        await _harness.SetParametersAsync(panel, Selection(SprintTwo));
        Assert.False(await ShowsLoadFailureAsync(panel));

        await _harness.SetParametersAsync(panel, Selection(SprintOne));
        Assert.True(await ShowsLoadFailureAsync(panel));
    }

    private static Dictionary<string, object?> Selection(string sprint) => new()
    {
        [nameof(CapacityPlanningPanel.Area)] = Area,
        [nameof(CapacityPlanningPanel.Sprint)] = sprint
    };

    private async Task<bool> ShowsLoadFailureAsync(int panel) =>
        (await _harness.ElementsAsync(panel)).Any(e => e.Attribute("role") == "alert" && e.Text.Contains("Load failed:", StringComparison.Ordinal));
}
