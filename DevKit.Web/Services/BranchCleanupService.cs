using System.Diagnostics;
using System.Globalization;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Branch Cleanup's TFS work: list the chosen repositories' branches, find the work items that link
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
    private readonly SettingsService _settings;
    private readonly ILogger<BranchCleanupService> _logger;

    public BranchCleanupService(
        TfsApiService tfs, BranchCacheService branchCache, SettingsService settings, ILogger<BranchCleanupService> logger)
    {
        _tfs = tfs;
        _branchCache = branchCache;
        _settings = settings;
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
    /// Deletes the rows in batches per repository. A row locked at load is refused unsent. Just before a
    /// repository's batches, its active pull requests and its Settings branches are read again and the
    /// load's rule is applied to them, so a branch that became protected since the load is refused unsent
    /// too and its row locked with the reason. Branch pickers are refreshed for every repository touched.
    /// </summary>
    public async Task<List<BranchDeleteOutcome>> DeleteAsync(
        IReadOnlyCollection<BranchCleanupRow> rows, IProgress<BranchCleanupProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var outcomes = rows.Where(r => !r.CanDelete).Select(Refused).ToList();

        var deletable = rows.Where(r => r.CanDelete).ToList();
        var done = 0;
        _logger.LogInformation("Branch cleanup delete started: {Count} branches in {Repos} repositories",
            deletable.Count, deletable.Select(r => r.Repo.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Advance(0);

        foreach (var byRepo in deletable.GroupBy(r => r.Repo.Id, StringComparer.OrdinalIgnoreCase))
        {
            outcomes.AddRange(await DeleteInRepositoryAsync(byRepo.First().Repo, byRepo.ToList(), Advance, ct));
        }
        return outcomes;

        void Advance(int answered)
        {
            done += answered;
            progress?.Report(new BranchCleanupProgress(DeleteStage, done, deletable.Count));
        }
    }

    private async Task<List<BranchDeleteOutcome>> DeleteInRepositoryAsync(
        TfsRepo repo, IReadOnlyList<BranchCleanupRow> rows, Action<int> advance, CancellationToken ct)
    {
        var protection = await CurrentProtectionAsync(repo, ct);
        foreach (var row in rows)
        {
            row.ProtectedReason = protection(row.Branch);
        }

        var outcomes = rows.Where(r => !r.CanDelete).Select(Refused).ToList();
        if (outcomes.Count > 0)
        {
            _logger.LogInformation("Refused {Count} branches in {Repo} that are protected now", outcomes.Count, repo.Name);
            advance(outcomes.Count);
        }

        var deletedHere = 0;
        foreach (var batch in rows.Where(r => r.CanDelete).Chunk(DeleteBatchSize))
        {
            var answered = await DeleteBatchAsync(repo, batch, ct);
            outcomes.AddRange(answered);
            deletedHere += answered.Count(o => o.Deleted);
            advance(batch.Length);
        }

        if (deletedHere > 0)
        {
            _branchCache.Invalidate(repo.Id);
        }
        _logger.LogInformation("Deleted {Deleted} of {Requested} branches in {Repo}", deletedHere, rows.Count, repo.Name);
        return outcomes;
    }

    /// <summary>
    /// The rule the load applied, run on what TFS and Settings say now: another tab may have made a branch a
    /// QA or merging branch, and a pull request may have been opened since the page was loaded.
    /// </summary>
    private async Task<Func<string, string?>> CurrentProtectionAsync(TfsRepo repo, CancellationToken ct)
    {
        var configured = BranchCleanupClassifier.ConfiguredSet(
            BranchCleanupClassifier.ConfiguredNames(_settings.GetRepoBranches(repo.Id)));
        var (openPrBranches, _) = await ReadOpenPullRequestBranchesAsync(repo, ct);
        return branch => BranchCleanupClassifier.ProtectedReason(branch, configured, openPrBranches);
    }

    private static BranchDeleteOutcome Refused(BranchCleanupRow row) =>
        new(row.Repo.Id, row.Repo.Name, row.Branch, false, $"Not deleted: {row.ProtectedReason}.");

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
        var prsRead = ReadOpenPullRequestBranchesAsync(repo, ct);
        await Task.WhenAll(refsRead, prsRead);

        var (listing, listError) = await refsRead;
        var (openPrBranches, prWarning) = await prsRead;

        var warnings = new List<string>();
        if (listing == null)
        {
            warnings.Add($"{repo.Name}: the branches could not be listed ({listError}).");
            return new RepoBranches(repo, Array.Empty<TfsRef>(), null, warnings);
        }
        if (listing.Truncated)
        {
            warnings.Add($"{repo.Name}: only the first {listing.Refs.Count} branches were listed.");
        }
        if (prWarning != null)
        {
            warnings.Add(prWarning);
        }

        return new RepoBranches(repo, listing.Refs, openPrBranches, warnings);
    }

    /// <summary>
    /// The branches the repository's active pull requests use, or null with the reason when they are not all
    /// known (the read failed, or there were more active pull requests than are read), so every branch is kept.
    /// </summary>
    private async Task<(IReadOnlySet<string>? Branches, string? Warning)> ReadOpenPullRequestBranchesAsync(
        TfsRepo repo, CancellationToken ct)
    {
        var (read, error) = await TryReadAsync(() => _tfs.GetActivePullRequestBranchesAsync(repo, ct), "open pull requests", repo, ct);
        if (read == null)
        {
            return (null, $"{repo.Name}: open pull requests could not be read ({error}), so every branch here is locked.");
        }
        if (read.Truncated)
        {
            _logger.LogWarning("Read {Max} active pull requests in {Repo} without reaching the last, so every branch in it is kept",
                TfsApiService.MaxActivePullRequests, repo.Name);
            return (null,
                $"{repo.Name}: only the first {TfsApiService.MaxActivePullRequests} active pull requests were read, so every branch here is locked.");
        }
        return (read.Branches, null);
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
            _logger.LogError(ex, "Delete request for {Count} branches in {Repo} failed", refs.Count, repo.Name);
            return refs.Select(r => new BranchDeleteOutcome(repo.Id, repo.Name, r.Name, false, $"Not deleted: {ex.Message}")).ToList();
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
            _logger.LogError(ex, "Branch cleanup could not read {What} for {Repo}", what, repo.Name);
            return (null, ex.Message);
        }
    }
}

/// <summary>One repository's branches and the branches its open pull requests merge from or into (null when not all are known).</summary>
public sealed record RepoBranches(
    TfsRepo Repo, IReadOnlyList<TfsRef> Refs, IReadOnlySet<string>? OpenPrBranches, IReadOnlyList<string> Warnings);

/// <summary>
/// The rules of Branch Cleanup, free of TFS calls so each one is pinned by a test: which branches must be
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
            var configured = ConfiguredSet(
                query.ConfiguredBranchesByRepoId.TryGetValue(repo.Repo.Id, out var names) ? names : Array.Empty<string>());

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

    /// <summary>The QA and merging branches Settings holds for one repository, blanks left out.</summary>
    public static IReadOnlyList<string> ConfiguredNames(RepoBranchDefaults branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        return new[] { branches.QaBranch, branches.MergingBranch }.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
    }

    /// <summary>Configured branch names the way <see cref="ProtectedReason"/> compares them: no refs/heads/, any case.</summary>
    public static HashSet<string> ConfiguredSet(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(GitRefName.Strip).ToHashSet(StringComparer.OrdinalIgnoreCase);

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
    /// Work items named by team branches that nothing has linked yet. Their links still have to be read
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
