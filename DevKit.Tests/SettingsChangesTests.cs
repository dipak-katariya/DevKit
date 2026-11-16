using System.Text.Json.Nodes;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What one save changed, applied to the settings kept by then: only the fields that changed are replaced, objects field
/// by field and lists whole, a removed field is removed, and everything else stays as another page saved it.
/// </summary>
public sealed class SettingsChangesTests
{
    private const string Kept = """
        { "TeamName": "TEAM-A", "DashboardView": "Tiles", "MostUsedRepoIds": ["r1", "r2"],
          "RepoBranches": { "r1": { "QaBranch": "qa" }, "r2": { "QaBranch": "dev" } }, "FromLater": 1 }
        """;

    [Fact]
    public void Only_what_changed_is_applied_and_the_rest_stays()
    {
        var changes = SettingsChanges.Between(
            """{ "TeamName": "", "DashboardView": "Grid", "MostUsedRepoIds": ["r1"], "RepoBranches": { "r1": { "QaBranch": "qa" } } }""",
            """{ "TeamName": "", "DashboardView": "Grid", "MostUsedRepoIds": ["r3"], "RepoBranches": { "r1": { "QaBranch": "main" } } }""");

        var merged = JsonNode.Parse(SettingsChanges.Apply(Kept, changes))!;

        Assert.Equal("TEAM-A", (string?)merged["TeamName"]);
        Assert.Equal("Tiles", (string?)merged["DashboardView"]);
        Assert.Equal("r3", (string?)Assert.Single(merged["MostUsedRepoIds"]!.AsArray()));
        Assert.Equal("main", (string?)merged["RepoBranches"]!["r1"]!["QaBranch"]);
        Assert.Equal("dev", (string?)merged["RepoBranches"]!["r2"]!["QaBranch"]);
        Assert.Equal(1, (int?)merged["FromLater"]);
    }

    [Fact]
    public void A_removed_entry_is_removed_and_one_added_is_added()
    {
        var changes = SettingsChanges.Between(
            """{ "RepoBranches": { "r1": { "QaBranch": "qa" } } }""",
            """{ "RepoBranches": { "r3": { "QaBranch": "rel" } }, "TeamName": "TEAM-B" }""");

        var merged = JsonNode.Parse(SettingsChanges.Apply(Kept, changes))!;

        Assert.Null(merged["RepoBranches"]!["r1"]);
        Assert.Equal("dev", (string?)merged["RepoBranches"]!["r2"]!["QaBranch"]);
        Assert.Equal("rel", (string?)merged["RepoBranches"]!["r3"]!["QaBranch"]);
        Assert.Equal("TEAM-B", (string?)merged["TeamName"]);
    }

    [Fact]
    public void Nothing_changed_changes_nothing()
    {
        var changes = SettingsChanges.Between(Kept, Kept);

        Assert.Empty(changes);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Kept), JsonNode.Parse(SettingsChanges.Apply(Kept, changes))));
    }
}
