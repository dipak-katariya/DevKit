using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>One file of a pull request: its object ids in each version, then the verdict they or its lines give.</summary>
public sealed partial class CodeCompareService
{
    private async Task<FileCodeCheck> CheckFileAsync(FileJob job, CancellationToken ct)
    {
        var change = job.Change;
        if (change.Unsupported is { } why)
        {
            return Result(change, new BlobIds(null, null, null, null), FileCodeStatus.Unverified, why);
        }

        try
        {
            var resolved = await ResolveAsync(job, ct).ConfigureAwait(false);
            var decided = CodePresenceAnalyzer.ClassifyByObjectId(change.Kind, resolved.Ids);
            var check = decided is { } byId
                ? Result(change, resolved.Ids, byId.Status, byId.Reason)
                : await CompareLinesAsync(job, resolved, ct).ConfigureAwait(false);
            return resolved.OldPathStillThere ? FlagLeftoverOldPath(check) : check;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Code compare {RunId}: {Path} in pull request {PrId} ({Repo}) could not be compared",
                job.Parent.RunId, change.Path, job.Parent.Pr.PullRequestId, job.Parent.Pr.RepositoryName);
            return new FileCodeCheck
            {
                Path = change.Path,
                OriginalPath = change.OriginalPath,
                Change = change.Kind,
                Status = FileCodeStatus.Error,
                Reason = $"Couldn't compare this file: {TfsErrors.Describe(ex)}"
            };
        }
    }

    /// <summary>The file's object id in each version, looked up concurrently.</summary>
    private static async Task<ResolvedFile> ResolveAsync(FileJob job, CancellationToken ct)
    {
        var (s, change, reader) = (job.Snapshot, job.Change, job.Parent.Reader);
        Task<BlobRef?> Find(string commit, string path) => reader.FindBlobAsync(s.Project, s.RepoId, commit, path, ct);
        Task<BlobRef?> KnownOr(string? known, string commit, string path) =>
            known is not null ? Task.FromResult<BlobRef?>(new BlobRef(known, null)) : Find(commit, path);
        var none = Task.FromResult<BlobRef?>(null);

        var baseRef = change.Kind == FileChangeKind.Add ? none : KnownOr(change.BaseBlobId, s.BaseCommit, change.OriginalPath ?? change.Path);
        var afterRef = change.Kind == FileChangeKind.Delete ? none : KnownOr(change.AfterBlobId, s.AfterCommit, change.Path);
        var destinationRef = Find(s.Destination, change.Path);
        var qaRef = s.Qa is null ? none : Find(s.Qa, change.Path);
        var oldPathRef = change.OriginalPath is null ? none : Find(s.Destination, change.OriginalPath);
        await Task.WhenAll(baseRef, afterRef, destinationRef, qaRef, oldPathRef).ConfigureAwait(false);

        var destination = await destinationRef.ConfigureAwait(false);
        var qa = await qaRef.ConfigureAwait(false);
        var ids = new BlobIds(
            (await baseRef.ConfigureAwait(false))?.ObjectId,
            (await afterRef.ConfigureAwait(false))?.ObjectId,
            destination?.ObjectId,
            qa?.ObjectId,
            QaKnown: s.Qa is not null);
        return new ResolvedFile(ids, Math.Max(destination?.Size ?? 0, qa?.Size ?? 0),
            await oldPathRef.ConfigureAwait(false) is not null);
    }

    private static async Task<FileCodeCheck> CompareLinesAsync(FileJob job, ResolvedFile resolved, CancellationToken ct)
    {
        var limits = job.Limits;
        if (resolved.LargestKnownSize > limits.MaxTextBytes)
        {
            return Result(job.Change, resolved.Ids, FileCodeStatus.Unverified, TooLargeReason(limits));
        }

        var texts = await ReadVersionsAsync(new BlobSource(job.Parent.Reader, job.Snapshot.Project, job.Snapshot.RepoId), resolved.Ids, limits, ct)
            .ConfigureAwait(false);
        if (texts.Problem is not null)
        {
            return Result(job.Change, resolved.Ids, FileCodeStatus.Unverified, texts.Problem);
        }

        var analysis = CodePresenceAnalyzer.Analyze(texts.Versions!);
        return Result(job.Change, resolved.Ids, analysis.Status, analysis.Reason) with
        {
            Hunks = analysis.Problems,
            HunkCount = analysis.HunkCount,
            HunksPresent = analysis.HunksPresent,
            Approximate = analysis.Approximate
        };
    }

    /// <summary>
    /// The text of each version the line check needs. QA's current version is read only when it differs
    /// from what the pull request left — otherwise there is nothing later on QA to check against.
    /// </summary>
    private static async Task<(FileVersions? Versions, string? Problem)> ReadVersionsAsync(
        BlobSource source, BlobIds ids, CodeCompareLimits limits, CancellationToken ct)
    {
        Task<TextRead> Read(string? blobId) => ReadTextAsync(source, blobId, limits, ct);
        var wantQa = ids.Qa is not null && !string.Equals(ids.Qa, ids.After, StringComparison.OrdinalIgnoreCase);

        var reads = await Task.WhenAll(Read(ids.Base), Read(ids.After), Read(ids.Destination), Read(wantQa ? ids.Qa : null))
            .ConfigureAwait(false);
        var problem = reads.Select(r => r.Problem).FirstOrDefault(p => p is not null);
        if (problem is not null)
        {
            return (null, problem);
        }

        // A file the branch does not have reads as empty, so every line of the change shows as missing.
        return (new FileVersions(reads[0].Text, reads[1].Text ?? "", reads[2].Text ?? "", reads[3].Text), null);
    }

    private static async Task<TextRead> ReadTextAsync(
        BlobSource source, string? blobId, CodeCompareLimits limits, CancellationToken ct)
    {
        if (blobId is null)
        {
            return default;
        }

        var bytes = await source.Reader.ReadBlobAsync(source.Project, source.RepoId, blobId, limits.MaxTextBytes, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return new TextRead(null, TooLargeReason(limits));
        }

        var text = TextBlob.Decode(bytes);
        return text is null
            ? new TextRead(null, "A binary file that differs from QA, so it can't be compared line by line — check it by hand.")
            : new TextRead(text, null);
    }

    private static string TooLargeReason(CodeCompareLimits limits) =>
        $"Larger than {limits.MaxTextMegabytes} MB and different from QA, so it can't be compared line by line here — check it by hand.";

    private static FileCodeCheck Result(FileChange change, BlobIds ids, FileCodeStatus status, string reason) => new()
    {
        Path = change.Path,
        OriginalPath = change.OriginalPath,
        Change = change.Kind,
        Status = status,
        Reason = reason,
        BaseBlobId = ids.Base,
        AfterBlobId = ids.After,
        DestinationBlobId = ids.Destination,
        QaBlobId = ids.Qa
    };

    /// <summary>A rename is only complete once the old path is gone; a branch with both still has unmoved code.</summary>
    private static FileCodeCheck FlagLeftoverOldPath(FileCodeCheck check) => check with
    {
        Status = check.IsPresent ? FileCodeStatus.Partial : check.Status,
        Reason = check.Reason + $" The old file {check.OriginalPath} is still on the branch as well."
    };

    private sealed record FileJob(Snapshot Snapshot, FileChange Change, CodeCompareLimits Limits, PrJob Parent);

    /// <param name="LargestKnownSize">The bigger of the branch and QA versions, when the trees said.</param>
    private sealed record ResolvedFile(BlobIds Ids, long LargestKnownSize, bool OldPathStillThere);

    /// <summary>Where a pull request's file contents are read from.</summary>
    private sealed record BlobSource(GitObjectReader Reader, string Project, string RepoId);

    /// <summary>One version's text; a null <see cref="Text"/> with no problem means the file is absent there.</summary>
    private readonly record struct TextRead(string? Text, string? Problem);
}
