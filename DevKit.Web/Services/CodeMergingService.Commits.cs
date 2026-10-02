using System.Globalization;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Commits behind each pull request, read eagerly for the first rows and lazily for the rest.</summary>
public partial class CodeMergingService
{
    // ═══ COMMITS ═══

    /// <summary>
    /// Reads commits for the rows the user sees first. The rest are left for
    /// <see cref="LoadRowCommitsAsync"/> when a row is opened, so time to first paint does not
    /// scale with the size of the sprint.
    /// </summary>
    private async Task LoadCommitsAsync(CodeMergingBundle bundle, int eagerRows, CancellationToken ct)
    {
        var rows = bundle.Requirements.Take(Math.Max(0, eagerRows)).ToList();

        // A pull request can be linked from two work items; the same instance appears in both
        // rows, so loading it once keeps the call count down and the two rows consistent.
        // Off-QA pull requests are included: "All PRs" reveals them without a reload, and a
        // revealed row showing no commits would read as the tool having lost them.
        var distinct = rows.SelectMany(r => r.AllPullRequests).Distinct().ToList();
        await Task.WhenAll(distinct.Select(async pr => pr.Commits = await FetchCommitsAsync(pr, ct)));

        foreach (var row in rows) row.CommitsLoaded = true;
    }

    /// <summary>
    /// Fills in one deferred row's commits. Safe to call on a row already loaded — it returns
    /// without a request — so the caller does not have to track which rows are outstanding.
    /// </summary>
    public async Task LoadRowCommitsAsync(RequirementMergingRow row, CancellationToken ct = default)
    {
        if (row.CommitsLoaded) return;
        try
        {
            var pending = row.AllPullRequests.Where(p => p.Commits.Count == 0).Distinct().ToList();
            await Task.WhenAll(pending.Select(async pr => pr.Commits = await FetchCommitsAsync(pr, ct)));
            row.CommitsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load commits for work item {Id}", row.WorkItemId);
        }
    }

    private async Task<List<MergingCommit>> FetchCommitsAsync(MergingPullRequest pr, CancellationToken ct)
    {
        var url = _req.ProjectApi(pr.Project,
            $"git/repositories/{pr.RepositoryId}/pullRequests/{pr.PullRequestId}/commits?api-version={_req.Ver}");
        try
        {
            var data = await TfsThrottle.RunAsync(() => _req.GetJsonAsync(url, ct), ct);
            return TfsJson.Values(data)
                .Select(c => ParseCommit(c, pr))
                .OrderBy(c => c.Date ?? DateTime.MaxValue)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load commits for PR {PrId} in {Repo}", pr.PullRequestId, pr.RepositoryName);
            return new List<MergingCommit>();
        }
    }

    private MergingCommit ParseCommit(JsonElement c, MergingPullRequest pr)
    {
        var sha = TfsJson.Str(c, "commitId");
        var author = TfsJson.CommitPerson(c, "author");
        var committer = TfsJson.CommitPerson(c, "committer");
        return new MergingCommit
        {
            CommitId = sha,
            Message = TfsJson.Str(c, "comment"),
            Author = string.IsNullOrEmpty(author.Name) ? committer.Name : author.Name,
            AuthorEmail = TfsJson.CommitEmail(c, "author"),
            AuthorDate = author.Date,
            Date = author.Date ?? committer.Date,
            Url = $"{_req.Url}/{Uri.EscapeDataString(pr.Project)}/_git/{Uri.EscapeDataString(pr.RepositoryName)}/commit/{sha}"
        };
    }

}
