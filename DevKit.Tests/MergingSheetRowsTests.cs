using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Copy details: the rows of the team's merge sheet that a work item or a pull request puts on the clipboard. Each row
/// has the sheet's 19 columns, filled only where DevKit knows the value, so pasting into a row's first cell fills it
/// and leaves the rest of the row empty.
/// </summary>
public sealed class MergingSheetRowsTests
{
    private const string Requirement = "PHOENIX : 1001 : Login page";
    private const string WorkItemUrl = "https://tfs.example.test/tfs/Collection/Contoso/_workitems/edit/1001";

    // India is five and a half hours ahead, so a pull request closed in the evening in UTC was merged the next day there.
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("DevKit test IST", TimeSpan.FromMinutes(330), "IST", "IST");

    private static readonly RequirementMergingRow WorkItem = new() { WorkItemId = 1001, Title = "Login page", WorkItemUrl = WorkItemUrl };

    private static MergingCommit Commit(string id, string message) => new() { CommitId = id, Message = message };

    private static MergingPullRequest Pr(int id, string status = "completed", DateTime? closed = null, IEnumerable<MergingCommit>? commits = null) => new()
    {
        PullRequestId = id,
        Title = $"PHOENIX : 1001 : Login page part {id}",
        Status = status,
        SourceBranch = $"PHOENIX/PI2026_4_2/1001_login_page_{id}",
        Url = $"https://tfs.example.test/tfs/Collection/Contoso/_git/Web/pullrequest/{id}",
        ClosedDate = closed,
        Commits = (commits ?? Enumerable.Empty<MergingCommit>()).ToList()
    };

    private static List<MergingSheetRows.Row> Rows(params MergingPullRequest[] prs) =>
        MergingSheetRows.For(WorkItem, prs, Requirement, India);

    [Fact]
    public void A_row_fills_only_DATE_REQ_CHR_TFS_ID_Pull_Request_Branch_Name_and_Commit_IDs()
    {
        var pr = Pr(97953, closed: new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc),
            commits: new[] { Commit("a1b2c3d4e5", "PHOENIX : 1001 : Login page"), Commit("f6e5d4c3b2", "PHOENIX : 1001 : Review fixes") });

        var cells = Assert.Single(Rows(pr)).Cells;

        Assert.Equal(MergingSheetRows.ColumnCount, cells.Count);
        Assert.Equal("2026-11-16", cells[MergingSheetRows.DateColumn]);
        Assert.Equal(Requirement, cells[MergingSheetRows.RequirementColumn]);
        Assert.Equal("Pull Request 97953: PHOENIX : 1001 : Login page part 97953", cells[MergingSheetRows.PullRequestColumn]);
        Assert.Equal("PHOENIX/PI2026_4_2/1001_login_page_97953", cells[MergingSheetRows.BranchColumn]);
        Assert.Equal("a1b2c3d4e5\nf6e5d4c3b2", cells[MergingSheetRows.CommitsColumn]);
        var filled = new[]
        {
            MergingSheetRows.DateColumn, MergingSheetRows.RequirementColumn, MergingSheetRows.PullRequestColumn,
            MergingSheetRows.BranchColumn, MergingSheetRows.CommitsColumn
        };
        Assert.All(Enumerable.Range(0, MergingSheetRows.ColumnCount).Except(filled), column => Assert.Equal("", cells[column]));
    }

    [Fact]
    public void The_date_is_the_day_it_was_merged_where_the_sheet_is_kept()
    {
        var cells = Assert.Single(Rows(Pr(1, closed: new DateTime(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc)))).Cells;

        Assert.Equal("2026-11-17", cells[MergingSheetRows.DateColumn]);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("notSet")]
    public void A_pull_request_not_merged_yet_has_no_date(string status)
    {
        var cells = Assert.Single(Rows(Pr(1, status, closed: new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc)))).Cells;

        Assert.Equal("", cells[MergingSheetRows.DateColumn]);
    }

    [Fact]
    public void Merges_from_other_branches_and_rebase_commits_are_left_out_of_Commit_IDs()
    {
        var pr = Pr(1, commits: new[]
        {
            Commit("1111111111", "PHOENIX : 1001 : Login page"),
            Commit("2222222222", "Merge branch 'main' into PHOENIX/PI2026_4_2/1001_login_page"),
            Commit("3333333333", "Merge remote-tracking branch 'origin/dev-qa'"),
            Commit("4444444444", "PHOENIX : Rebase and conflict resolve"),
            Commit("5555555555", "Rebased onto release/26.4"),
            Commit("6666666666", "Fix rebase of the import job"),
            Commit("7777777777", "PHOENIX : Merge branch 'dev-qa-net8' into PHOENIX/PI2026_4_2/1001_login_page"),
            Commit("8888888888", "Merge from dev"),
            Commit("9999999999", "Merged PR 92599: PHOENIX : 1608055 : delivery mode"),
            Commit("aaaaaaaaaa", "Merge fromDate and toDate on the report")
        });

        var cells = Assert.Single(Rows(pr)).Cells;

        Assert.Equal("1111111111\n6666666666\naaaaaaaaaa", cells[MergingSheetRows.CommitsColumn]);
    }

    [Theory]
    [InlineData("Merge branch 'main' into feature/x", true)]
    [InlineData("FXCPMGR : Merge remote-tracking branch 'origin/dev'", true)]
    [InlineData("merge from main", true)]
    [InlineData("Merge from", true)]
    [InlineData("Merged PR 92599: FXCPMGR : 1608055 : delivery mode", true)]
    [InlineData("Merged PR notes into the guide", false)]
    [InlineData("Merged PR 12 without a colon", false)]
    [InlineData("Some team : Merge branch 'main'", false)]
    [InlineData("Merge fromDate fields", false)]
    [InlineData("FXCPMGR : 1702401 : Merge the two grids", false)]
    [InlineData("FXCPMGR : 92599 : Merge from QA", true)]
    [InlineData("PHOENIX : Merge from dev", true)]
    [InlineData("FXCPMGR : Merged PR 1: x", true)]
    [InlineData("Merge branches dropdown into one picker", false)]
    [InlineData("FXCPMGR : Merge branch-name validation fix", false)]
    [InlineData("fix: Merge branch list ordering", false)]
    [InlineData("Bug: Merge branch picker crash", false)]
    [InlineData("feat: Merge from template support", false)]
    [InlineData("Hotfix: Merge from address parser", false)]
    [InlineData("UI: Merge from the sidebar", false)]
    [InlineData("Fix the grid\nNote: Merge from dev later", false)]
    public void A_merge_from_commit_brought_another_branch_in(string message, bool expected)
    {
        Assert.Equal(expected, new MergingCommit { Message = message }.IsMergeFromCommit);
    }

    [Fact]
    public void Verification_still_requires_a_Merged_PR_commit()
    {
        // Copy details leaves it out as work brought in from another pull request, but verification still requires it:
        // IsBranchMergeCommit covers only git's own merge subjects, and the change it carries must reach the branch.
        Assert.False(new MergingCommit { Message = "Merged PR 92599: FXCPMGR : 1608055 : delivery mode" }.IsBranchMergeCommit);
    }

    [Fact]
    public void Developer_commits_that_only_look_like_merges_or_rebases_stay_in_Commit_IDs()
    {
        var pr = Pr(1, commits: new[]
        {
            Commit("1111111111", "fix: rebase handling of import dates"),
            Commit("2222222222", "Bug: Merge branch picker crash"),
            Commit("3333333333", "Merge branches dropdown into one picker"),
            Commit("4444444444", "Fix the import\nNote: rebase onto dev first"),
            Commit("5555555555", "FXCPMGR : 92599 : Merge from QA")
        });

        var cells = Assert.Single(Rows(pr)).Cells;

        Assert.Equal("1111111111\n2222222222\n3333333333\n4444444444", cells[MergingSheetRows.CommitsColumn]);
        Assert.True(MergingSheetRows.HasOwnCommits(pr));
    }

    [Fact]
    public void A_pull_request_with_only_merges_and_rebases_has_no_own_commits()
    {
        var pr = Pr(1, commits: new[] { Commit("1111111111", "Merge from dev"), Commit("2222222222", "PHOENIX : Rebase branch") });

        Assert.False(MergingSheetRows.HasOwnCommits(pr));
        Assert.False(MergingSheetRows.HasOwnCommits(Pr(2)));
        Assert.Equal("", Assert.Single(Rows(pr)).Cells[MergingSheetRows.CommitsColumn]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MergingSheetRows.ColumnCount - 1)]
    [InlineData(MergingSheetRows.ColumnCount + 1)]
    public void A_row_must_have_every_column_of_the_sheet(int count)
    {
        Assert.Throws<ArgumentException>(() => new MergingSheetRows.Row(new string[count], WorkItemUrl, ""));
    }

    [Fact]
    public void A_full_width_row_can_be_built()
    {
        var row = new MergingSheetRows.Row(Enumerable.Repeat("", MergingSheetRows.ColumnCount).ToArray(), WorkItemUrl, "");

        Assert.Equal(MergingSheetRows.ColumnCount, row.Cells.Count);
    }

    [Fact]
    public void Each_pull_request_is_a_row_in_its_order_and_abandoned_ones_are_left_out()
    {
        var rows = Rows(Pr(3), Pr(1, "abandoned"), Pr(2, "active"));

        Assert.Equal(new[] { "Pull Request 3", "Pull Request 2" },
            rows.Select(row => row.Cells[MergingSheetRows.PullRequestColumn].Split(':')[0]));
        Assert.All(rows, row => Assert.Equal(WorkItemUrl, row.RequirementUrl));
    }

    [Fact]
    public void Line_breaks_and_tabs_from_TFS_never_move_a_value_into_another_cell()
    {
        var pr = Pr(1);
        pr.Title = "Login\tpage\r\nsecond line";
        pr.SourceBranch = " feature/x\n";

        var cells = Assert.Single(MergingSheetRows.For(WorkItem, new[] { pr }, "PHOENIX : 1001 : Login\npage", India)).Cells;

        Assert.Equal("Pull Request 1: Login page second line", cells[MergingSheetRows.PullRequestColumn]);
        Assert.Equal("feature/x", cells[MergingSheetRows.BranchColumn]);
        Assert.Equal("PHOENIX : 1001 : Login page", cells[MergingSheetRows.RequirementColumn]);
    }

    [Fact]
    public void As_text_cells_are_tab_separated_rows_end_in_CRLF_and_a_multi_line_cell_is_quoted()
    {
        var first = Pr(1, commits: new[] { Commit("aaaa", "work"), Commit("bbbb", "more \"work\"") });
        var second = Pr(2, commits: new[] { Commit("cccc", "work") });

        var text = MergingSheetRows.ToText(Rows(first, second));

        var lines = text.Split("\r\n");
        Assert.Equal(2, lines.Length);
        Assert.Equal(MergingSheetRows.ColumnCount, lines[1].Split('\t').Length);
        Assert.Contains("\t\"aaaa\nbbbb\"\t", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("\tcccc\t", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://evil.example\")")]
    [InlineData("+1+1")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    public void A_cell_Excel_would_read_as_a_formula_is_kept_as_text(string title)
    {
        var text = MergingSheetRows.ToText(MergingSheetRows.For(WorkItem, new[] { Pr(1) }, title, India));

        var requirement = text.Split('\t')[MergingSheetRows.RequirementColumn];
        Assert.StartsWith("'", requirement.Trim('"'), StringComparison.Ordinal);
    }

    [Fact]
    public void As_HTML_the_work_item_and_the_pull_request_are_links_and_text_cells_stay_text()
    {
        var pr = Pr(97953, commits: new[] { Commit("0123456789", "work"), Commit("1e10", "work") });

        var html = MergingSheetRows.ToHtml(Rows(pr));

        Assert.StartsWith("<table", html, StringComparison.Ordinal);
        Assert.Equal(MergingSheetRows.ColumnCount, CountOf(html, "<td"));
        Assert.Contains($"<a href=\"{WorkItemUrl}\">{Requirement}</a>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://tfs.example.test/tfs/Collection/Contoso/_git/Web/pullrequest/97953\">Pull Request 97953:", html, StringComparison.Ordinal);
        Assert.Contains("0123456789<br style=\"mso-data-placement:same-cell\">1e10", html, StringComparison.Ordinal);
        // REQ/CHR, Pull Request and Branch Name as text; the commit list over two lines stays General; the 15 empty
        // cells (DATE too, as this one has no merge date) carry no format, so what is typed there later reads as usual.
        Assert.Equal(3, CountOf(html, "mso-number-format:'\\@'"));
        Assert.Equal(15, CountOf(html, "<td></td>"));
        // The sheet styles its own cells; a style sheet in the HTML would also be rewritten by the browser on copy.
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void As_HTML_the_date_goes_out_year_first_and_shows_with_slashes()
    {
        var html = MergingSheetRows.ToHtml(Rows(Pr(1, closed: new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc))));

        Assert.Contains("<td style=\"mso-number-format:'m\\/d\\/yyyy'\">2026-11-05</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_cell_longer_than_Excel_shows_stays_General_unless_it_would_start_a_formula()
    {
        var pr = Pr(1, commits: new[] { Commit(new string('a', 40), "work") });
        pr.Title = new string('t', 300);

        var html = MergingSheetRows.ToHtml(MergingSheetRows.For(WorkItem, new[] { pr }, "=" + new string('x', 300), India));

        // A single commit id: text, so a run of digits is not turned into a number.
        Assert.Contains($"<td style=\"mso-number-format:'\\@'\">{new string('a', 40)}</td>", html, StringComparison.Ordinal);
        // The long Pull Request cell would show ##### as text; the formula-like REQ/CHR cell is kept as text anyway.
        Assert.Contains("<td><a href=", html, StringComparison.Ordinal);
        Assert.Contains($"<td style=\"mso-number-format:'\\@'\"><a href=\"{WorkItemUrl}\">=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void As_HTML_text_from_TFS_is_encoded_and_only_web_addresses_become_links()
    {
        var pr = Pr(1);
        pr.Title = "<img src=x onerror=alert(1)> & co";
        pr.Url = "javascript:alert(1)";

        var html = MergingSheetRows.ToHtml(MergingSheetRows.For(WorkItem, new[] { pr }, "<b>title</b>", India));

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt; &amp; co", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;title&lt;/b&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_work_item_without_pull_requests_to_copy_has_no_rows()
    {
        Assert.Empty(Rows(Pr(1, "abandoned")));
        Assert.Equal("", MergingSheetRows.ToText(Rows()));
    }

    [Theory]
    [InlineData("Rebase", true)]
    [InlineData("rebased onto main", true)]
    [InlineData("Rebasing feature branch", true)]
    [InlineData("  PHOENIX : Rebase and conflict resolve", true)]
    [InlineData("FXCPMGR : rebase with dev", true)]
    [InlineData("Rebase: pick up main", true)]
    // The team writes "TEAM : "; without the space an upper-case word reads like a commit type ("UI:"), so it stays.
    [InlineData("FXCPMGR: rebase with dev", false)]
    [InlineData("fix: rebase handling of import dates", false)]
    [InlineData("Fix: Rebase button misaligned", false)]
    [InlineData("UI: rebase toolbar icons", false)]
    [InlineData("Fix the import\nNote: rebase onto dev first", false)]
    [InlineData("Rebase branch\nFXCPMGR : details", true)]
    [InlineData("Fix rebase of the import job", false)]
    [InlineData("Rebasement of the tables", false)]
    [InlineData("PHOENIX : 1001 : Rebase button on the page", false)]
    [InlineData("Some team name : Rebase", false)]
    [InlineData("", false)]
    public void A_rebase_commit_opens_with_the_word_rebase_after_at_most_a_one_word_team(string message, bool expected)
    {
        Assert.Equal(expected, new MergingCommit { Message = message }.IsRebaseCommit);
    }

    [Theory]
    [InlineData("Phoenix", "1001", "Login page", "PHOENIX : 1001 : Login page")]
    [InlineData("", "1001", "Login page", "TEAM : 1001 : Login page")]
    public void The_REQ_CHR_name_is_the_pull_request_title_Branch_Creator_suggests(string team, string id, string title, string expected)
    {
        Assert.Equal(expected, BranchNaming.PullRequestName(team, id, title));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
