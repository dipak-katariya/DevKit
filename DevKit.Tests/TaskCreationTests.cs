using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Task Creation writes real work items, so what gets ticked and how each task is tagged decide
/// what lands in TFS — and whether the Bugs audit and the capacity tab count it as bug work.
/// </summary>
public class TaskCreationTests
{
    private static TaskCreationSelection For(string? parentType) =>
        new(PlanningTaskCatalog.DefaultTasksFor(parentType));

    [Theory]
    [InlineData("Bug")]
    [InlineData("bug")]
    public void Tasks_under_a_bug_are_tagged_bug_maintenance_not_product(string parentType)
    {
        Assert.Equal(BugWork.MaintenanceTag, PlanningTaskCatalog.TagFor(parentType));
    }

    [Theory]
    [InlineData("Requirement")]
    [InlineData("Change Request")]
    [InlineData(null)]
    public void Tasks_under_anything_else_keep_the_product_tag(string? parentType)
    {
        Assert.Equal(PlanningTaskCatalog.ProductTag, PlanningTaskCatalog.TagFor(parentType));
    }

    [Fact]
    public void A_bug_task_tag_is_one_the_bug_audit_and_capacity_tab_recognise()
    {
        var task = new WorkItem { Type = "Task", Tags = PlanningTaskCatalog.TagFor("Bug") };

        Assert.True(task.HasTag(BugWork.MaintenanceTag));
        Assert.True(BugWork.Is(task, parentIsBug: false));
    }

    [Theory]
    [InlineData("Requirement")]
    [InlineData("Change Request")]
    [InlineData(null)]
    public void Planned_work_defaults_to_the_standard_development_set(string? parentType)
    {
        var defaults = PlanningTaskCatalog.DefaultTasksFor(parentType);

        Assert.Equal(9, defaults.Count);
        Assert.Equal(PlanningTaskCatalog.DevelopmentPrefix, defaults[0]);
    }

    [Theory]
    [InlineData("Bug")]
    [InlineData("bug")]
    public void A_bug_defaults_to_resolution_and_retesting_only(string parentType)
    {
        var defaults = PlanningTaskCatalog.DefaultTasksFor(parentType);

        Assert.Equal(new[] { "Bug Resolution", "Bug Retesting" }, defaults);
    }

    [Theory]
    [InlineData("Requirement")]
    [InlineData("Bug")]
    public void Every_default_task_is_a_real_catalog_category(string parentType)
    {
        var catalog = PlanningTaskCatalog.Categories.Select(c => c.Prefix).ToHashSet();
        var defaults = PlanningTaskCatalog.DefaultTasksFor(parentType);

        Assert.All(defaults, prefix => Assert.Contains(prefix, catalog));
        Assert.Equal(defaults.Count, defaults.Distinct().Count());
    }

    [Fact]
    public void Ticking_code_development_alone_selects_only_code_development()
    {
        var selection = For("Requirement");

        selection.Toggle(PlanningTaskCatalog.DevelopmentPrefix);

        Assert.Equal(1, selection.Count);
        Assert.True(selection.Contains(PlanningTaskCatalog.DevelopmentPrefix));
        Assert.False(selection.AllDefaultsSelected);
    }

    [Fact]
    public void Default_tasks_ticks_exactly_the_standard_set()
    {
        var selection = For("Requirement");

        selection.ToggleDefaults();

        Assert.True(selection.AllDefaultsSelected);
        Assert.Equal(9, selection.Count);
        Assert.All(selection.Defaults, prefix => Assert.True(selection.Contains(prefix)));
    }

    [Fact]
    public void Default_tasks_under_a_bug_ticks_only_the_two_bug_tasks()
    {
        var selection = For("Bug");

        selection.ToggleDefaults();

        Assert.Equal(2, selection.Count);
        Assert.Equal(new[] { "Bug Resolution", "Bug Retesting" }, selection.InCatalogOrder().Select(c => c.Prefix));
        Assert.False(selection.Contains(PlanningTaskCatalog.DevelopmentPrefix));
    }

    [Fact]
    public void A_bug_marks_only_its_own_two_tasks_as_default()
    {
        var selection = For("Bug");

        Assert.True(selection.IsDefault("Bug Resolution"));
        Assert.True(selection.IsDefault("Bug Retesting"));
        Assert.False(selection.IsDefault(PlanningTaskCatalog.DevelopmentPrefix));
        Assert.False(selection.IsDefault("Wiki Review"));
    }

    [Fact]
    public void A_bug_can_still_take_any_other_category()
    {
        var selection = For("Bug");
        selection.ToggleDefaults();

        selection.Toggle("Technical Analysis");

        Assert.Equal(3, selection.Count);
        Assert.True(selection.AllDefaultsSelected);
    }

    [Fact]
    public void Unticking_default_tasks_keeps_other_ticks()
    {
        var selection = For("Requirement");
        selection.Toggle("Support");
        selection.ToggleDefaults();

        selection.ToggleDefaults();

        Assert.Equal(1, selection.Count);
        Assert.True(selection.Contains("Support"));
        Assert.Equal(0, selection.SelectedDefaults);
    }

    [Fact]
    public void Default_tasks_completes_a_partial_set_instead_of_clearing_it()
    {
        var selection = For("Requirement");
        selection.ToggleDefaults();
        selection.Toggle("Code Review");
        Assert.False(selection.AllDefaultsSelected);
        Assert.Equal(8, selection.SelectedDefaults);

        selection.ToggleDefaults();

        Assert.True(selection.AllDefaultsSelected);
    }

    [Fact]
    public void Prefixes_outside_the_catalog_are_ignored()
    {
        var selection = For("Requirement");

        selection.Toggle("Not a real category");

        Assert.Equal(0, selection.Count);
    }

    [Fact]
    public void Defaults_outside_the_catalog_are_dropped_rather_than_offered()
    {
        var selection = new TaskCreationSelection(new[] { "Code Review", "Not a real category" });

        Assert.Equal(new[] { "Code Review" }, selection.Defaults);
        Assert.False(selection.IsDefault("Not a real category"));

        selection.ToggleDefaults();

        Assert.Equal(1, selection.Count);
    }

    [Fact]
    public void Tasks_are_created_in_catalog_order_whatever_the_tick_order()
    {
        var selection = For("Requirement");
        selection.Toggle("TC Review");
        selection.Toggle("Plan Preparation");
        selection.Toggle("Code Review");

        var order = selection.InCatalogOrder().Select(c => c.Prefix);

        Assert.Equal(new[] { "Plan Preparation", "Code Review", "TC Review" }, order);
    }

    [Fact]
    public void After_a_partial_failure_only_the_failed_categories_stay_ticked()
    {
        var selection = For("Requirement");
        selection.ToggleDefaults();

        selection.RetainOnly(new[] { "Wiki Review", "L2 Testing" });

        Assert.Equal(2, selection.Count);
        Assert.True(selection.Contains("Wiki Review"));
        Assert.True(selection.Contains("L2 Testing"));
    }
}
