using System.Reflection;
using DevKit.Web.Components;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The pill and badge colours every grid takes from WorkItemBadges, so one work-item state, type or
/// pull-request status reads the same on every page.
/// </summary>
public sealed class WorkItemBadgesTests
{
    private static readonly string[] BadgeRuleNames =
    {
        "StateCls", "TypeCls", "PrStatusCls",
        nameof(WorkItemBadges.StateClass), nameof(WorkItemBadges.TypeClass), nameof(WorkItemBadges.PrStatusClass)
    };

    // "Inactive" reading as active is the keyword rule's known trade-off; it is pinned here so tightening it
    // to whole words is a deliberate, visible change.
    [Theory]
    [InlineData("Active", "state-active")]
    [InlineData("ACTIVE", "state-active")]
    [InlineData("In Progress", "state-active")]
    [InlineData("Inactive", "state-active")]
    [InlineData("Resolved", "state-resolved")]
    [InlineData("Done", "state-resolved")]
    [InlineData("Completed", "state-resolved")]
    [InlineData("Closed", "state-closed")]
    [InlineData("Proposed", "state-new")]
    [InlineData("New", "state-new")]
    [InlineData("Removed", "state-new")]
    [InlineData("", "state-new")]
    [InlineData(null, "state-new")]
    public void States_are_coloured_by_keyword(string? state, string expected)
    {
        Assert.Equal(expected, WorkItemBadges.StateClass(state));
    }

    [Theory]
    [InlineData("Bug", "bug")]
    [InlineData("bug", "bug")]
    [InlineData("Change Request", "cr")]
    [InlineData("Requirement", "")]
    [InlineData("Task", "")]
    [InlineData(null, "")]
    public void Bugs_and_change_requests_get_their_own_type_colour(string? type, string expected)
    {
        Assert.Equal(expected, WorkItemBadges.TypeClass(type));
    }

    [Theory]
    [InlineData("completed", "pr-completed")]
    [InlineData("abandoned", "pr-abandoned")]
    [InlineData("active", "pr-active")]
    [InlineData("notSet", "pr-active")]
    [InlineData("", "pr-active")]
    [InlineData(null, "pr-active")]
    public void Any_pull_request_status_but_completed_or_abandoned_reads_as_active(string? status, string expected)
    {
        Assert.Equal(expected, WorkItemBadges.PrStatusClass(status));
    }

    [Fact]
    public void No_component_keeps_its_own_badge_rule()
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static | BindingFlags.Instance;

        var copies = typeof(WorkItemBadges).Assembly.GetTypes()
            .Where(t => t != typeof(WorkItemBadges))
            .SelectMany(t => t.GetMethods(Declared).Where(m => BadgeRuleNames.Contains(m.Name, StringComparer.Ordinal))
                .Select(m => $"{t.Name}.{m.Name}"));

        Assert.Empty(copies);
    }
}
