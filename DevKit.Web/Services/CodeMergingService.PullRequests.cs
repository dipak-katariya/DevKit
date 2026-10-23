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

    private const string ReplyWithoutRepository = "TFS sent it without its repository, so it could not be placed.";

    /// <summary>The pull requests prefetched per selected repository, and the repositories whose list failed.</summary>
    /// <param name="ByRepo">
    /// Keyed by every selected repository, a failed one included: the keys are what lets a link into a
    /// repository be read by id when its list did not cover it.
    /// </param>
    private sealed record PrefetchedPullRequests(
        Dictionary<string, List<MergingPullRequest>> ByRepo, List<DiscoveryWarning> Failures);

    /// <summary>One read of a repository's pull requests, or the failure that stopped it.</summary>
    private readonly record struct RepoPullRequestsRead(List<MergingPullRequest> PullRequests, DiscoveryWarning? Failure);

    /// <summary>A work item's linked pull requests that were found, and the reads that failed on the way.</summary>
    private sealed record LinkedPullRequests(List<MergingPullRequest> Found, List<DiscoveryWarning> Failures)
    {
        public static LinkedPullRequests Failed(DiscoveryWarning failure) =>
            new(new List<MergingPullRequest>(), new List<DiscoveryWarning> { failure });
    }

    /// <summary>One link resolved to its pull request, or the failure that stopped the read.</summary>
    private readonly record struct LinkRead(MergingPullRequest? PullRequest, DiscoveryWarning? Failure);

    /// <summary>
    /// Fetches every candidate pull request per repository once, up to <paramref name="top"/> per status.
    /// Matching then happens in memory, which is what makes the branch/title fallback affordable; the
    /// alternative is one API call per requirement per repository.
    /// </summary>
    private async Task<PrefetchedPullRequests> PrefetchPullRequestsAsync(IReadOnlyList<TfsRepo> repos, int top, CancellationToken ct)
    {
        var loaded = await Task.WhenAll(repos.Select(async repo =>
        {
            var perStatus = await Task.WhenAll(
                PullRequestStatuses.Select(status => FetchRepoPullRequestsAsync(repo, status, top, ct)));

            // Both of a repository's lists failing is one problem for the reader, so it is named once.
            var failure = perStatus.Select(read => read.Failure).FirstOrDefault(f => f is not null);
            return (repo.Id, Prs: perStatus.SelectMany(read => read.PullRequests).ToList(), Failure: failure);
        }));

        return new PrefetchedPullRequests(
            loaded.ToDictionary(x => x.Id, x => x.Prs, StringComparer.OrdinalIgnoreCase),
            loaded.Select(x => x.Failure).OfType<DiscoveryWarning>().ToList());
    }

    private async Task<RepoPullRequestsRead> FetchRepoPullRequestsAsync(
        TfsRepo repo, string status, int top, CancellationToken ct)
    {
        var url = _req.ProjectApi(repo.Project,
            $"git/repositories/{repo.Id}/pullrequests" +
            $"?searchCriteria.status={status}&$top={top}&api-version={_req.Ver}");
        try
        {
            var data = await _req.GetJsonAsync(url, ct);
            return new RepoPullRequestsRead(TfsJson.Values(data).Select(pr => ParsePullRequest(pr, repo)).ToList(), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to load {Status} pull requests for repo {Repo}", status, repo.Name);
            return new RepoPullRequestsRead(
                new List<MergingPullRequest>(),
                new DiscoveryWarning($"Pull request list of {repo.Name}", TfsErrors.Describe(ex)));
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
            CreationDate = TfsJson.Timestamp(pr, "creationDate"),
            ClosedDate = TfsJson.Timestamp(pr, "closedDate"),
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
        var linked = await FetchLinkedPullRequestsAsync(query.Project, row.WorkItemId, prsByRepo, ct);
        foreach (var pr in linked.Found)
        {
            Sort(pr);
        }

        if (query.MatchByBranchOrTitle)
        {
            var token = row.WorkItemId.ToString(CultureInfo.InvariantCulture);
            foreach (var pr in prsByRepo.Values.SelectMany(v => v).Where(pr => MentionsWorkItem(pr, token)))
            {
                Sort(pr);
            }
        }

        // Assigned, not accumulated: a row can be resolved again on a re-load.
        row.PullRequests = Ordered(onQa.Values);
        row.OffQaBranchPullRequests = Ordered(offQa.Values);
        row.DiscoveryGaps = linked.Failures;
    }

    /// <summary>
    /// Puts every failed read on the bundle for the page, and a failed pull-request list on the rows it
    /// could have cost. The rule for a list: with branch/title matching on, every row is held back, because
    /// any work item could have had a pull request there that only its branch or title tied to it. With links
    /// only, the list failing loses nothing: a linked pull request it did not cover is read on its own, and a
    /// failure there marks that work item's row.
    /// </summary>
    private static void RecordDiscoveryGaps(
        CodeMergingBundle bundle, IReadOnlyList<DiscoveryWarning> listFailures, bool matchByBranchOrTitle)
    {
        bundle.DiscoveryWarnings = listFailures.Concat(bundle.Requirements.SelectMany(r => r.DiscoveryGaps)).ToList();
        if (!matchByBranchOrTitle)
        {
            return;
        }
        foreach (var row in bundle.Requirements)
        {
            row.DiscoveryGaps.AddRange(listFailures);
        }
    }

    private static List<MergingPullRequest> Ordered(IEnumerable<MergingPullRequest> prs) => prs
        .OrderBy(pr => pr.FirstActivityDate ?? DateTime.MaxValue)
        .ThenBy(pr => pr.PullRequestId)
        .ToList();

    /// <summary>
    /// The pull requests the work item links to, in the selected repositories only (the keys of
    /// <paramref name="prsByRepo"/>). A work item often links pull requests in several repositories,
    /// and the sheet answers for the ones the user picked. A read that fails is handed back rather
    /// than dropped, so the row can say it may be missing pull requests.
    /// </summary>
    private async Task<LinkedPullRequests> FetchLinkedPullRequestsAsync(
        string project, int workItemId,
        IReadOnlyDictionary<string, List<MergingPullRequest>> prsByRepo, CancellationToken ct)
    {
        var url = _req.ProjectApi(project, $"wit/workitems/{workItemId}?$expand=relations&api-version={_req.Ver}");
        List<PullRequestArtifactRef> links;
        try
        {
            var data = await _req.GetJsonAsync(url, ct);
            links = SelectedPullRequestLinks(data, prsByRepo);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to read pull-request links for work item {Id}", workItemId);
            return LinkedPullRequests.Failed(new DiscoveryWarning($"Links of work item #{workItemId}", TfsErrors.Describe(ex)));
        }

        // The prefetch only covers each repository's most recent pull requests, so a link to an
        // older one misses. The work item named it explicitly, so fetch it by id rather than
        // dropping it. A link that carries no repository resolves to wherever the pull request
        // lives, which is checked against the selection once it is known.
        var resolved = await Task.WhenAll(links.Select(async link =>
            FindPrefetched(prsByRepo, link) is { } prefetched
                ? new LinkRead(prefetched, null)
                : await FetchPullRequestByIdAsync(project, workItemId, link, ct)));

        var found = resolved
            .Select(r => r.PullRequest)
            .OfType<MergingPullRequest>()
            .Where(pr => prsByRepo.ContainsKey(pr.RepositoryId))
            .ToList();
        return new LinkedPullRequests(found, resolved.Select(r => r.Failure).OfType<DiscoveryWarning>().ToList());
    }

    /// <summary>
    /// The work item's pull-request links. One into a repository that is not selected is left out: it
    /// could only be dropped after fetching it, at the cost of a request.
    /// </summary>
    private static List<PullRequestArtifactRef> SelectedPullRequestLinks(
        JsonElement workItem, IReadOnlyDictionary<string, List<MergingPullRequest>> prsByRepo)
    {
        if (!workItem.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array)
        {
            return new List<PullRequestArtifactRef>();
        }
        return rels.EnumerateArray()
            .Select(rel => PrArtifactLink.TryParse(TfsJson.Str(rel, "url")))
            .Where(l => l is not null)
            .Select(l => l!.Value)
            .Where(l => l.RepositoryId is null || prsByRepo.ContainsKey(l.RepositoryId))
            .ToList();
    }

    /// <summary>
    /// Reads one pull request the prefetch never covered: an older one in a selected repository, or
    /// one linked without its repository. The repository comes back on the response, so the caller
    /// can tell whether it is one of the selected ones; a reply without it cannot be placed, so it
    /// counts as a failed read rather than as no pull request.
    /// </summary>
    private async Task<LinkRead> FetchPullRequestByIdAsync(
        string project, int workItemId, PullRequestArtifactRef link, CancellationToken ct)
    {
        var path = link.RepositoryId is null
            ? $"git/pullrequests/{link.PullRequestId}"
            : $"git/repositories/{link.RepositoryId}/pullrequests/{link.PullRequestId}";
        var subject = $"Pull request !{link.PullRequestId} linked from #{workItemId}";
        try
        {
            var data = await _req.GetJsonAsync(_req.ProjectApi(project, $"{path}?api-version={_req.Ver}"), ct);

            if (!data.TryGetProperty("repository", out var repoEl) || repoEl.ValueKind != JsonValueKind.Object)
            {
                _logger.LogError("TFS sent pull request {PrId}, linked from work item {Id}, without its repository",
                    link.PullRequestId, workItemId);
                return new LinkRead(null, new DiscoveryWarning(subject, ReplyWithoutRepository));
            }

            var repo = new TfsRepo
            {
                Id = TfsJson.Str(repoEl, "id"),
                Name = TfsJson.Str(repoEl, "name"),
                Project = repoEl.TryGetProperty("project", out var projEl) && projEl.ValueKind == JsonValueKind.Object
                    ? TfsJson.Str(projEl, "name")
                    : project
            };
            return new LinkRead(ParsePullRequest(data, repo), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to read pull request {PrId} linked from work item {Id}", link.PullRequestId, workItemId);
            return new LinkRead(null, new DiscoveryWarning(subject, TfsErrors.Describe(ex)));
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
