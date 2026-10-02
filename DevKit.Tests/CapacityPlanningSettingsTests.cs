using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The settings file is hand-editable and every value in it ends up in arithmetic or on screen,
/// so normalisation is the security and correctness boundary for the whole tab.
/// </summary>
public class CapacityPlanningSettingsTests
{
    [Fact]
    public void Defaults_reproduce_the_formula_that_used_to_be_hard_coded()
    {
        var s = new CapacityPlanningSettings();

        Assert.Equal(PlanningMode.AiOverPlan, s.Mode);
        Assert.Equal(40, s.AiOverPlanPercent);
        Assert.Equal(8.5, s.HoursPerDay);
        Assert.True(s.UseTfsMemberCapacity);
        Assert.Equal(SprintLengthSource.IterationDates, s.SprintLength);
        Assert.Equal(5, s.DaysPerWeek);
        Assert.Equal(new[] { "ALM", "Team B Support", "Engineering Operations & Support", "Regression" },
            s.TagRules.Select(r => r.Tag));
        // Regression was never carved out of the over-plan base; the other three always were.
        Assert.Equal(new[] { true, true, true, false }, s.TagRules.Select(r => r.ExcludeFromOverPlan));
    }

    [Fact]
    public void Regular_mode_applies_no_over_plan_whatever_the_ai_percentage_holds()
    {
        var s = new CapacityPlanningSettings { Mode = PlanningMode.Regular, AiOverPlanPercent = 40 };

        Assert.Equal(0, s.EffectiveOverPlanPercent);
    }

    [Fact]
    public void Ai_mode_applies_the_configured_percentage()
    {
        var s = new CapacityPlanningSettings { Mode = PlanningMode.AiOverPlan, AiOverPlanPercent = 25 };

        Assert.Equal(25, s.EffectiveOverPlanPercent);
    }

    [Fact]
    public void A_two_week_five_day_sprint_is_ten_days()
    {
        Assert.Equal(10, new CapacityPlanningSettings { SprintWeeks = 2 }.FixedSprintDays);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(500, CapacityPlanningSettings.MaxOverPlanPercent)]
    [InlineData(double.NaN, CapacityPlanningSettings.DefaultAiOverPlanPercent)]
    [InlineData(double.PositiveInfinity, CapacityPlanningSettings.DefaultAiOverPlanPercent)]
    [InlineData(35, 35)]
    public void Over_plan_percentage_is_bounded(double input, double expected)
    {
        Assert.Equal(expected, new CapacityPlanningSettings { AiOverPlanPercent = input }.Normalized().AiOverPlanPercent);
    }

    [Theory]
    [InlineData(0, CapacityPlanningSettings.DefaultHoursPerDay)]
    [InlineData(-4, CapacityPlanningSettings.DefaultHoursPerDay)]
    [InlineData(double.NaN, CapacityPlanningSettings.DefaultHoursPerDay)]
    [InlineData(99, CapacityPlanningSettings.MaxHoursPerDay)]
    [InlineData(12, 12)]
    [InlineData(8, 8)]
    public void Hours_per_day_is_bounded(double input, double expected)
    {
        Assert.Equal(expected, new CapacityPlanningSettings { HoursPerDay = input }.Normalized().HoursPerDay);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(99, CapacityPlanningSettings.MaxSprintWeeks)]
    [InlineData(3, 3)]
    public void Sprint_weeks_are_bounded(int input, int expected)
    {
        Assert.Equal(expected, new CapacityPlanningSettings { SprintWeeks = input }.Normalized().SprintWeeks);
    }

    [Fact]
    public void Working_days_are_deduplicated_and_ordered_monday_first()
    {
        var s = new CapacityPlanningSettings
        {
            WorkingDays = { DayOfWeek.Saturday, DayOfWeek.Monday, DayOfWeek.Monday }
        }.Normalized();

        Assert.Equal(new[]
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday
        }, s.WorkingDays);
    }

    [Fact]
    public void An_empty_or_invalid_week_falls_back_to_monday_to_friday()
    {
        var empty = new CapacityPlanningSettings { WorkingDays = new() }.Normalized();
        var invalid = new CapacityPlanningSettings { WorkingDays = new() { (DayOfWeek)42 } }.Normalized();

        Assert.Equal(CapacityPlanningSettings.DefaultWorkingDays(), empty.WorkingDays);
        Assert.Equal(CapacityPlanningSettings.DefaultWorkingDays(), invalid.WorkingDays);
    }

    [Fact]
    public void Undefined_enum_values_fall_back_to_defaults()
    {
        var s = new CapacityPlanningSettings { Mode = (PlanningMode)9, SprintLength = (SprintLengthSource)9 }.Normalized();

        Assert.Equal(PlanningMode.AiOverPlan, s.Mode);
        Assert.Equal(SprintLengthSource.IterationDates, s.SprintLength);
    }

    [Fact]
    public void Tag_rules_are_trimmed_deduplicated_and_blank_ones_dropped()
    {
        var s = new CapacityPlanningSettings
        {
            TagRules = new()
            {
                new() { Tag = "  ALM  ", Deliverable = true },
                new() { Tag = "alm", Deliverable = false },     // duplicate by case — first wins
                new() { Tag = "   " },
                new() { Tag = new string('x', CapacityPlanningSettings.MaxNameLength + 1) },
                null!
            }
        }.Normalized();

        var only = Assert.Single(s.TagRules);
        Assert.Equal("ALM", only.Tag);
        Assert.True(only.Deliverable);
    }

    [Fact]
    public void Tag_rules_are_capped()
    {
        var many = Enumerable.Range(0, CapacityPlanningSettings.MaxTagRules + 10)
            .Select(i => new PlanningTagRule { Tag = $"tag{i}" })
            .ToList();

        var s = new CapacityPlanningSettings { TagRules = many }.Normalized();

        Assert.Equal(CapacityPlanningSettings.MaxTagRules, s.TagRules.Count);
    }

    [Fact]
    public void Blank_marker_tags_and_empty_teams_fall_back_to_defaults()
    {
        var s = new CapacityPlanningSettings
        {
            DeliverableMarkerTag = "  ",
            NonDeliverableMarkerTag = "",
            DevDisciplines = new(),
            QaDisciplines = new() { " ", "" }
        }.Normalized();

        Assert.Equal("Deliverable", s.DeliverableMarkerTag);
        Assert.Equal("Non-Deliverable", s.NonDeliverableMarkerTag);
        Assert.Equal(new[] { "Development" }, s.DevDisciplines);
        Assert.Equal(new[] { "Test" }, s.QaDisciplines);
    }

    [Fact]
    public void Null_collections_from_a_damaged_file_do_not_throw()
    {
        var s = new CapacityPlanningSettings { WorkingDays = null!, TagRules = null!, DevDisciplines = null!, QaDisciplines = null! };

        var n = s.Normalized();

        Assert.Equal(5, n.DaysPerWeek);
        Assert.Equal(4, n.TagRules.Count);
        Assert.Equal(new[] { "Development" }, n.DevDisciplines);
    }

    [Fact]
    public void Clone_is_deep_so_a_draft_edit_never_leaks_into_the_saved_settings()
    {
        var saved = new CapacityPlanningSettings();
        var draft = saved.Clone();

        draft.TagRules[0].Deliverable = true;
        draft.WorkingDays.Add(DayOfWeek.Saturday);
        draft.DevDisciplines.Add("Analysis");

        Assert.False(saved.TagRules[0].Deliverable);
        Assert.Equal(5, saved.WorkingDays.Count);
        Assert.Single(saved.DevDisciplines);
    }

    [Fact]
    public void Legacy_deliverable_toggles_carry_over_by_tag_ignoring_case()
    {
        var s = CapacityPlanningSettings.FromLegacy(new Dictionary<string, bool>
        {
            ["alm"] = true,
            ["Regression"] = true,
            ["Team B Support"] = false,
            ["Something Else"] = true
        });

        Assert.True(s.TagRules.Single(r => r.Tag == "ALM").Deliverable);
        Assert.True(s.TagRules.Single(r => r.Tag == "Regression").Deliverable);
        Assert.False(s.TagRules.Single(r => r.Tag == "Team B Support").Deliverable);
        Assert.DoesNotContain(s.TagRules, r => r.Tag == "Something Else");
    }

    [Fact]
    public void Settings_survive_a_json_round_trip_exactly()
    {
        var original = new CapacityPlanningSettings
        {
            Mode = PlanningMode.Regular,
            AiOverPlanPercent = 25,
            HoursPerDay = 8,
            UseTfsMemberCapacity = false,
            SprintLength = SprintLengthSource.Fixed,
            SprintWeeks = 3,
            WorkingDays = { DayOfWeek.Saturday },
            TagRules = { new() { Tag = "Spike", Deliverable = true, ExcludeFromOverPlan = true } },
            DevDisciplines = { "Analysis" }
        }.Normalized();

        var restored = JsonSerializer.Deserialize<CapacityPlanningSettings>(JsonSerializer.Serialize(original))!.Normalized();

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
        // A collection with an initializer must be replaced, not appended to, on deserialise.
        Assert.Equal(6, restored.DaysPerWeek);
    }
}

/// <summary>Persistence through SettingsService: migration, round-trip, and copy semantics.</summary>
[Collection(SettingsFileCollection.Name)]
public class CapacityPlanningSettingsServiceTests : IDisposable
{
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "usersettings.json");

    public CapacityPlanningSettingsServiceTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    private static SettingsService NewService() => new(NullLogger<SettingsService>.Instance);

    [Fact]
    public void A_file_from_before_these_settings_migrates_its_deliverable_toggles_once()
    {
        File.WriteAllText(SettingsPath, """{ "DeliverableTags": { "ALM": true, "Regression": true } }""");

        var settings = NewService().CapacityPlanning;

        Assert.True(settings.TagRules.Single(r => r.Tag == "ALM").Deliverable);
        Assert.True(settings.TagRules.Single(r => r.Tag == "Regression").Deliverable);
        Assert.False(settings.TagRules.Single(r => r.Tag == "Team B Support").Deliverable);

        // Migration was persisted, so the next start reads the new section directly.
        Assert.Contains("CapacityPlanning", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Saved_settings_are_read_back_after_a_restart()
    {
        var first = NewService();
        var next = first.CapacityPlanning;
        next.Mode = PlanningMode.Regular;
        next.HoursPerDay = 12;
        next.SprintWeeks = 3;
        first.SaveCapacityPlanning(next);

        var reloaded = NewService().CapacityPlanning;

        Assert.Equal(PlanningMode.Regular, reloaded.Mode);
        Assert.Equal(12, reloaded.HoursPerDay);
        Assert.Equal(3, reloaded.SprintWeeks);
    }

    [Fact]
    public void Saving_sanitises_what_is_stored()
    {
        var service = NewService();
        var bad = service.CapacityPlanning;
        bad.AiOverPlanPercent = -50;
        bad.HoursPerDay = 0;
        service.SaveCapacityPlanning(bad);

        var stored = NewService().CapacityPlanning;

        Assert.Equal(0, stored.AiOverPlanPercent);
        Assert.Equal(CapacityPlanningSettings.DefaultHoursPerDay, stored.HoursPerDay);
    }

    [Fact]
    public void Editing_what_was_read_does_not_change_the_saved_settings_until_saved()
    {
        var service = NewService();

        var copy = service.CapacityPlanning;
        copy.AiOverPlanPercent = 90;
        copy.TagRules.Clear();

        var fresh = service.CapacityPlanning;
        Assert.Equal(40, fresh.AiOverPlanPercent);
        Assert.Equal(4, fresh.TagRules.Count);
    }
}
