using DevKit.Web.Components;
using DevKit.Web.Models;
using Xunit;

namespace DevKit.Tests;

/// <summary>Six pages build their area and sprint pickers from this, so its value format is a contract.</summary>
public class AreaSprintSelectTests
{
    private static readonly TfsArea Ara = new() { Name = "FX to CPFOIA Migration", Path = @"\CasepointARA\Area\FX to CPFOIA Migration", Project = "CasepointARA" };

    private static readonly TfsIteration[] Sprints =
    {
        new() { Name = "PI-2026-5_1", Path = @"\CasepointARA\Iteration\PI-2026-5_1", Project = "CasepointARA" },
        new() { Name = "PI-2026-5_1", Path = @"\Core\Iteration\PI-2026-5_1", Project = "Core" }
    };

    [Fact]
    public void An_area_survives_a_round_trip_through_its_dropdown_value()
    {
        var parsed = AreaSprintSelect.Parse(AreaSprintSelect.Serialize(Ara));

        Assert.NotNull(parsed);
        Assert.Equal((Ara.Name, Ara.Path, Ara.Project), (parsed.Name, parsed.Path, parsed.Project));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("only|||two")]
    [InlineData("a|||b|||c|||d")]
    public void A_malformed_value_is_no_area(string? value)
    {
        Assert.Null(AreaSprintSelect.Parse(value));
    }

    [Fact]
    public void A_saved_area_is_restored_only_while_it_still_exists()
    {
        var saved = AreaSprintSelect.Serialize(Ara);

        Assert.NotNull(AreaSprintSelect.Restore(saved, new[] { Ara }));
        Assert.Null(AreaSprintSelect.Restore(saved, Array.Empty<TfsArea>()));
    }

    [Fact]
    public void Sprint_options_are_scoped_to_the_areas_project()
    {
        var scoped = AreaSprintSelect.SprintOptions(Sprints, Ara);
        var all = AreaSprintSelect.SprintOptions(Sprints, null);

        Assert.Equal(@"\CasepointARA\Iteration\PI-2026-5_1", Assert.Single(scoped).Value);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Area_and_sprint_groups_are_one_per_project()
    {
        Assert.Single(AreaSprintSelect.AreaGroups(new[] { Ara }));
        Assert.Equal(2, AreaSprintSelect.SprintGroups(Sprints).Count);
    }

    [Fact]
    public void A_load_says_what_it_still_needs_before_it_can_start()
    {
        Assert.Equal("Select an area first.", AreaSprintSelect.MissingSelection(null, Sprints[0].Path));
        Assert.Equal("Select an area first.", AreaSprintSelect.MissingSelection(null, null));
        Assert.Equal("Select a sprint first.", AreaSprintSelect.MissingSelection(Ara, ""));
        Assert.Null(AreaSprintSelect.MissingSelection(Ara, Sprints[0].Path));
    }

    [Fact]
    public void A_loading_message_names_the_area_and_the_sprint_by_their_short_names()
    {
        Assert.Equal("FX to CPFOIA Migration · PI-2026-5_1", AreaSprintSelect.Describe(Ara, Sprints[0].Path));
        Assert.Equal("PI-2026-5_1", AreaSprintSelect.Describe(null, "PI-2026-5_1"));
    }
}
