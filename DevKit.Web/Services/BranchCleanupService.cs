using System.Diagnostics;
using System.Globalization;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Branch Delete's TFS work: list the chosen repositories' branches, find the work items that link
/// them, and delete the ones picked. The rules that decide what may be deleted live in
/// <see cref="BranchCleanupClassifier"/>, and deletion applies them again rather than trusting the page.
/// </summary>
public sealed class BranchCleanupService
{
    /// <summary>Branches per delete request: quick in bulk, yet small enough that the progress bar moves.</summary>
    private const int DeleteBatchSize = 50;

    private const string ListingStage = "Listing branches";
    private const string SprintStage = "Reading the sprint's work items";
    private const string LinkStage = "Checking work-item links";
    private const string DeleteStage = "Deleting branches";

    private readonly TfsApiService _tfs;
    private readonly BranchCacheService _branchCache;
    private readonly ILogger<BranchCleanupService> _logger;

    public BranchCleanupService(TfsApiService tfs, BranchCacheService branchCache, ILogger<BranchCleanupService> logger)
    {
        _tfs = tfs;
        _branchCache = branchCache;
        _logger = logger;
    }

    /// <summary>
    /// Every branch in the repositories, classified. A repository that cannot be listed is reported and
    /// skipped; one whose open pull requests cannot be read is listed with every branch locked. A failed
    /// work-item read fails the whole load, because without it linked branches would look unattached.
    /// </summary>
    public async Task<BranchCleanupResult> LoadAsync(
        BranchCleanupQuery query, IProgress<BranchCleanupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var started = Stopwatch.StartNew();
        _logger.LogInformation("Branch cleanup load started for {Repos} repositories and {Sprints} sprints",
            query.Repos.Count, query.SprintPaths.Count);

        var result = new BranchCleanupResult();
        var listings = await ListRepositoriesAsync(query.Repos, result, progress, ct);
        var sprintItems = await LoadSprintWorkItemsAsync(query, progress, ct);

        var rows = BranchCleanupClassifier.BuildRows(listings, query);
        BranchCleanupClassifier.AttachLinks(rows, sprintItems, fromSelectedSprint: true);

        var namedIds = BranchCleanupClassifier.UncheckedWorkItemIds(rows, sprintItems);
        progress?.Report(new BranchCleanupProgress(LinkStage, 0, namedIds.Count));
        var named = await _tfs.GetBranchLinksAsync(namedIds, ct);
        BranchCleanupClassifier.AttachLinks(rows, named, fromSelectedSprint: false);
        progress?.Report(new BranchCleanupProgress(LinkStage, namedIds.Count, namedIds.Count));

        result.SprintWorkItemCount = sprintItems.Count;
        result.CheckedWorkItemCount = sprintItems.Count + named.Count;
        result.Rows.AddRange(rows
            .OrderBy(r => r.Repo.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Branch, StringComparer.OrdinalIgnoreCase));

        _logger.LogInformation(
            "Branch cleanup load finished in {Elapsed} ms: {Rows} branches, {Linked} linked, {Unattached} unattached, {Protected} protected, {Warnings} warnings",
            started.ElapsedMilliseconds, rows.Count,
            rows.Count(r => r.LinkState == BranchLinkState.Linked),
            rows.Count(r => r.LinkState == BranchLinkState.Unattached),
            rows.Count(r => !r.CanDelete),
            result.Warnings.Count);
        return result;
    }

    /// <summary>
    /// Deletes the rows in batches per repository. A protected row is refused here as well, so a stale page
    /// can never delete what the rules keep. Branch pickers are refreshed for every repository touched.
    /// </summary>
    public async Task<List<BranchDeleteOutcome>> DeleteAsync(
        IReadOnlyCollection<BranchCleanupRow> rows, IProgress<BranchCleanupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var outcomes = rows.Where(r => !r.CanDelete)
            .Select(r => new BranchDeleteOutcome(r.Repo.Id, r.Repo.Name, r.Branch, false, $"Not deleted — {r.ProtectedReason}."))
            .ToList();

        var deletable = rows.Where(r => r.CanDelete).ToList();
        var done = 0;
        _logger.LogInformation("Branch cleanup delete started: {Count} branches in {Repos} repositories",
            deletable.Count, deletable.Select(r => r.Repo.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        progress?.Report(new BranchCleanupProgress(DeleteStage, 0, deletable.Count));

        foreach (var byRepo in deletable.GroupBy(r => r.Repo.Id, StringComparer.OrdinalIgnoreCase))
        {
            var repo = byRepo.First().Repo;
            var deletedHere = 0;
            foreach (var batch in byRepo.Chunk(DeleteBatchSize))
            {
                var answered = await DeleteBatchAsync(repo, batch, ct);
                outcomes.AddRange(answered);
                deletedHere += answered.Count(o => o.Deleted);
                done += batch.Length;
                progress?.Report(new BranchCleanupProgress(DeleteStage, done, deletable.Count));
            }

            if (deletedHere > 0) _branchCache.Invalidate(repo.Id);
            _logger.LogInformation("Deleted {Deleted} of {Requested} branches in {Repo}", deletedHere, byRepo.Count(), repo.Name);
        }
        return outcomes;
    }

    private async Task<List<RepoBranches>> ListRepositoriesAsync(
        IReadOnlyList<TfsRepo> repos, BranchCleanupResult result, IProgress<BranchCleanupProgress>? progress, CancellationToken ct)
    {
        var done = 0;
        progress?.Report(new BranchCleanupProgress(ListingStage, 0, repos.Count));

        var listings = await Task.WhenAll(repos.Select(async repo =>
        {
            var listing = await ListRepositoryAsync(repo, ct);
            progress?.Report(new BranchCleanupProgress(ListingStage, Interlocked.Increment(ref done), repos.Count));
            return listing;
        }));

        result.Warnings.AddRange(listings.SelectMany(l => l.Warnings));
        return listings.Where(l => l.Refs.Count > 0).ToList();
    }

    /// <summary>Branches and open pull requests are independent reads, so they run side by side.</summary>
    private async Task<RepoBranches> ListRepositoryAsync(TfsRepo repo, CancellationToken ct)
    {
        var refsRead = TryReadAsync(() => _tfs.ListBranchRefsAsync(repo, ct), "branches", repo, ct);
        var prsRead = TryReadAsync(() => _tfs.GetActivePullRequestBranchesAsync(repo, ct), "open pull requests", repo, ct);
        await Task.WhenAll(refsRead, prsRead);

        var (listing, listError) = await refsRead;
        var (openPrBranches, prError) = await prsRead;

        var warnings = new List<string>();
        if (listing == null)
        {
            warnings.Add($"{repo.Name}: the branches could not be listed ({listError}).");
            return new RepoBranches(repo, Array.Empty<TfsRef>(), null, warnings);
        }
        if (listing.Truncated)
            warnings.Add($"{repo.Name}: only the first {listing.Refs.Count} branches were listed.");
        if (openPrBranches == null)
            warnings.Add($"{repo.Name}: open pull requests could not be read ({prError}), so every branch here is locked.");

        return new RepoBranches(repo, listing.Refs, openPrBranches, warnings);
    }

    private async Task<List<WorkItemBranches>> LoadSprintWorkItemsAsync(
        BranchCleanupQuery query, IProgress<BranchCleanupProgress>? progress, CancellationToken ct)
    {
        if (query.Area == null || query.SprintPaths.Count == 0) return new List<WorkItemBranches>();

        progress?.Report(new BranchCleanupProgress(SprintStage, 0, 1));
        var ids = await _tfs.QuerySprintWorkItemIdsAsync(query.Area.Project, query.Area.Path, query.SprintPaths, ct);
        var items = await _tfs.GetBranchLinksAsync(ids, ct);
        progress?.Report(new BranchCleanupProgress(SprintStage, 1, 1));

        _logger.LogInformation("Branch cleanup read {Count} work items from the selected sprints", items.Count);
        return items;
    }

    private async Task<List<BranchDeleteOutcome>> DeleteBatchAsync(TfsRepo repo, BranchCleanupRow[] batch, CancellationToken ct)
    {
        var refs = batch.Select(r => new TfsRef { Name = r.Branch, ObjectId = r.ObjectId }).ToList();
        try
        {
            return await _tfs.DeleteBranchesAsync(repo, refs, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Delete request for {Count} branches in {Repo} failed", refs.Count, repo.Name);
            return refs.Select(r => new BranchDeleteOutcome(repo.Id, repo.Name, r.Name, false, $"Not deleted — {ex.Message}")).ToList();
        }
    }

    /// <summary>A read that reports its failure instead of throwing, so one repository cannot sink the others.</summary>
    private async Task<(T? Value, string? Error)> TryReadAsync<T>(Func<Task<T>> read, string what, TfsRepo repo, CancellationToken ct)
        where T : class
    {
        try
        {
            return (await read(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Branch cleanup could not read {What} for {Repo}", what, repo.Name);
            return (null, ex.Message);
        }
    }
}

/// <summary>One repository's branches and the branches its open pull requests come from (null when unknown).</summary>
public sealed record RepoBranches(
    TfsRepo Repo, IReadOnlyList<TfsRef> Refs, IReadOnlySet<string>? OpenPrBranches, IReadOnlyList<string> Warnings);

/// <summary>
/// The rules of Branch Delete, free of TFS calls so each one is pinned by a test: which branches must be
/// kept, which a work item links, and which carry the team prefix but are linked by nothing.
/// </summary>
public static class BranchCleanupClassifier
{
    public const string ProtectedName = "Protected branch";
    public const string ConfiguredBranch = "QA or merging branch in Settings";
    public const string OpenPullRequest = "Has an active pull request";
    public const string PullRequestsUnknown = "Open pull requests could not be checked";

    private const string ReleasePrefix = "release/";

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        { "main", "master", "develop", "release", "hotfix" };

    public static List<BranchCleanupRow> BuildRows(IEnumerable<RepoBranches> repos, BranchCleanupQuery query)
    {
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentNullException.ThrowIfNull(query);

        var team = string.IsNullOrWhiteSpace(query.TeamName) ? "" : BranchNaming.TeamSegment(query.TeamName);
        var sprintSegments = query.SprintPaths
            .Select(path => BranchNaming.SprintSegment(path.Split('\\').LastOrDefault()))
            .Where(segment => segment.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = new List<BranchCleanupRow>();
        foreach (var repo in repos)
        {
            var configured = query.ConfiguredBranchesByRepoId.TryGetValue(repo.Repo.Id, out var names)
                ? names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(GitRefName.Strip).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var branch in repo.Refs)
            {
                var parsed = BranchNaming.Parse(branch.Name);
                rows.Add(new BranchCleanupRow
                {
                    Repo = repo.Repo,
                    Branch = branch.Name,
                    ObjectId = branch.ObjectId,
                    Creator = branch.Creator,
                    Parsed = parsed,
                    MentionsTeam = BranchNaming.MentionsTeam(branch.Name, team),
                    InSelectedSprint = parsed != null && sprintSegments.Contains(parsed.Sprint),
                    ProtectedReason = ProtectedReason(branch.Name, configured, repo.OpenPrBranches)
                });
            }
        }
        return rows;
    }

    /// <summary>Why a branch must be kept, or null when it may be deleted.</summary>
    public static string? ProtectedReason(string branch, IReadOnlySet<string> configured, IReadOnlySet<string>? openPrBranches)
    {
        if (ProtectedNames.Contains(branch) || branch.StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase)) return ProtectedName;
        if (configured.Contains(branch)) return ConfiguredBranch;
        if (openPrBranches == null) return PullRequestsUnknown;
        return openPrBranches.Contains(branch) ? OpenPullRequest : null;
    }

    /// <summary>Records which work items link each row; links to branches that were not listed are ignored.</summary>
    public static void AttachLinks(IReadOnlyList<BranchCleanupRow> rows, IEnumerable<WorkItemBranches> workItems, bool fromSelectedSprint)
    {
        var byKey = new Dictionary<string, BranchCleanupRow>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows) byKey.TryAdd(row.Key, row);

        foreach (var workItem in workItems)
        {
            var linked = new LinkedWorkItem(workItem.Item.Id, workItem.Item.Title, workItem.Item.Type,
                workItem.Item.State, workItem.Item.Sprint, workItem.Item.Project);

            foreach (var link in workItem.Links)
            {
                if (!byKey.TryGetValue(BranchCleanupRow.KeyOf(link.RepositoryId, link.BranchName), out var row)) continue;
                if (row.LinkedBy.All(l => l.Id != linked.Id)) row.LinkedBy.Add(linked);
                if (fromSelectedSprint) row.InSelectedSprint = true;
            }
        }
    }

    /// <summary>
    /// Work items named by team branches that nothing has linked yet — their links still have to be read
    /// before any of those branches can be called unattached. Ids already read are not asked for twice.
    /// </summary>
    public static List<int> UncheckedWorkItemIds(IEnumerable<BranchCleanupRow> rows, IEnumerable<WorkItemBranches> alreadyRead)
    {
        var read = alreadyRead.Select(w => w.Item.Id).ToHashSet(StringComparer.Ordinal);
        return rows
            .Where(r => r.MentionsTeam && r.LinkedBy.Count == 0 && r.Parsed != null)
            .Select(r => r.Parsed!.WorkItemId)
            .Where(id => !read.Contains(id.ToString(CultureInfo.InvariantCulture)))
            .Distinct()
            .ToList();
    }
}
