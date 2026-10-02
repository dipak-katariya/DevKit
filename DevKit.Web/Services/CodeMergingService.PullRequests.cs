using System.Globalization;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Finding the pull requests that belong to a requirement: the work item's own links first,
/// then a match against the pull requests prefetched per repository.
/// </summary>
public partial class CodeMergingService
{
    // ═══ PULL REQUEST DISCOVERY ═══

    /// <summary>
    /// Fetches every candidate pull request per repository once. Matching then happens in
    /// memory, which is what makes the branch/title fallback affordable — the alternative
    /// is one API call per requirement per repository.
    /// </summary>
    private async Task<Dictionary<string, List<MergingPullRequest>>> PrefetchPullRequestsAsync(
        IReadOnlyList<TfsRepo> repos, CancellationToken ct)
    {
        var loaded = await Task.WhenAll(repos.Select(async repo =>
        {
            var perStatus = await Task.WhenAll(
                PullRequestStatuses.Select(status => FetchRepoPullRequestsAsync(repo, status, PullRequestsPerRepo, ct)));
            return (repo.Id, Prs: perStatus.SelectMany(x => x).ToList());
        }));

        return loaded.ToDictionary(x => x.Id, x => x.Prs, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<MergingPullRequest>> FetchRepoPullRequestsAsync(
        TfsRepo repo, string status, int top, CancellationToken ct)
    {
        var url = _req.ProjectApi(repo.Project,
            $"git/repositories/{repo.Id}/pullrequests" +
            $"?searchCriteria.status={status}&$top={top}&api-version={_req.Ver}");
        try
        {
            var data = await TfsThrottle.RunAsync(() => _req.GetJsonAsync(url, ct), ct);
            return TfsJson.Values(data).Select(pr => ParsePullRequest(pr, repo)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load {Status} pull requests for repo {Repo}", status, repo.Name);
            return new List<MergingPullRequest>();
        }
    }

    private MergingPullRequest ParsePullRequest(JsonElement pr, TfsRepo repo)
    {
        var id = TfsJson.Int(pr, "pullRequestId");
        var source = GitRefName.Strip(TfsJson.Str(pr, "sourceRefName"));
        var target = GitRefName.Strip(TfsJson.Str(pr, "targetRefName"));
        return new MergingPullRequest
        {
            PullRequestId = id,
            Title = TfsJson.Str(pr, "title"),
            Project = repo.Project,
            RepositoryId = repo.Id,
            RepositoryName = repo.Name,
            SourceBranch = source,
            TargetBranch = target,
            SourceBranchUrl = _req.BranchWebUrl(repo.Project, repo.Name, source),
            TargetBranchUrl = _req.BranchWebUrl(repo.Project, repo.Name, target),
            Status = TfsJson.Str(pr, "status"),
            CreatedBy = TfsJson.Person(pr, "createdBy"),
            CreationDate = TfsJson.Date(pr, "creationDate"),
            ClosedDate = TfsJson.Date(pr, "closedDate"),
            LastMergeCommitId = NestedCommitId(pr, "lastMergeCommit"),
            LastMergeSourceCommitId = NestedCommitId(pr, "lastMergeSourceCommit"),
            LastMergeTargetCommitId = NestedCommitId(pr, "lastMergeTargetCommit"),
            Url = $"{_req.Url}/{Uri.EscapeDataString(repo.Project)}/_git/{Uri.EscapeDataString(repo.Name)}/pullrequest/{id}"
        };
    }

    private static string NestedCommitId(JsonElement pr, string prop) =>
        pr.TryGetProperty(prop, out var c) && c.ValueKind == JsonValueKind.Object ? TfsJson.Str(c, "commitId") : "";

    /// <summary>
    /// Two-pass discovery, deduplicated by pull-request id: the work item's own artifact
    /// links first (accurate, because someone explicitly linked the PR), then an id match
    /// inside the source branch name or PR title for the very common case of a developer
    /// never linking it.
    /// </summary>
    private async Task ResolvePullRequestsAsync(
        CodeMergingQuery query, RequirementMergingRow row,
        IReadOnlyDictionary<string, List<MergingPullRequest>> prsByRepo, CancellationToken ct)
    {
        var onQa = new Dictionary<int, MergingPullRequest>();
        var offQa = new Dictionary<int, MergingPullRequest>();

        void Sort(MergingPullRequest pr)
        {
            if (onQa.ContainsKey(pr.PullRequestId) || offQa.ContainsKey(pr.PullRequestId)) return;
            if (PassesRepoTargetFilter(pr, query.QaBranchByRepoId)) onQa[pr.PullRequestId] = pr;
            else offQa[pr.PullRequestId] = pr;
        }

        // A hand-attached link used to be shown whatever it targeted. That let intermediate
        // feature-branch pull requests onto a sheet whose whole premise is "these targeted the
        // QA branch", so the configured branch now decides for linked and matched pull requests
        // alike. The ones held back are kept aside rather than dropped, so "All PRs" can show
        // them for cross-checking without a reload.
        foreach (var pr in await FetchLinkedPullRequestsAsync(query.Project, row.WorkItemId, prsByRepo, ct))
            Sort(pr);

        if (query.MatchByBranchOrTitle)
        {
            var token = row.WorkItemId.ToString(CultureInfo.InvariantCulture);
            foreach (var pr in prsByRepo.Values.SelectMany(v => v))
                if (MentionsWorkItem(pr, token)) Sort(pr);
        }

        // Assigned, not accumulated: a row can be resolved again on a re-load.
        row.PullRequests = Ordered(onQa.Values);
        row.OffQaBranchPullRequests = Ordered(offQa.Values);
    }

    private static List<MergingPullRequest> Ordered(IEnumerable<MergingPullRequest> prs) => prs
        .OrderBy(pr => pr.FirstActivityDate ?? DateTime.MaxValue)
        .ThenBy(pr => pr.PullRequestId)
        .ToList();

    /// <summary>
    /// The pull requests the work item links to, in the selected repositories only — the keys of
    /// <paramref name="prsByRepo"/>. A work item often links pull requests in several repositories,
    /// and the sheet answers for the ones the user picked.
    /// </summary>
    private async Task<List<MergingPullRequest>> FetchLinkedPullRequestsAsync(
        string project, int workItemId,
        IReadOnlyDictionary<string, List<MergingPullRequest>> prsByRepo, CancellationToken ct)
    {
        var url = _req.ProjectApi(project, $"wit/workitems/{workItemId}?$expand=relations&api-version={_req.Ver}");
        var matches = new List<MergingPullRequest>();
        try
        {
            var data = await TfsThrottle.RunAsync(() => _req.GetJsonAsync(url, ct), ct);
            if (!data.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array)
            {
                return matches;
            }

            // A link into a repository that is not selected is not even fetched: it could only be
            // dropped afterwards, at the cost of a request.
            var links = rels.EnumerateArray()
                .Select(rel => PrArtifactLink.TryParse(TfsJson.Str(rel, "url")))
                .Where(l => l is not null)
                .Select(l => l!.Value)
                .Where(l => l.RepositoryId is null || prsByRepo.ContainsKey(l.RepositoryId))
                .ToList();

            // The prefetch only covers each repository's most recent pull requests, so a link to an
            // older one misses. The work item named it explicitly, so fetch it by id rather than
            // dropping it. A link that carries no repository resolves to wherever the pull request
            // lives, which is checked against the selection once it is known.
            var resolved = await Task.WhenAll(links.Select(async link =>
                FindPrefetched(prsByRepo, link) ?? await FetchPullRequestByIdAsync(project, link, ct)));

            matches.AddRange(resolved.OfType<MergingPullRequest>().Where(pr => prsByRepo.ContainsKey(pr.RepositoryId)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read pull-request links for work item {Id}", workItemId);
        }
        return matches;
    }

    /// <summary>
    /// Reads one pull request the prefetch never covered — an older one in a selected repository, or
    /// one linked without its repository. The repository comes back on the response, so the caller
    /// can tell whether it is one of the selected ones.
    /// </summary>
    private async Task<MergingPullRequest?> FetchPullRequestByIdAsync(
        string project, PullRequestArtifactRef link, CancellationToken ct)
    {
        var path = link.RepositoryId is null
            ? $"git/pullrequests/{link.PullRequestId}"
            : $"git/repositories/{link.RepositoryId}/pullrequests/{link.PullRequestId}";
        try
        {
            var data = await TfsThrottle.RunAsync(
                () => _req.GetJsonAsync(_req.ProjectApi(project, $"{path}?api-version={_req.Ver}"), ct), ct);

            if (!data.TryGetProperty("repository", out var repoEl) || repoEl.ValueKind != JsonValueKind.Object)
                return null;

            var repo = new TfsRepo
            {
                Id = TfsJson.Str(repoEl, "id"),
                Name = TfsJson.Str(repoEl, "name"),
                Project = repoEl.TryGetProperty("project", out var projEl) && projEl.ValueKind == JsonValueKind.Object
                    ? TfsJson.Str(projEl, "name")
                    : project
            };
            return ParsePullRequest(data, repo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read linked pull request {PrId}", link.PullRequestId);
            return null;
        }
    }

    /// <summary>
    /// Resolves an artifact link against the pre-fetched pull requests. When the link
    /// carried no repository GUID, every repository is probed for that pull-request id.
    /// </summary>
    private static MergingPullRequest? FindPrefetched(
        IReadOnlyDictionary<string, List<MergingPullRequest>> prsByRepo, PullRequestArtifactRef link)
    {
        if (link.RepositoryId != null && prsByRepo.TryGetValue(link.RepositoryId, out var scoped))
            return scoped.FirstOrDefault(p => p.PullRequestId == link.PullRequestId);

        return prsByRepo.Values.SelectMany(v => v).FirstOrDefault(p => p.PullRequestId == link.PullRequestId);
    }

    /// <summary>
    /// A standalone run of digits the length of a work item id. Deliberately generic: this
    /// runs where no candidate id list exists, unlike <see cref="MentionsWorkItem"/>, which
    /// matches one known id. Bounded by non-digits so a longer number cannot match.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex WorkItemIdShape =
        new(@"(?<![0-9])[0-9]{6,8}(?![0-9])", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool ReferencesWorkItemId(MergingPullRequest pr) =>
        WorkItemIdShape.IsMatch(pr.SourceBranch ?? "") || WorkItemIdShape.IsMatch(pr.Title ?? "");

    private static bool MentionsWorkItem(MergingPullRequest pr, string id) =>
        ContainsIdToken(pr.SourceBranch, id) || ContainsIdToken(pr.Title, id);

    /// <summary>
    /// Substring match bounded by non-digits, so work item 1234 does not match branch
    /// "feature/12345-thing". Catches "feature/1234-fix" and "1234: fix the thing".
    /// </summary>
    private static bool ContainsIdToken(string text, string id)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var i = text.IndexOf(id, StringComparison.Ordinal); i >= 0; i = text.IndexOf(id, i + 1, StringComparison.Ordinal))
        {
            var after = i + id.Length;
            var leftOk = i == 0 || !char.IsDigit(text[i - 1]);
            var rightOk = after >= text.Length || !char.IsDigit(text[after]);
            if (leftOk && rightOk) return true;
        }
        return false;
    }

    /// <summary>
    /// Drops pull requests that did not target their repository's QA branch. Keyed by
    /// repository id rather than name because names repeat across projects, so a name key
    /// would apply one repository's branch to another's pull requests. A repository absent
    /// from the map, or mapped to blank, keeps all of its pull requests.
    /// </summary>
    private static bool PassesRepoTargetFilter(MergingPullRequest pr, IReadOnlyDictionary<string, string> qaBranchByRepoId)
    {
        if (qaBranchByRepoId.Count == 0) return true;
        if (!qaBranchByRepoId.TryGetValue(pr.RepositoryId, out var expected) || string.IsNullOrWhiteSpace(expected)) return true;
        return GitRefName.Matches(pr.TargetBranch, expected);
    }

}
