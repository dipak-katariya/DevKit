using DevKit.Web.Components;
using DevKit.Web.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// A Code Merging Sheet row's own buttons, read from its markup: Copy details and Update status are on every row, say
/// what a click does, stay off when there is nothing for them to do, and show how their last click went.
/// </summary>
public sealed class MergingRowActionTests
{
    private const string CopyLabel = "Copy details of 1001 for the merge sheet";
    private const string StatusLabel = "Update status of 1001";
    private const string StatusTitle = "Update status: adds PT Merged. Shows what changes first.";

    private static readonly MergingRow.StatusButton SetUp = new(true, StatusTitle);

    private readonly List<RequirementMergingRow> _copied = new();
    private readonly List<(RequirementMergingRow Row, MergingPullRequest Pr)> _copiedPrs = new();
    private readonly List<RequirementMergingRow> _updated = new();

    private static MergingPullRequest Pr(int id, string status = "completed") => new() { PullRequestId = id, Status = status };

    private static RequirementMergingRow Row(params MergingPullRequest[] prs) =>
        new() { WorkItemId = 1001, Title = "Login page", PullRequests = prs.ToList() };

    private Dictionary<string, object?> Parameters(RequirementMergingRow row, MergingRow.StatusButton? status = null) => new()
    {
        [nameof(MergingRow.Row)] = row,
        [nameof(MergingRow.VisiblePrs)] = row.PullRequests,
        [nameof(MergingRow.Status)] = status ?? MergingRow.StatusButton.NotSetUp,
        [nameof(MergingRow.OnCopyRow)] = EventCallback.Factory.Create<RequirementMergingRow>(this, _copied.Add),
        [nameof(MergingRow.OnCopyPr)] = EventCallback.Factory.Create<(RequirementMergingRow Row, MergingPullRequest Pr)>(this, _copiedPrs.Add),
        [nameof(MergingRow.OnUpdateStatus)] = EventCallback.Factory.Create<RequirementMergingRow>(this, _updated.Add)
    };

    private static async Task<ComponentHarness.Element> ButtonAsync(ComponentHarness harness, int row, string label) =>
        (await harness.ElementsAsync(row)).Single(e => e.Name == "button" && e.Attribute("aria-label") == label);

    private static Task<bool> ClickAsync(ComponentHarness harness, int row, string label) =>
        harness.RaiseAsync(row, e => e.Name == "button" && e.Attribute("aria-label") == label, "onclick", new MouseEventArgs());

    [Fact]
    public async Task Update_status_is_off_and_points_to_Settings_until_it_is_set_up()
    {
        using var harness = ComponentHarness.Create(_ => { });
        var row = await harness.RenderAsync<MergingRow>(Parameters(Row(Pr(1))));

        var button = await ButtonAsync(harness, row, StatusLabel);

        Assert.NotNull(button.Attribute("disabled"));
        Assert.Equal(MergingRow.StatusButton.NotSetUp.Title, button.Attribute("title"));
        Assert.Contains("Settings", button.Attribute("title"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_set_up_Update_status_says_what_it_does_and_a_click_asks_the_page_for_this_row()
    {
        using var harness = ComponentHarness.Create(_ => { });
        var target = Row(Pr(1));
        var row = await harness.RenderAsync<MergingRow>(Parameters(target, SetUp));

        var button = await ButtonAsync(harness, row, StatusLabel);
        Assert.Null(button.Attribute("disabled"));
        Assert.Equal(StatusTitle, button.Attribute("title"));

        Assert.True(await ClickAsync(harness, row, StatusLabel));
        Assert.Same(target, Assert.Single(_updated));
    }

    [Fact]
    public async Task Copy_details_counts_the_rows_it_copies_and_leaves_abandoned_pull_requests_out()
    {
        using var harness = ComponentHarness.Create(_ => { });
        var target = Row(Pr(1), Pr(2, "active"), Pr(3, "abandoned"));
        var row = await harness.RenderAsync<MergingRow>(Parameters(target));

        var button = await ButtonAsync(harness, row, CopyLabel);
        Assert.Null(button.Attribute("disabled"));
        Assert.Equal(
            "Copy details: 2 rows for the merge sheet, one per pull request. Paste into the Name cell of an empty row. 1 abandoned pull request left out.",
            button.Attribute("title"));

        Assert.True(await ClickAsync(harness, row, CopyLabel));
        Assert.Same(target, Assert.Single(_copied));
    }

    [Theory]
    [InlineData(false, "No pull requests to copy")]
    [InlineData(true, "Its pull requests were abandoned, so there is nothing to copy")]
    public async Task Copy_details_is_off_when_there_is_nothing_to_copy(bool abandonedOnly, string title)
    {
        using var harness = ComponentHarness.Create(_ => { });
        var row = await harness.RenderAsync<MergingRow>(Parameters(abandonedOnly ? Row(Pr(3, "abandoned")) : Row()));

        var button = await ButtonAsync(harness, row, CopyLabel);

        Assert.NotNull(button.Attribute("disabled"));
        Assert.Equal(title, button.Attribute("title"));
    }

    [Fact]
    public async Task A_button_at_work_keeps_focus_and_says_it_is_busy_and_a_finished_one_shows_what_happened()
    {
        using var harness = ComponentHarness.Create(_ => { });
        var target = Row(Pr(1));
        target.CopyAction = new RowAction(RowActionState.Busy, "Reading the commits…");
        target.StatusAction = new RowAction(RowActionState.Done, "Updated #1001: added PT Merged.");
        var row = await harness.RenderAsync<MergingRow>(Parameters(target, SetUp));

        // Disabling it would drop keyboard focus to the page; the page ignores a second click instead.
        var copy = await ButtonAsync(harness, row, CopyLabel);
        Assert.Null(copy.Attribute("disabled"));
        Assert.Equal("true", copy.Attribute("aria-busy"));
        Assert.Equal("Reading the commits…", copy.Attribute("title"));
        var status = await ButtonAsync(harness, row, StatusLabel);
        Assert.Null(status.Attribute("disabled"));
        Assert.Null(status.Attribute("aria-busy"));
        Assert.Contains("cm-act-done", status.Attribute("class"), StringComparison.Ordinal);
        Assert.Equal($"Updated #1001: added PT Merged.\n{StatusTitle}", status.Attribute("title"));
    }

    [Fact]
    public async Task Each_pull_request_has_its_own_copy_and_an_abandoned_one_has_it_off()
    {
        using var harness = ComponentHarness.Create(_ => { });
        var merged = Pr(1);
        var target = Row(merged, Pr(2, "abandoned"), Pr(3, "active"));
        target.IsExpanded = true;
        var row = await harness.RenderAsync<MergingRow>(Parameters(target));

        var abandoned = await ButtonAsync(harness, row, "Copy details of !2 for the merge sheet");
        Assert.NotNull(abandoned.Attribute("disabled"));
        Assert.Equal("Abandoned, so it has no row in the merge sheet", abandoned.Attribute("title"));
        var active = await ButtonAsync(harness, row, "Copy details of !3 for the merge sheet");
        Assert.Equal("Copy details: the merge sheet row of !3. It is not merged yet, so DATE stays empty.", active.Attribute("title"));

        Assert.True(await ClickAsync(harness, row, "Copy details of !1 for the merge sheet"));
        var copied = Assert.Single(_copiedPrs);
        Assert.Same(target, copied.Row);
        Assert.Same(merged, copied.Pr);
    }
}
