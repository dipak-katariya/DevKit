using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Read-only discovery for the Code Merging Sheet: which work items are in the sprint,
/// which pull requests belong to them, and which commits those pull requests carry.
/// Nothing here writes to TFS. Failures become <see cref="CodeMergingBundle.ErrorMessage"/>, worded by
/// <see cref="TfsErrors.Describe"/>, or <see cref="CodeMergingBundle.DiscoveryWarnings"/> for a read that
/// only cost some work items or pull requests, rather than exceptions, so a partially-failing load still renders. A
/// cancellation the caller asked for is not a failure: it ends the load with the exception.
/// Each of those three stages lives in its own partial: .WorkItems, .PullRequests and .Commits.
/// </summary>
public partial class CodeMergingService
{
    private const int WorkItemBatchSize = 200;
    private const int PullRequestsPerRepo = 500;

    /// <summary>
    /// The unlinked search has its own, larger page: it runs on demand rather than on every
    /// sheet load, so raising it costs nothing until someone asks for it.
    /// </summary>
    private const int UnlinkedPrsPerRepo = 1000;

    /// <summary>
    /// Each candidate costs a request to read its work-item links, so the check is bounded and
    /// the caller is told when it bit rather than silently returning a partial answer.
    /// </summary>
    private const int MaxUnlinkedPrChecks = 1000;
    private const int MaxRequirements = 1000;
    private const string DeliverableTag = "Deliverable";

    private const string WorkItemFields =
        "System.Id,System.Title,System.State,System.AssignedTo,System.WorkItemType,System.Tags";

    private const string UnreadWorkItemReason =
        "TFS did not return it: deleted, moved, or this token cannot read it. It is not on the sheet.";

    private static readonly string[] TaggedWorkItemTypes = { "Requirement", "User Story", "Product Backlog Item" };
    private static readonly string[] AlwaysIncludedTypes = { "Bug", "Change Request" };
    private static readonly string[] PullRequestStatuses = { "completed", "active" };

    private readonly TfsRequestClient _req;
    private readonly ILogger<CodeMergingService> _logger;

    public CodeMergingService(TfsRequestClient req, ILogger<CodeMergingService> logger)
    {
        _req = req;
        _logger = logger;
    }

    /// <summary>Over a client of the caller's own, as tests use it; DI only sees the public constructor.</summary>
    internal CodeMergingService(HttpClient http, SettingsService settings, ILogger<CodeMergingService> logger)
        : this(new TfsRequestClient(http, settings), logger)
    {
    }

    public async Task<CodeMergingBundle> GetMergingDataAsync(CodeMergingQuery query, CancellationToken ct = default)
    {
        var bundle = new CodeMergingBundle();
        try
        {
            var ids = await FetchWorkItemIdsAsync(query, ct);
            if (ids.Count > MaxRequirements)
            {
                bundle.TruncationNotice =
                    $"{ids.Count} work items matched. Showing the first {MaxRequirements}. Narrow the area or sprint to see the rest.";
                ids = ids.Take(MaxRequirements).ToList();
            }
            if (ids.Count == 0) return bundle;

            await LoadRequirementsAsync(bundle, query.Project, ids, ct);
            if (bundle.Requirements.Count == 0)
            {
                bundle.ErrorMessage =
                    $"TFS returned none of the {ids.Count} work items that matched. They may have been deleted, or this token cannot read them.";
                return bundle;
            }
            var prefetched = await PrefetchPullRequestsAsync(query.Repositories, PullRequestsPerRepo, ct);

            // Each task writes only to its own row, so no shared state is mutated here.
            await Task.WhenAll(bundle.Requirements.Select(row =>
                ResolvePullRequestsAsync(query, row, prefetched.ByRepo, ct)));

            // Sorted before the commit pass, so "the first rows" means the first rows on
            // screen rather than whatever order the work item query happened to return.
            bundle.Requirements = bundle.Requirements
                .OrderBy(r => r.AssignedTo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.WorkItemId)
                .ToList();
            RecordDiscoveryGaps(bundle, prefetched.Failures, query.MatchByBranchOrTitle);
            bundle.DiscoveryWarnings.InsertRange(0, UnreadWorkItemWarnings(bundle.UnreadWorkItemIds));

            await LoadCommitsAsync(bundle, query.EagerCommitRows, ct);
            bundle.DeferredRowCount = bundle.Requirements.Count(r => !r.CommitsLoaded);
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex, "Code merging load failed for {Project} / {Iteration}", query.Project, query.IterationSummary);
            bundle.ErrorMessage = TfsErrors.Describe(ex);
        }
        return bundle;
    }

    /// <summary>
    /// Recent pull requests that carry no work-item link, which is exactly why they never show
    /// on the sheet. Run only when asked: it needs one extra request per pull request to read
    /// its links, so folding it into the sheet's load would slow every load for everyone.
    /// </summary>
    /// <param name="ignoreIdInBranchOrTitle">
    /// Mirrors the sheet's Discovery setting. When true, a pull request whose branch or title
    /// carries a work-item id is not reported: the sheet finds those by that id, so they are
    /// not invisible, which is what this view is for.
    /// </param>
    public async Task<UnlinkedPrBundle> GetUnlinkedPullRequestsAsync(
        IReadOnlyList<TfsRepo> repos, int days,
        IReadOnlyDictionary<string, string> qaBranchByRepoId,
        bool ignoreIdInBranchOrTitle, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        var bundle = new UnlinkedPrBundle { HasRun = true, SearchedFrom = since };

        if (repos.Count == 0)
        {
            bundle.ErrorMessage = "Select at least one repository first.";
            return bundle;
        }

        try
        {
            var prefetched = await PrefetchPullRequestsAsync(repos, UnlinkedPrsPerRepo, ct);
            var raised = RaisedSince(prefetched, since);
            bundle.RaisedPrCount = raised.Count;

            // Narrowed to the QA branch before the link checks, not after: each check costs a
            // request, and a pull request aimed somewhere else is not what this view is for.
            var recent = raised.Where(pr => PassesRepoTargetFilter(pr, qaBranchByRepoId)).ToList();
            bundle.ScannedPrCount = recent.Count;

            if (recent.Count > MaxUnlinkedPrChecks)
            {
                bundle.TruncationNotice =
                    $"{recent.Count} pull requests targeted the QA branch in this window. Checking the {MaxUnlinkedPrChecks} most recent. Shorten the range to cover the rest.";
                recent = recent.Take(MaxUnlinkedPrChecks).ToList();
            }

            var checkedPrs = await Task.WhenAll(recent.Select(pr => CheckWorkItemLinkAsync(pr, ct)));

            // A link check that failed comes back as linked, so a transient error cannot
            // invent an unlinked pull request and send someone chasing a non-problem.
            var unlinked = checkedPrs.Where(x => !x.Linked).Select(x => x.Pr).ToList();
            bundle.ReadFailures = prefetched.Failures
                .Concat(checkedPrs.Select(x => x.Failure).OfType<DiscoveryWarning>())
                .ToList();

            if (ignoreIdInBranchOrTitle)
            {
                var referenced = unlinked.Where(ReferencesWorkItemId).ToList();
                bundle.ReferencedByIdCount = referenced.Count;
                unlinked = unlinked.Except(referenced).ToList();
            }

            await Task.WhenAll(unlinked.Select(pr => ReadCommitsAsync(pr, ct)));
            bundle.PullRequests = unlinked;

            foreach (var repo in repos)
            {
                bundle.QaBranchByRepo[repo.Name] =
                    qaBranchByRepoId.TryGetValue(repo.Id, out var qa) ? qa : "";
            }
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex, "Unlinked pull-request search failed for {Repos} repositories", repos.Count);
            bundle.ErrorMessage = TfsErrors.Describe(ex);
        }
        return bundle;
    }

    private static List<MergingPullRequest> RaisedSince(PrefetchedPullRequests prefetched, DateTime since) =>
        prefetched.ByRepo.Values
            .SelectMany(x => x)
            .Where(pr => pr.CreationDate.HasValue && pr.CreationDate.Value >= since)
            .GroupBy(pr => (pr.RepositoryId, pr.PullRequestId))
            .Select(g => g.First())
            .OrderByDescending(pr => pr.CreationDate)
            .ToList();

    /// <summary>One pull request's link check: whether it links a work item, and the failure when it could not be read.</summary>
    private readonly record struct LinkCheck(MergingPullRequest Pr, bool Linked, DiscoveryWarning? Failure);

    /// <summary>
    /// Null-safe on purpose: a failed lookup counts as linked so an API hiccup cannot produce a false
    /// "nobody linked this". The failure is handed back, so the page can say the search may have missed some.
    /// </summary>
    private async Task<LinkCheck> CheckWorkItemLinkAsync(MergingPullRequest pr, CancellationToken ct)
    {
        var url = _req.ProjectApi(pr.Project,
            $"git/repositories/{pr.RepositoryId}/pullRequests/{pr.PullRequestId}/workitems?api-version={_req.Ver}");
        try
        {
            var data = await _req.GetJsonAsync(url, ct);
            return new LinkCheck(pr, TfsJson.Values(data).Any(), null);
        }
        catch (Exception ex) when (!TfsErrors.IsRequestedCancellation(ex, ct))
        {
            _logger.LogError(ex, "Could not read the work-item links of PR {PrId} in {Repo}, so it counts as linked",
                pr.PullRequestId, pr.RepositoryName);
            return new LinkCheck(pr, true,
                new DiscoveryWarning($"Links of pull request !{pr.PullRequestId} in {pr.RepositoryName}", TfsErrors.Describe(ex)));
        }
    }

    /// <summary>Branch names across the given repositories, deduplicated and sorted.</summary>
    public async Task<List<string>> GetBranchesAsync(IReadOnlyList<TfsRepo> repos, CancellationToken ct = default)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perRepo = await Task.WhenAll(repos.Select(r => FetchRepoBranchesAsync(r, ct)));
        foreach (var name in perRepo.SelectMany(x => x)) all.Add(name);
        return all.OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ═══ BRANCHES ═══

    /// <summary>
    /// Branch names for the pickers. A failure answers an empty list rather than the pages read so far:
    /// BranchCacheService never keeps an empty result, so the next read retries, where a partial list
    /// would have been cached as the whole repository. A cancelled listing answers empty too, and the
    /// caller that cancelled it decides what that means.
    /// </summary>
    private async Task<List<string>> FetchRepoBranchesAsync(TfsRepo repo, CancellationToken ct)
    {
        try
        {
            var listing = await GitRefListing.ListBranchesAsync(_req, repo, ct);
            if (listing.Truncated)
                _logger.LogWarning("Branch listing for {Repo} stopped at the page cap after {Count} branches", repo.Name, listing.Refs.Count);
            return listing.Refs.Select(r => r.Name).ToList();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Branch listing for {Repo} was cancelled", repo.Name);
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load branches for repo {Repo}", repo.Name);
            return new List<string>();
        }
    }
}
