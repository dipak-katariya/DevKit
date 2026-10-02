using System.Collections.Concurrent;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The code comparison end to end, against an in-memory git server that answers the same endpoints TFS
/// does — refs, commit diffs, commits, trees and blobs — from real file trees. Besides the verdicts, the
/// tests pin down what made the old check unreliable or slow: per-scope limits, one failing pull request
/// not stopping the rest, Stop restoring earlier results, and files settled without being downloaded.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed partial class CodeCompareServiceTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "CasepointARA";
    private const string RepoId = "acef7bab-9ec9-4469-9abb-24adbc320d75";
    private const string QaBranch = "dev-qa";
    private const string Release = "release/26.4";

    private const string ExportBefore =
        "public class Export\n{\n    public int Count(int[] items)\n    {\n        var total = 0;\n" +
        "        foreach (var item in items)\n        {\n            total += item;\n        }\n        return total;\n    }\n}\n";

    private const string ExportAfter =
        "public class Export\n{\n    public int Count(int[] items)\n    {\n        if (items is null)\n        {\n" +
        "            throw new ArgumentNullException(nameof(items));\n        }\n        var total = 0;\n" +
        "        foreach (var item in items)\n        {\n            total += item;\n        }\n        return total;\n    }\n}\n";

    private const string NewFile = "public static class Totals\n{\n    public static int Sum(int a, int b) => a + b;\n}\n";

    private readonly FakeGitServer _git = new();
    private readonly HttpClient _http;
    private readonly CodeCompareService _service;

    public CodeCompareServiceTests()
    {
        _http = new HttpClient(_git, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = CollectionUrl;
        _service = new CodeCompareService(_http, settings, NullLogger<CodeCompareService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _git.Dispose();
    }

    [Fact]
    public async Task Code_fully_on_the_branch_is_present_without_downloading_a_file()
    {
        var (pr, after) = MergedPr(1001);
        _git.Branch(Release, _git.Commit(With(after, "/docs/notes.md", "release notes")));

        var summary = await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Present, pr.CodeCheck!.Verdict);
        Assert.Equal(1, summary.Present);
        Assert.Equal(2, pr.CodeCheck.Files.Count);
        Assert.DoesNotContain(_git.Requests, r => r.Contains("/blobs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Code_not_moved_yet_is_missing_file_by_file()
    {
        var (pr, _) = MergedPr(1002);
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", ExportBefore))));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        var check = pr.CodeCheck!;
        Assert.Equal(CodeVerdict.Missing, check.Verdict);
        Assert.All(check.Files, f => Assert.Equal(FileCodeStatus.Missing, f.Status));
        Assert.Contains(check.Files, f => f.Path == "/src/Totals.cs" && f.Reason.Contains("not on the branch"));
    }

    [Fact]
    public async Task Code_lost_while_resolving_a_conflict_is_caught_with_the_lines()
    {
        var (pr, _) = MergedPr(1003);
        var lost = ExportAfter.Replace("            throw new ArgumentNullException(nameof(items));\n", "");
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", lost), ("/src/Totals.cs", NewFile))));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Partial, pr.CodeCheck!.Verdict);
        var export = Assert.Single(pr.CodeCheck.Files, f => f.Path == "/src/Export.cs");
        Assert.Equal(FileCodeStatus.Partial, export.Status);
        var hunk = Assert.Single(export.Hunks);
        Assert.Contains(hunk.Lines, l => l.Kind == CodeLineKind.Added && l.InDestination == false && l.Text.Contains("throw"));
    }

    [Fact]
    public async Task Code_is_found_when_the_branch_has_other_changes_in_the_same_file()
    {
        var (pr, _) = MergedPr(1004);
        var releaseExport = ExportAfter.Replace("public class Export", "// release copy\npublic class Export");
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", releaseExport), ("/src/Totals.cs", NewFile))));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Present, pr.CodeCheck!.Verdict);
        Assert.Contains(_git.Requests, r => r.Contains("/blobs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ids_missing_from_the_diff_are_found_through_the_trees()
    {
        _git.OmitOriginalIds = true;
        var (pr, after) = MergedPr(1005);
        _git.Branch(Release, _git.Commit(after));

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Present, pr.CodeCheck!.Verdict);
    }

    [Fact]
    public async Task Each_tree_is_read_once_however_many_pull_requests_walk_it()
    {
        var (first, after) = MergedPr(1006);
        var (second, _) = MergedPr(1007);
        _git.Branch(Release, _git.Commit(after));

        await CompareAsync(CodeCompareScope.Sprint, first, second);

        var treeReads = _git.Requests.Where(r => r.Contains("/trees/", StringComparison.Ordinal))
            .GroupBy(r => r.Split('?')[0]);
        Assert.All(treeReads, g => Assert.Single(g));
    }

    [Fact]
    public async Task A_pull_request_too_big_for_the_sprint_run_points_to_its_own_check_which_handles_it()
    {
        var before = Enumerable.Range(0, 520).ToDictionary(i => $"/gen/File{i}.cs", i => $"// v1 {i}\n");
        var after = before.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("v1", "v2"));
        var baseCommit = _git.Commit(before);
        var merge = _git.Commit(after, baseCommit);
        _git.Branch(QaBranch, merge);
        _git.Branch(Release, _git.Commit(after));
        var pr = Pr(1008, baseCommit, merge);

        await CompareAsync(CodeCompareScope.Sprint, pr);
        Assert.Equal(CodeVerdict.TooLarge, pr.CodeCheck!.Verdict);
        Assert.True(pr.CodeCheck.NeedsAttention);
        Assert.Contains("Check code on this pull request", pr.CodeCheck.Summary);

        await CompareAsync(CodeCompareScope.PullRequest, pr);
        Assert.Equal(CodeVerdict.Present, pr.CodeCheck!.Verdict);
        Assert.Equal(520, pr.CodeCheck.Files.Count);
    }

    [Fact]
    public async Task Pull_requests_that_cannot_be_compared_say_why()
    {
        var (merged, after) = MergedPr(1011);
        _git.Branch(Release, _git.Commit(after));
        var active = new MergingPullRequest { PullRequestId = 1012, Project = Project, RepositoryId = RepoId, Status = "active" };
        var otherRepo = new MergingPullRequest
        {
            PullRequestId = 1013, Project = Project, RepositoryId = "another-repo", RepositoryName = "Other",
            Status = "completed", LastMergeCommitId = merged.LastMergeCommitId
        };

        var summary = await CompareAsync(CodeCompareScope.Sprint, merged, active, otherRepo);

        Assert.Equal(CodeVerdict.Skipped, active.CodeCheck!.Verdict);
        Assert.Contains("Not completed", active.CodeCheck.Summary);
        Assert.Equal(CodeVerdict.Skipped, otherRepo.CodeCheck!.Verdict);
        Assert.Contains("No merging branch", otherRepo.CodeCheck.Summary);
        Assert.Equal(2, summary.Skipped);
    }

    [Fact]
    public async Task Progress_is_reported_through_to_the_end()
    {
        var (first, after) = MergedPr(1016);
        var (second, _) = MergedPr(1017);
        _git.Branch(Release, _git.Commit(after));
        var progress = new Recorder();

        await _service.CompareAsync(
            new CodeCompareRequest(new[] { first, second }, Branches(Release), CodeCompareScope.Sprint), progress);

        var last = progress.Reports.Last();
        Assert.Equal(2, last.PullRequestsDone);
        Assert.Equal(2, last.PullRequestsTotal);
        Assert.Equal(100, last.Percent);
        Assert.Equal(4, last.FilesKnown);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_flagged_without_being_downloaded()
    {
        var huge = "// generated\n" + new string('x', 64);
        _git.ReportSize(huge + "changed", 3L * 1024 * 1024);
        var baseCommit = _git.Commit(Files(("/data/Big.cs", huge)));
        var merge = _git.Commit(Files(("/data/Big.cs", huge + "changed")), baseCommit);
        _git.Branch(QaBranch, merge);
        _git.Branch(Release, _git.Commit(Files(("/data/Big.cs", huge + "different"))));
        var pr = Pr(1018, baseCommit, merge);

        await CompareAsync(CodeCompareScope.Sprint, pr);

        var file = Assert.Single(pr.CodeCheck!.Files);
        Assert.Equal(FileCodeStatus.Unverified, file.Status);
        Assert.Contains("Larger than 2 MB", file.Reason);
        Assert.DoesNotContain(_git.Requests, r => r.Contains("/blobs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_binary_file_that_differs_is_left_for_a_manual_check()
    {
        var baseCommit = _git.CommitBytes(new Dictionary<string, byte[]> { ["/img/logo.png"] = new byte[] { 0x89, 0x50, 0x00, 0x01 } });
        var merge = _git.CommitBytes(new Dictionary<string, byte[]> { ["/img/logo.png"] = new byte[] { 0x89, 0x50, 0x00, 0x02 } }, baseCommit);
        _git.Branch(QaBranch, merge);
        _git.Branch(Release, _git.CommitBytes(new Dictionary<string, byte[]> { ["/img/logo.png"] = new byte[] { 0x89, 0x50, 0x00, 0x03 } }));
        var pr = Pr(1019, baseCommit, merge);

        await CompareAsync(CodeCompareScope.Sprint, pr);

        Assert.Equal(CodeVerdict.Unverified, pr.CodeCheck!.Verdict);
        Assert.Contains("binary", Assert.Single(pr.CodeCheck.Files).Reason);
    }

    [Fact]
    public async Task A_rename_with_the_old_file_still_on_the_branch_is_only_partly_merged()
    {
        var baseCommit = _git.Commit(Files(("/src/Old.cs", ExportBefore)));
        var merge = _git.Commit(Files(("/src/New.cs", ExportBefore)), baseCommit);
        _git.Renames[(baseCommit, merge)] = ("/src/Old.cs", "/src/New.cs");
        _git.Branch(QaBranch, merge);
        _git.Branch(Release, _git.Commit(Files(("/src/Old.cs", ExportBefore), ("/src/New.cs", ExportBefore))));
        var pr = Pr(1020, baseCommit, merge);

        await CompareAsync(CodeCompareScope.Sprint, pr);

        var file = Assert.Single(pr.CodeCheck!.Files);
        Assert.Equal(FileChangeKind.Rename, file.Change);
        Assert.Equal(FileCodeStatus.Partial, file.Status);
        Assert.Contains("/src/Old.cs is still on the branch", file.Reason);
    }

    [Fact]
    public async Task The_lines_of_a_file_settled_by_object_id_can_be_read_on_demand()
    {
        var (pr, _) = MergedPr(1021);
        _git.Branch(Release, _git.Commit(Files(("/src/Export.cs", ExportBefore), ("/src/Totals.cs", NewFile))));
        await CompareAsync(CodeCompareScope.Sprint, pr);
        var export = Assert.Single(pr.CodeCheck!.Files, f => f.Path == "/src/Export.cs");
        Assert.True(export.CanExplain);

        var detailed = await _service.ExplainAsync(pr, export);

        Assert.Equal(FileCodeStatus.Missing, detailed.Status);
        var hunk = Assert.Single(detailed.Hunks);
        Assert.Equal(HunkStatus.Missing, hunk.Status);
        Assert.False(detailed.CanExplain);
    }

    /// <summary>A completed pull request on QA that changed Export.cs and added Totals.cs.</summary>
    private (MergingPullRequest Pr, Dictionary<string, string> After) MergedPr(int id)
    {
        var before = Files(("/src/Export.cs", ExportBefore));
        var after = Files(("/src/Export.cs", ExportAfter), ("/src/Totals.cs", NewFile));
        var baseCommit = _git.Commit(before);
        var merge = _git.Commit(after, baseCommit);
        _git.Branch(QaBranch, merge);
        return (Pr(id, baseCommit, merge), after);
    }

    private static MergingPullRequest Pr(int id, string baseCommit, string mergeCommit) => new()
    {
        PullRequestId = id,
        Project = Project,
        RepositoryId = RepoId,
        RepositoryName = "CasepointARA",
        TargetBranch = QaBranch,
        Status = "completed",
        LastMergeCommitId = mergeCommit,
        LastMergeTargetCommitId = baseCommit
    };

    private Task<CodeCompareRunSummary> CompareAsync(CodeCompareScope scope, params MergingPullRequest[] prs) =>
        _service.CompareAsync(new CodeCompareRequest(prs, Branches(Release), scope));

    private static Dictionary<string, string> Branches(string merging) =>
        new(StringComparer.OrdinalIgnoreCase) { [RepoId] = merging };

    private static Dictionary<string, string> Files(params (string Path, string Content)[] files) =>
        files.ToDictionary(f => f.Path, f => f.Content);

    private static Dictionary<string, string> With(Dictionary<string, string> files, string path, string content) =>
        new(files) { [path] = content };

    private sealed class Recorder : IProgress<CodeCompareProgress>
    {
        public ConcurrentQueue<CodeCompareProgress> Reports { get; } = new();

        public void Report(CodeCompareProgress value) => Reports.Enqueue(value);
    }
}
