using DevKit.Web.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The sprint every page starts on: the sprint running today by its dates in TFS, or the one chosen in Settings.
/// Settings saved before the choice existed keep the sprint they named.
/// </summary>
public sealed class DefaultSprintTests
{
    private static readonly DateTime Today = Day(10, 16);

    private static readonly TfsIteration[] Sprints =
    {
        Sprint("Alpha", "PI-5_1", Day(9, 21), Day(10, 2)),
        Sprint("Alpha", "PI-5_2", Day(10, 5), Day(10, 16)),
        Sprint("Alpha", "PI-5_3", Day(10, 19), Day(10, 30)),
        Sprint("Beta", "PI-5_2", Day(10, 12), Day(10, 23)),
        new TfsIteration { Project = "Alpha", Name = "Backlog", Path = @"Alpha\Backlog" }
    };

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Local);

    private static TfsIteration Sprint(string project, string name, DateTime start, DateTime finish) => new()
    {
        Project = project, Name = name, Path = $@"{project}\{name}", StartDate = start, FinishDate = finish
    };

    private static SettingsService NewSettings()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, new MemorySettingsStore(), "https://tfs.example.test/tfs/Collection");
        settings.LoadFromStore(allowsWindowsSignIn: false);
        return settings;
    }

    [Fact]
    public void The_current_sprint_is_the_one_whose_dates_include_today_in_the_areas_project()
    {
        Assert.Equal(@"Alpha\PI-5_2", AreaSprintSelect.CurrentSprint(Sprints, "Alpha", Today)!.Path);
        Assert.Equal(@"Beta\PI-5_2", AreaSprintSelect.CurrentSprint(Sprints, "Beta", Today)!.Path);
    }

    [Fact]
    public void Between_two_sprints_the_current_sprint_is_the_one_that_ended_last()
    {
        Assert.Equal(@"Alpha\PI-5_2", AreaSprintSelect.CurrentSprint(Sprints, "Alpha", Day(10, 17))!.Path);
    }

    [Fact]
    public void Sprints_without_dates_are_never_the_current_sprint()
    {
        Assert.Null(AreaSprintSelect.CurrentSprint(new[] { Sprints[^1] }, "Alpha", Today));
    }

    [Fact]
    public void New_settings_start_every_page_on_the_current_sprint()
    {
        var settings = NewSettings();

        Assert.True(settings.DefaultSprintIsCurrent);
        Assert.Equal(@"Alpha\PI-5_2", AreaSprintSelect.DefaultSprint(settings, Sprints, new TfsArea { Project = "Alpha" }, Today));
    }

    [Fact]
    public void A_chosen_sprint_is_where_every_page_starts_while_it_exists()
    {
        var settings = NewSettings();
        settings.DefaultSprint = @"Alpha\PI-5_1";
        settings.DefaultSprintIsCurrent = false;

        Assert.Equal(@"Alpha\PI-5_1", AreaSprintSelect.DefaultSprint(settings, Sprints, null, Today));
        settings.DefaultSprint = @"Alpha\Deleted";
        Assert.Null(AreaSprintSelect.DefaultSprint(settings, Sprints, null, Today));
    }

    [Fact]
    public void Settings_from_before_the_choice_keep_the_sprint_they_named()
    {
        var store = new MemorySettingsStore();
        store.Write("""{ "DefaultSprint": "Alpha\\PI-5_1" }""");
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, "https://tfs.example.test/tfs/Collection");
        settings.LoadFromStore(allowsWindowsSignIn: false);

        Assert.False(settings.DefaultSprintIsCurrent);
        Assert.Equal(@"Alpha\PI-5_1", AreaSprintSelect.DefaultSprint(settings, Sprints, null, Today));
    }

    [Fact]
    public void The_choice_is_saved()
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, "https://tfs.example.test/tfs/Collection");
        settings.LoadFromStore(allowsWindowsSignIn: false);

        settings.DefaultSprintIsCurrent = false;

        Assert.Contains("\"DefaultSprintMode\": \"Chosen\"", store.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/settings", "Settings · DevKit")]
    [InlineData("/insights/", "Sprint Insights · DevKit")]
    [InlineData("/", "Dashboard · DevKit")]
    [InlineData("/no-such-page", "DevKit")]
    public void The_browser_tab_names_the_tool(string path, string title)
    {
        Assert.Equal(title, ToolCatalog.PageTitle(path));
    }
}
