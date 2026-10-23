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
    /// scale with the size of the sprint. A row whose read failed is left unloaded like them.
    /// </summary>
    private async Task LoadCommitsAsync(CodeMergingBundle bundle, int eagerRows, CancellationToken ct)
    {
        var rows = bundle.Requirements.Take(Math.Max(0, eagerRows)).ToList();

        // A pull request can be linked from two work items; the same instance appears in both
        // rows, so loading it once keeps the call count down and the two rows consistent.
        // Off-QA pull requests are included: "All PRs" reveals them without a reload, and a
        // revealed row showing no commits would read as the tool having lost them.
        var distinct = rows.SelectMany(r => r.AllPullRequests()).Distinct().ToList();
        await Task.WhenAll(distinct.Select(pr => ReadCommitsAsync(pr, ct)));

        foreach (var row in rows)
        {
            row.CommitsLoaded = row.CommitsError is null;
        }
    }

    /// <summary>
    /// Fills in one deferred row's commits, reading again any that failed before. Safe to call on a row
    /// already loaded (it returns without a request), so the caller does not have to track which rows are
    /// outstanding. A read that fails again leaves the row unloaded with the reason on its pull request. A
    /// cancellation the caller asked for ends it with the exception and leaves the row unloaded.
    /// </summary>
    public async Task LoadRowCommitsAsync(RequirementMergingRow row, CancellationToken ct = default)
    {
        if (row.CommitsLoaded)
        {
            return;
        }
        var pending = row.AllPullRequests().Where(p => p.Commits.Count == 0 || p.CommitsError is not null).Distinct().ToList();
        await Task.WhenAll(pending.Select(pr => ReadCommitsAsync(pr, ct)));
        row.CommitsLoaded = row.CommitsError is null;
    }

    /// <summary>
    /// Reads one pull request's commits onto it. A failure is logged and kept on the pull request in words
    /// for the page instead of reading as no commits; a later read that succeeds clears it.
    /// </summary>
    private async Task ReadCommitsAsync(MergingPullRequest pr, CancellationToken ct)
    {
        try
        {
            pr.Commits = await FetchCommitsAsync(pr, ct);
            pr.CommitsError = null;
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex, "Failed to load commits for PR {PrId} in {Repo}", pr.PullRequestId, pr.RepositoryName);
            pr.CommitsError = TfsErrors.Describe(ex);
        }
    }

    private async Task<List<MergingCommit>> FetchCommitsAsync(MergingPullRequest pr, CancellationToken ct)
    {
        var url = _req.ProjectApi(pr.Project,
            $"git/repositories/{pr.RepositoryId}/pullRequests/{pr.PullRequestId}/commits?api-version={_req.Ver}");
        var data = await _req.GetJsonAsync(url, ct);
        return TfsJson.Values(data)
            .Select(c => ParseCommit(c, pr))
            .OrderBy(c => c.Date ?? DateTime.MaxValue)
            .ToList();
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
