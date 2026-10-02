using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Branch Delete removes branches for good, so every rule that decides what it offers is pinned here:
/// what is locked, what a work item links, and what counts as an unattached team branch.
/// </summary>
public class BranchCleanupTests
{
    private const string RepoId = "acef7bab-9ec9-4469-9abb-24adbc320d75";
    private static readonly TfsRepo Repo = new() { Id = RepoId, Name = "FX-CPFOIA-Migration", Project = "CasepointARA" };

    private static readonly HashSet<string> NoOpenPrs = new(StringComparer.OrdinalIgnoreCase);

    private static TfsRef Ref(string name, string creator = "Dipak Katariya") =>
        new() { Name = name, ObjectId = new string('a', 40), Creator = creator };

    private static RepoBranches Listing(IReadOnlySet<string>? openPrs, params string[] branches) =>
        new(Repo, branches.Select(b => Ref(b)).ToList(), openPrs, Array.Empty<string>());

    private static BranchCleanupQuery Query(string team = "FXCPMGR", string[]? sprints = null, string[]? configured = null) => new()
    {
        Repos = new[] { Repo },
        TeamName = team,
        SprintPaths = sprints ?? Array.Empty<string>(),
        ConfiguredBranchesByRepoId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [RepoId] = configured ?? Array.Empty<string>()
        }
    };

    private static WorkItemBranches LinksFrom(string id, params string[] branches) => new(
        new WorkItem { Id = id, Title = $"Work item {id}", State = "Closed", Type = "Requirement", Sprint = "PI-2026-3_3", Project = "CasepointARA" },
        branches.Select(b => new WorkItemBranchLink { RepositoryId = RepoId, BranchName = b }).ToList());

    private static BranchCleanupRow Row(List<BranchCleanupRow> rows, string branch) => rows.Single(r => r.Branch == branch);

    // ─── What is locked ───

    [Theory]
    [InlineData("main")]
    [InlineData("MASTER")]
    [InlineData("develop")]
    [InlineData("release")]
    [InlineData("hotfix")]
    [InlineData("release/FXCPMGR_26.4.2.x")]
    public void Long_lived_branches_are_always_locked(string branch)
    {
        Assert.Equal(BranchCleanupClassifier.ProtectedName,
            BranchCleanupClassifier.ProtectedReason(branch, NoOpenPrs, NoOpenPrs));
    }

    [Fact]
    public void The_qa_and_merging_branches_from_settings_are_locked_whatever_they_are_called()
    {
        var rows = BranchCleanupClassifier.BuildRows(
            new[] { Listing(NoOpenPrs, "dev-qa-net8", "FXCPMGR/PI2026_3_3/1_x") },
            Query(configured: new[] { "refs/heads/dev-qa-net8" }));

        Assert.Equal(BranchCleanupClassifier.ConfiguredBranch, Row(rows, "dev-qa-net8").ProtectedReason);
        Assert.True(Row(rows, "FXCPMGR/PI2026_3_3/1_x").CanDelete);
    }

    [Fact]
    public void A_branch_with_an_active_pull_request_is_locked()
    {
        var openPrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FXCPMGR/PI2026_3_3/1_x" };

        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(openPrs, "FXCPMGR/PI2026_3_3/1_x") }, Query());

        Assert.Equal(BranchCleanupClassifier.OpenPullRequest, rows[0].ProtectedReason);
        Assert.False(rows[0].CanDelete);
    }

    [Fact]
    public void When_open_pull_requests_could_not_be_read_every_branch_in_that_repository_is_locked()
    {
        var rows = BranchCleanupClassifier.BuildRows(
            new[] { Listing(openPrs: null, "FXCPMGR/PI2026_3_3/1_x", "feature/anything") }, Query());

        Assert.All(rows, r => Assert.Equal(BranchCleanupClassifier.PullRequestsUnknown, r.ProtectedReason));
    }

    // ─── Links and unattached team branches ───

    [Fact]
    public void A_team_branch_no_work_item_links_is_unattached()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1538999_x") }, Query());

        Assert.Equal(BranchLinkState.Unattached, rows[0].LinkState);
    }

    [Fact]
    public void A_branch_without_the_team_prefix_is_not_judged()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "users/someone/experiment") }, Query());

        Assert.Equal(BranchLinkState.NotChecked, rows[0].LinkState);
    }

    [Fact]
    public void Without_a_team_name_nothing_is_called_unattached()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1538999_x") }, Query(team: ""));

        Assert.Equal(BranchLinkState.NotChecked, rows[0].LinkState);
    }

    [Fact]
    public void A_link_from_a_sprint_work_item_marks_the_branch_linked_and_in_that_sprint()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "feature/other-name") }, Query());

        BranchCleanupClassifier.AttachLinks(rows, new[] { LinksFrom("1538999", "feature/other-name") }, fromSelectedSprint: true);

        Assert.Equal(BranchLinkState.Linked, rows[0].LinkState);
        Assert.True(rows[0].InSelectedSprint);
        Assert.Equal("1538999", Assert.Single(rows[0].LinkedBy).Id);
    }

    [Fact]
    public void A_link_found_through_the_name_marks_the_branch_linked_but_not_in_the_sprint()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1538999_x") }, Query());

        BranchCleanupClassifier.AttachLinks(rows, new[] { LinksFrom("1538999", "FXCPMGR/PI2026_3_3/1538999_x") }, fromSelectedSprint: false);

        Assert.Equal(BranchLinkState.Linked, rows[0].LinkState);
        Assert.False(rows[0].InSelectedSprint);
    }

    [Fact]
    public void Links_match_branches_whatever_the_case_and_each_work_item_is_listed_once()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1_x") }, Query());
        var link = LinksFrom("1", "fxcpmgr/pi2026_3_3/1_X");

        BranchCleanupClassifier.AttachLinks(rows, new[] { link, link }, fromSelectedSprint: false);

        Assert.Single(rows[0].LinkedBy);
    }

    [Fact]
    public void Links_to_branches_that_were_not_listed_are_ignored()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1_x") }, Query());

        BranchCleanupClassifier.AttachLinks(rows, new[] { LinksFrom("2", "FXCPMGR/PI2026_3_3/2_deleted") }, fromSelectedSprint: true);

        Assert.Empty(rows[0].LinkedBy);
        Assert.False(rows[0].InSelectedSprint);
    }

    [Fact]
    public void A_branch_named_after_a_selected_sprint_is_in_it_even_without_a_link()
    {
        var rows = BranchCleanupClassifier.BuildRows(
            new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1_x", "FXCPMGR/PI2026_5_2/2_y") },
            Query(sprints: new[] { @"CasepointARA\Iteration\PI-2026-3_3" }));

        Assert.True(Row(rows, "FXCPMGR/PI2026_3_3/1_x").InSelectedSprint);
        Assert.False(Row(rows, "FXCPMGR/PI2026_5_2/2_y").InSelectedSprint);
    }

    [Fact]
    public void Only_work_items_not_already_read_are_asked_for_and_each_only_once()
    {
        var rows = BranchCleanupClassifier.BuildRows(
            new[] { Listing(NoOpenPrs, "FXCPMGR/S/10_a", "FXCPMGR/S/10_b", "FXCPMGR/S/11_c", "FXCPMGR/S/12_d", "feature/fxcpmgr-no-id", "users/x/13_e") },
            Query());
        var sprintItems = new[] { LinksFrom("11") };

        var ids = BranchCleanupClassifier.UncheckedWorkItemIds(rows, sprintItems);

        Assert.Equal(new[] { 10, 12 }, ids.OrderBy(i => i));
    }

    [Fact]
    public void Rows_carry_the_creator_and_the_parsed_name()
    {
        var rows = BranchCleanupClassifier.BuildRows(new[] { Listing(NoOpenPrs, "FXCPMGR/PI2026_3_3/1538999_x") }, Query());

        Assert.Equal("Dipak Katariya", rows[0].Creator);
        Assert.Equal(1538999, rows[0].Parsed!.WorkItemId);
        Assert.Equal(BranchCleanupRow.KeyOf(RepoId.ToUpperInvariant(), "fxcpmgr/pi2026_3_3/1538999_X"), rows[0].Key);
    }

    // ─── What TFS answers ───

    [Fact]
    public void A_listed_ref_keeps_its_creator()
    {
        using var doc = JsonDocument.Parse("""{"name":"refs/heads/FXCPMGR/x","objectId":"abc123","creator":{"displayName":"Neel Patel"}}""");

        var parsed = GitRefListing.ParseRef(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("FXCPMGR/x", parsed.Name);
        Assert.Equal("Neel Patel", parsed.Creator);
    }

    [Fact]
    public void A_listed_ref_without_a_creator_or_without_an_object_id_is_handled()
    {
        using var noCreator = JsonDocument.Parse("""{"name":"refs/heads/a","objectId":"abc"}""");
        using var noObject = JsonDocument.Parse("""{"name":"refs/heads/a"}""");

        Assert.Equal("", GitRefListing.ParseRef(noCreator.RootElement)!.Creator);
        Assert.Null(GitRefListing.ParseRef(noObject.RootElement));
    }

    [Fact]
    public void Only_branches_tfs_reports_as_succeeded_count_as_deleted()
    {
        using var response = JsonDocument.Parse("""
            {"value":[
              {"name":"refs/heads/a","success":true,"updateStatus":"succeeded"},
              {"name":"refs/heads/b","success":false,"updateStatus":"staleOldObjectId"},
              {"name":"refs/heads/c","success":false,"updateStatus":"somethingNew","customMessage":"Blocked by the server"},
              {"name":"refs/heads/d","success":true,"updateStatus":"succeededNonExistentRef"}
            ]}
            """);
        var requested = new[] { Ref("a"), Ref("b"), Ref("c"), Ref("d"), Ref("e") };

        var outcomes = GitRefUpdate.Outcomes(Repo, requested, response.RootElement).ToDictionary(o => o.Branch);

        Assert.True(outcomes["a"].Deleted);
        Assert.False(outcomes["b"].Deleted);
        Assert.Contains("pushed to it since it was loaded", outcomes["b"].Message);
        Assert.False(outcomes["c"].Deleted);
        Assert.Contains("Blocked by the server", outcomes["c"].Message);
        Assert.True(outcomes["d"].Deleted);
        Assert.Contains("Already gone", outcomes["d"].Message);
        Assert.False(outcomes["e"].Deleted);
        Assert.Contains("did not report", outcomes["e"].Message);
    }

    [Fact]
    public void Progress_percent_is_bounded_and_safe_with_nothing_to_do()
    {
        Assert.Equal(0, new BranchCleanupProgress("x", 0, 0).Percent);
        Assert.Equal(50, new BranchCleanupProgress("x", 5, 10).Percent);
        Assert.Equal(100, new BranchCleanupProgress("x", 12, 10).Percent);
    }
}
