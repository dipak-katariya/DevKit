using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

// What counts as bug work decides which hours the capacity tab keeps out of Completed
// and which the burndown colours as bug time, so both tabs agree.

public class BugWorkTests
{
    [Fact]
    public void A_bug_itself_is_bug_work() =>
        Assert.True(BugWork.Is(new WorkItem { Type = "Bug" }, parentIsBug: false));

    [Fact]
    public void A_task_under_a_bug_is_bug_work() =>
        Assert.True(BugWork.Is(new WorkItem { Type = "Task" }, parentIsBug: true));

    [Fact]
    public void A_task_tagged_bug_maintenance_is_bug_work() =>
        Assert.True(BugWork.Is(new WorkItem { Type = "Task", Tags = "Product; bug/maintenance" }, parentIsBug: false));

    [Fact]
    public void An_ordinary_task_is_not() =>
        Assert.False(BugWork.Is(new WorkItem { Type = "Task", Tags = "Product" }, parentIsBug: false));

    [Theory]
    [InlineData("Bug", true)]
    [InlineData("bug", true)]
    [InlineData("Task", false)]
    [InlineData(null, false)]
    public void The_type_check_ignores_case(string? workItemType, bool expected) =>
        Assert.Equal(expected, BugWork.IsBugType(workItemType));
}
