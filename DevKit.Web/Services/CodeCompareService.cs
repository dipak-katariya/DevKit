using System.Diagnostics;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>What to compare, where each repository's code should be, and the scope that sets the limits.</summary>
public sealed record CodeCompareRequest(
    IReadOnlyList<MergingPullRequest> PullRequests,
    IReadOnlyDictionary<string, string> MergingBranchByRepoId,
    CodeCompareScope Scope);

/// <summary>
/// Compares pull requests' code with their repositories' merging branches — the answer to "is this work
/// actually on the release branch" that does not depend on commit SHAs or messages. A cherry-pick under
/// another message, a squash, or a conflict fixed and committed by hand all read correctly, and code
/// lost while resolving a conflict reads as missing even when every commit is on the branch.
///
/// A pull request's change is the diff of its merge commit against the QA commit it merged onto. For
/// each file, the git object ids of four versions — QA before, QA after, the branch now and QA now —
/// settle most files without downloading anything; the rest are compared line by line by
/// <see cref="CodePresenceAnalyzer"/>. Each pull request's <see cref="MergingPullRequest.CodeCheck"/> is
/// replaced as soon as it finishes, so the sheet fills in while the run continues.
/// </summary>
public sealed partial class CodeCompareService
{
    private const int MaxParallelPullRequests = 3;
    private const int MaxParallelFiles = 4;
    private const long BlobCacheBytes = 96L * 1024 * 1024;
    private const int RunIdLength = 8;

    private readonly TfsRequestClient _req;
    private readonly ILogger<CodeCompareService> _logger;

    public CodeCompareService(HttpClient http, SettingsService settings, ILogger<CodeCompareService> logger)
    {
        _req = new TfsRequestClient(http, settings);
        _logger = logger;
    }

    /// <summary>
    /// Compares every pull request in the request. Never throws for a pull request that fails — that
    /// one is marked <see cref="CodeVerdict.Failed"/> and the rest carry on. Cancelling stops the run
    /// and puts back whatever the unfinished pull requests showed before it started.
    /// </summary>
    public async Task<CodeCompareRunSummary> CompareAsync(
        CodeCompareRequest request, IProgress<CodeCompareProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runId = Guid.NewGuid().ToString("N")[..RunIdLength];
        using var logScope = _logger.BeginScope(new Dictionary<string, object> { ["CodeCompareRun"] = runId });
        var prs = request.PullRequests.Distinct().ToList();
        var previous = prs.ToDictionary<MergingPullRequest, MergingPullRequest, PrCodeCheck?>(
            p => p, p => p.CodeCheck, ReferenceEqualityComparer.Instance);
        var run = new RunTracker(prs.Count, progress);
        var reader = new GitObjectReader(_req, BlobCacheBytes);

        _logger.LogInformation(
            "Code compare {RunId} started: {Scope} scope, {PullRequests} pull requests in {Repositories} repositories",
            runId, request.Scope, prs.Count, prs.Select(p => p.RepositoryId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var work = new List<WorkItem>();
        var stopped = false;
        try
        {
            Prepare(prs, request, run, work);
            var tips = await ResolveTipsAsync(reader, work, runId, ct).ConfigureAwait(false);
            var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelPullRequests, CancellationToken = ct };
            await Parallel.ForEachAsync(work, options, (item, token) =>
                new ValueTask(CheckPullRequestAsync(new PrJob(item, tips, reader, request.Scope, run, runId), token)))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // Whatever surfaced after Stop — the cancellation itself, or a TFS error from a request that
            // was already on its way — the run was stopped, and nothing unfinished may keep a half answer.
            stopped = true;
            _logger.LogError(ex, "Code compare {RunId} stopped by the user after {Done} of {Total} pull requests",
                runId, run.Done, prs.Count);
            RestoreUnfinished(work, previous);
        }
        catch (Exception ex)
        {
            // Per-pull-request failures are caught where they happen; reaching here is a fault in the run
            // itself, so nothing is left showing "Comparing…" for ever.
            _logger.LogError(ex, "Code compare {RunId} failed after {Done} of {Total} pull requests", runId, run.Done, prs.Count);
            FailUnfinished(work, request.Scope, TfsErrors.Describe(ex));
        }

        var summary = run.Summary(request.Scope, stopped, reader.RequestCount);
        _logger.LogInformation(
            "Code compare {RunId} finished in {ElapsedMs} ms: {Present} present, {Missing} missing, {Partial} partial, " +
            "{Unverified} unverified, {Skipped} skipped, {TooLarge} too large, {Failed} failed across {Files} files using {Requests} TFS requests",
            runId, (long)summary.Elapsed.TotalMilliseconds, summary.Present, summary.Missing, summary.Partial,
            summary.Unverified, summary.Skipped, summary.TooLarge, summary.Failed, summary.Files, summary.Requests);
        return summary;
    }

    /// <summary>
    /// The line-by-line detail for one file whose verdict came from object ids alone, read on demand so
    /// the sprint-wide run never pays for detail nobody opens. The verdict itself is not changed.
    /// </summary>
    public async Task<FileCodeCheck> ExplainAsync(MergingPullRequest pr, FileCodeCheck file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(file);
        if (!file.CanExplain)
        {
            return file;
        }

        var reader = new GitObjectReader(_req, BlobCacheBytes);
        var limits = CodeCompareLimits.For(CodeCompareScope.PullRequest);
        var ids = new BlobIds(file.BaseBlobId, file.AfterBlobId, file.DestinationBlobId, file.QaBlobId);
        try
        {
            var texts = await ReadVersionsAsync(new BlobSource(reader, pr.Project, pr.RepositoryId), ids, limits, ct).ConfigureAwait(false);
            if (texts.Problem is not null)
            {
                return file with { DetailError = texts.Problem };
            }

            var analysis = CodePresenceAnalyzer.Analyze(texts.Versions!);
            return file with
            {
                Hunks = analysis.Problems,
                HunkCount = analysis.HunkCount,
                HunksPresent = analysis.HunksPresent,
                Approximate = analysis.Approximate,
                DetailError = analysis.Problems.Count == 0 ? "There are no changed lines to show — only whitespace differs." : ""
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Code compare: line detail for {Path} in pull request {PrId} ({Repo}) failed",
                file.Path, pr.PullRequestId, pr.RepositoryName);
            return file with { DetailError = $"Couldn't load the lines: {TfsErrors.Describe(ex)}" };
        }
    }

    /// <summary>
    /// Marks each pull request this run will compare with a marker only this run holds, and adds it to
    /// <paramref name="work"/> as it goes — so if this throws half-way, the caller still knows which
    /// pull requests to put back.
    /// </summary>
    private static void Prepare(List<MergingPullRequest> prs, CodeCompareRequest request, RunTracker run, List<WorkItem> work)
    {
        foreach (var pr in prs)
        {
            var branch = request.MergingBranchByRepoId.TryGetValue(pr.RepositoryId, out var configured)
                ? GitRefName.Strip((configured ?? "").Trim())
                : "";
            var skip = SkipReason(pr, branch);
            if (skip is not null)
            {
                var skipped = PrCodeCheck.NotCompared(CodeVerdict.Skipped, skip, request.Scope, branch);
                pr.CodeCheck = skipped;
                run.Finished(pr, skipped);
                continue;
            }

            var marker = PrCodeCheck.InProgress(request.Scope, branch);
            pr.CodeCheck = marker;
            work.Add(new WorkItem(pr, branch, marker));
        }
    }

    private static string? SkipReason(MergingPullRequest pr, string branch)
    {
        if (!pr.IsCompleted)
        {
            return string.Equals(pr.Status, "abandoned", StringComparison.OrdinalIgnoreCase)
                ? "Abandoned, so it has no code to compare."
                : "Not completed yet, so its code isn't on QA to compare.";
        }
        if (branch.Length == 0)
        {
            return $"No merging branch is set for {pr.RepositoryName} under Branch setup.";
        }
        return string.IsNullOrWhiteSpace(pr.LastMergeCommitId)
            ? "TFS has no merge commit for this pull request, so its change can't be read."
            : null;
    }

    /// <summary>
    /// Reads every branch the run needs once, up front. Comparing against a fixed commit keeps the
    /// result consistent even if someone pushes to the branch while the run is going.
    /// </summary>
    private async Task<IReadOnlyDictionary<TipKey, BranchTip>> ResolveTipsAsync(
        GitObjectReader reader, List<WorkItem> work, string runId, CancellationToken ct)
    {
        var keys = work
            .SelectMany(w => new[] { TipKey.For(w.Pr, w.Branch), TipKey.For(w.Pr, w.Pr.TargetBranch) })
            .Where(k => k.Branch.Length > 0)
            .Distinct()
            .ToList();

        var resolved = await Task.WhenAll(keys.Select(async key =>
            (Key: key, Tip: await TryBranchTipAsync(reader, key, runId, ct).ConfigureAwait(false)))).ConfigureAwait(false);
        return resolved.ToDictionary(r => r.Key, r => r.Tip);
    }

    /// <summary>The branch's commit, or why it could not be had — the two failures call for different fixes.</summary>
    private async Task<BranchTip> TryBranchTipAsync(GitObjectReader reader, TipKey key, string runId, CancellationToken ct)
    {
        try
        {
            var tip = await reader.BranchTipAsync(key.Project, key.RepoId, key.Branch, ct).ConfigureAwait(false);
            return tip is null
                ? new BranchTip(null, $"there is no branch named {key.Branch} in this repository")
                : new BranchTip(tip, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Code compare {RunId}: could not read branch {Branch} in repository {RepoId}",
                runId, key.Branch, key.RepoId);
            return new BranchTip(null, TfsErrors.Describe(ex));
        }
    }

    /// <summary>Puts back what each unfinished pull request showed before — unless something newer replaced this run's marker.</summary>
    private static void RestoreUnfinished(List<WorkItem> work, Dictionary<MergingPullRequest, PrCodeCheck?> previous)
    {
        foreach (var item in work)
        {
            item.Pr.TryReplaceCodeCheck(item.Marker, previous.GetValueOrDefault(item.Pr));
        }
    }

    private static void FailUnfinished(List<WorkItem> work, CodeCompareScope scope, string reason)
    {
        foreach (var item in work)
        {
            item.Pr.TryReplaceCodeCheck(item.Marker,
                PrCodeCheck.NotCompared(CodeVerdict.Failed, $"The comparison failed: {reason}", scope, item.Branch));
        }
    }

    private async Task CheckPullRequestAsync(PrJob job, CancellationToken ct)
    {
        var pr = job.Pr;
        var watch = Stopwatch.StartNew();
        PrCodeCheck result;
        try
        {
            result = await CompareCodeAsync(job, ct).ConfigureAwait(false);
            _logger.LogDebug(
                "Code compare {RunId}: pull request {PrId} in {Repo} is {Verdict} ({Files} files) after {ElapsedMs} ms",
                job.RunId, pr.PullRequestId, pr.RepositoryName, result.Verdict, result.Files.Count, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Code compare {RunId}: pull request {PrId} in {Repo} failed",
                job.RunId, pr.PullRequestId, pr.RepositoryName);
            result = job.NotCompared(CodeVerdict.Failed, $"The comparison failed: {TfsErrors.Describe(ex)}");
        }

        // Written only while this run still owns the pull request: a reset, a reload or a newer run
        // may have taken it over, and a result for the old settings must not land on top of theirs.
        pr.TryReplaceCodeCheck(job.Item.Marker, result);
        job.Run.Finished(pr, result);
    }

    private async Task<PrCodeCheck> CompareCodeAsync(PrJob job, CancellationToken ct)
    {
        var pr = job.Pr;
        var destination = job.Tips.GetValueOrDefault(TipKey.For(pr, job.Branch));
        if (destination.Commit is null)
        {
            return job.NotCompared(CodeVerdict.Failed,
                $"Couldn't read {job.Branch} in {pr.RepositoryName}: {destination.Problem ?? "it was not looked up"}.");
        }

        var baseCommit = !string.IsNullOrWhiteSpace(pr.LastMergeTargetCommitId)
            ? pr.LastMergeTargetCommitId
            : await job.Reader.FirstParentAsync(pr.Project, pr.RepositoryId, pr.LastMergeCommitId, ct).ConfigureAwait(false);
        if (baseCommit is null)
        {
            return job.NotCompared(CodeVerdict.Failed, "Couldn't find the QA commit this pull request merged onto.");
        }

        var limits = CodeCompareLimits.For(job.Scope);
        var changes = await job.Reader.ChangesAsync(
            pr.Project, pr.RepositoryId, baseCommit, pr.LastMergeCommitId, limits.MaxFilesPerPullRequest, ct).ConfigureAwait(false);
        if (changes.Truncated)
        {
            return job.NotCompared(CodeVerdict.TooLarge, TooManyFiles(job.Scope, limits));
        }
        if (changes.Incomplete)
        {
            return job.NotCompared(CodeVerdict.Failed,
                "TFS did not return the full list of files this pull request changed, so it can't be compared reliably.");
        }
        if (changes.Files.Count == 0)
        {
            // Nothing to compare is not the same as everything present, so it decides nothing.
            return job.NotCompared(CodeVerdict.Skipped, "This pull request changed no files, so there is no code to compare.");
        }

        // A pull request merged straight into the merging branch has no separate QA. "QA now" would be the
        // branch itself, every file would match it, and a branch that undid the change would read present.
        var mergedIntoBranch = GitRefName.Matches(pr.TargetBranch, job.Branch);
        var qa = mergedIntoBranch ? default : job.Tips.GetValueOrDefault(TipKey.For(pr, pr.TargetBranch));
        var snapshot = new Snapshot(pr.Project, pr.RepositoryId, baseCommit, pr.LastMergeCommitId, destination.Commit, qa.Commit);
        job.Run.Started(pr, changes.Files.Count);

        var files = new FileCodeCheck[changes.Files.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFiles, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Length), options, async (i, token) =>
        {
            files[i] = await CheckFileAsync(new FileJob(snapshot, changes.Files[i], limits, job), token).ConfigureAwait(false);
            job.Run.FileDone(pr);
        }).ConfigureAwait(false);

        var ordered = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var check = PrCodeCheck.FromFiles(ordered, job.Scope, job.Branch, destination.Commit);
        if (mergedIntoBranch)
        {
            return check with { Summary = check.Summary + $" It merged straight into {job.Branch}, so it is checked against that branch alone." };
        }
        return qa.Commit is not null
            ? check
            : check with
            {
                Summary = check.Summary + $" QA's current version couldn't be read ({qa.Problem ?? "no target branch"}), " +
                          "so changes QA made later were not taken into account."
            };
    }

    private static string TooManyFiles(CodeCompareScope scope, CodeCompareLimits limits)
    {
        var count = $"Changes more than {limits.MaxFilesPerPullRequest:N0} files";
        return scope switch
        {
            CodeCompareScope.Sprint => $"{count} — too many for the sprint-wide run. Use Check code on this pull request.",
            CodeCompareScope.WorkItem => $"{count} — too many for a work item check. Use Check code on this pull request.",
            _ => $"{count} — too many to compare here. Compare its branches in TFS instead."
        };
    }

    /// <param name="Marker">The in-progress check this run put on the pull request — proof it still owns it.</param>
    private sealed record WorkItem(MergingPullRequest Pr, string Branch, PrCodeCheck Marker);

    /// <summary>A branch's commit, or why there is none: the branch does not exist, or TFS could not be read.</summary>
    private readonly record struct BranchTip(string? Commit, string? Problem);

    /// <summary>A branch to read, normalised so the same branch is only read once per run.</summary>
    private sealed record TipKey(string Project, string RepoId, string Branch)
    {
        public static TipKey For(MergingPullRequest pr, string branch) =>
            new(pr.Project, pr.RepositoryId.ToLowerInvariant(), GitRefName.Strip((branch ?? "").Trim()));
    }

    /// <summary>The commits one pull request's files are read at.</summary>
    private sealed record Snapshot(string Project, string RepoId, string BaseCommit, string AfterCommit, string Destination, string? Qa);

    private sealed record PrJob(
        WorkItem Item, IReadOnlyDictionary<TipKey, BranchTip> Tips, GitObjectReader Reader,
        CodeCompareScope Scope, RunTracker Run, string RunId)
    {
        public MergingPullRequest Pr => Item.Pr;
        public string Branch => Item.Branch;

        public PrCodeCheck NotCompared(CodeVerdict verdict, string summary) =>
            PrCodeCheck.NotCompared(verdict, summary, Scope, Branch);
    }

    /// <summary>Counts and progress for one run. Thread-safe: pull requests and files finish in parallel.</summary>
    private sealed class RunTracker
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

        private readonly object _gate = new();
        private readonly int _total;
        private readonly IProgress<CodeCompareProgress>? _progress;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Dictionary<MergingPullRequest, (int Done, int Total)> _inFlight = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CodeVerdict, int> _verdicts = new();
        private int _done, _filesDone, _filesKnown;
        private string _current = "";
        private TimeSpan _lastReport = -ReportInterval;

        public RunTracker(int total, IProgress<CodeCompareProgress>? progress)
        {
            _total = total;
            _progress = progress;
        }

        public int Done
        {
            get
            {
                lock (_gate)
                {
                    return _done;
                }
            }
        }

        public void Started(MergingPullRequest pr, int files)
        {
            lock (_gate)
            {
                _inFlight[pr] = (0, files);
                _filesKnown += files;
                _current = $"!{pr.PullRequestId} · {pr.RepositoryName}";
                Report(force: false);
            }
        }

        public void FileDone(MergingPullRequest pr)
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(pr, out var state))
                {
                    _inFlight[pr] = (state.Done + 1, state.Total);
                }
                _filesDone++;
                Report(force: false);
            }
        }

        public void Finished(MergingPullRequest pr, PrCodeCheck? result)
        {
            lock (_gate)
            {
                _inFlight.Remove(pr);
                _done++;
                if (result is not null && result.Verdict != CodeVerdict.Checking)
                {
                    _verdicts[result.Verdict] = _verdicts.GetValueOrDefault(result.Verdict) + 1;
                }
                Report(force: true);
            }
        }

        public CodeCompareRunSummary Summary(CodeCompareScope scope, bool stopped, int requests)
        {
            lock (_gate)
            {
                int Count(CodeVerdict v) => _verdicts.GetValueOrDefault(v);
                return new CodeCompareRunSummary(scope, _total,
                    Count(CodeVerdict.Present), Count(CodeVerdict.Missing), Count(CodeVerdict.Partial),
                    Count(CodeVerdict.Unverified), Count(CodeVerdict.Skipped), Count(CodeVerdict.TooLarge), Count(CodeVerdict.Failed),
                    _filesKnown, _watch.Elapsed, stopped, requests);
            }
        }

        /// <summary>Called under the lock. File progress is throttled; a finished pull request always reports.</summary>
        private void Report(bool force)
        {
            if (_progress is null || (!force && _watch.Elapsed - _lastReport < ReportInterval))
            {
                return;
            }
            _lastReport = _watch.Elapsed;

            var partial = _inFlight.Values.Sum(s => s.Total == 0 ? 0 : (double)s.Done / s.Total);
            var fraction = _total == 0 ? 1 : (_done + partial) / _total;
            _progress.Report(new CodeCompareProgress(_done, _total, _filesDone, _filesKnown, fraction, _watch.Elapsed, _current));
        }
    }
}
