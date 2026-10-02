using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Git object ids of one file in the four versions a comparison reads. Null: absent there.</summary>
/// <param name="Base">QA just before the pull request merged.</param>
/// <param name="After">QA right after it merged — the pull request's own result.</param>
/// <param name="Destination">The merging branch now.</param>
/// <param name="Qa">QA now, which may carry later changes on top of this pull request.</param>
/// <param name="QaKnown">
/// False when QA's current version could not be read, or there is no separate QA to read (the pull
/// request merged straight into the merging branch). A null <paramref name="Qa"/> then means "unknown",
/// not "deleted on QA", and must never excuse a file the branch lacks.
/// </param>
public sealed record BlobIds(string? Base, string? After, string? Destination, string? Qa, bool QaKnown = true);

/// <summary>A file's text in the same four versions. Null: the file does not exist in that version.</summary>
public sealed record FileVersions(string? Base, string After, string Destination, string? Qa);

/// <summary>What the line-level check concluded about one file.</summary>
public sealed record FileAnalysis(
    FileCodeStatus Status,
    string Reason,
    IReadOnlyList<HunkCheck> Problems,
    int HunkCount,
    int HunksPresent,
    bool Approximate);

/// <summary>
/// Decides whether a pull request's change to a file is on the merging branch.
///
/// The change is the diff the pull request made on QA (before → after). Each block of it is looked for
/// on the branch the way <c>git apply</c> looks for where a patch goes: the block as it reads after the
/// pull request, with a few unchanged lines around it, is searched for as a contiguous run. Not found,
/// but the block as it read before the pull request is, means the change is missing. The surrounding
/// context is narrowed step by step so an unrelated edit next to a block does not hide it.
///
/// Finding text is only evidence when it is new. A block or line counts as there only if the branch has
/// more copies of it than the file had before the pull request, and a removed line counts as gone only if
/// the branch has no more copies than the pull request left. Each copy on the branch can vouch for one
/// block only, so two identical blocks are not both satisfied by one. A common line that exists elsewhere
/// in the file proves nothing, and is not taken as proof.
///
/// A block whose own lines QA changed again later — a fix on top of a fix — is also checked against QA's
/// current version of them, so a branch that took both changes is not flagged for the first one.
/// </summary>
public static partial class CodePresenceAnalyzer
{
    private const int MaxContext = 3;
    private const int MaxStoredProblems = 50;
    private const int MaxPreviewLines = 40;

    /// <summary>
    /// The verdict when comparing object ids settles it — identical content has an identical id — or
    /// null when the file differs from every reference version and needs comparing line by line.
    /// </summary>
    public static (FileCodeStatus Status, string Reason)? ClassifyByObjectId(FileChangeKind change, BlobIds ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.After is null ? ForDeletion(ids) : ForChange(change, ids);
    }

    public static FileAnalysis Analyze(FileVersions files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var interner = new LineInterner();
        var before = interner.Map(files.Base ?? "");
        var after = interner.Map(files.After);
        var diff = LineDiff.Compute(before.Ids, after.Ids);
        var destination = interner.Map(files.Destination);
        var qa = files.Qa is null ? null : interner.Map(files.Qa);
        var scope = new HunkScope(interner, before, after, destination, diff.Hunks, qa);

        var problems = new List<HunkCheck>();
        int judged = 0, present = 0, missing = 0;
        var viaQa = false;
        for (var i = 0; i < diff.Hunks.Count; i++)
        {
            // A block of nothing but braces and punctuation matches almost anywhere, so it can prove
            // neither presence nor absence. It sits out of the verdict rather than tilting it.
            if (scope.IsTrivial(i))
            {
                continue;
            }

            judged++;
            var finding = scope.Find(i);
            if (finding.IsPresent)
            {
                present++;
                viaQa |= finding.Status == HunkStatus.MatchesQa;
                continue;
            }

            if (finding.Status == HunkStatus.Missing)
            {
                missing++;
            }
            if (problems.Count < MaxStoredProblems)
            {
                problems.Add(scope.Build(i, finding));
            }
        }

        if (judged == 0)
        {
            return new FileAnalysis(FileCodeStatus.Present,
                "Only whitespace, blank lines or punctuation changed, so there is no code to look for.",
                Array.Empty<HunkCheck>(), 0, 0, diff.Approximate);
        }

        var approximate = diff.Approximate || scope.Approximate;
        var (status, reason) = Conclude(judged, present, missing, viaQa);
        if (approximate)
        {
            reason += " The versions differ so much that the comparison is approximate.";
        }
        return new FileAnalysis(status, reason, problems, judged, present, approximate);
    }

    private static (FileCodeStatus, string) ForDeletion(BlobIds ids)
    {
        if (ids.Destination is null)
        {
            return (FileCodeStatus.Present, "Deleted on the branch too.");
        }
        if (Same(ids.Destination, ids.Qa))
        {
            return (FileCodeStatus.MatchesQa, "QA has this file again, and the branch matches QA's version.");
        }
        return Same(ids.Destination, ids.Base)
            ? (FileCodeStatus.Missing, "This pull request deleted the file, but the branch still has it.")
            : (FileCodeStatus.Missing, "This pull request deleted the file, but the branch still has it, with other changes.");
    }

    private static (FileCodeStatus, string)? ForChange(FileChangeKind change, BlobIds ids)
    {
        if (ids.Destination is null)
        {
            if (ids.QaKnown && ids.Qa is null)
            {
                // Gone from QA at this path too — deleted later, or moved. Moved code may well be missing
                // from the branch, so this is left for a person rather than counted either way.
                return (FileCodeStatus.Unverified,
                    "Neither QA nor the branch has this file at this path any more. If QA deleted it later, nothing is missing; " +
                    "if QA moved it, check that its new location reached the branch.");
            }
            return (FileCodeStatus.Missing,
                change == FileChangeKind.Add ? "This new file is not on the branch." : "The branch doesn't have this file.");
        }
        if (Same(ids.Destination, ids.After))
        {
            return (FileCodeStatus.Present, "Identical to QA right after this pull request.");
        }
        if (Same(ids.Destination, ids.Qa))
        {
            return (FileCodeStatus.MatchesQa, Same(ids.Qa, ids.Base)
                ? "QA reverted this pull request's change later, and the branch matches QA."
                : "Identical to QA's current version, which also has later changes.");
        }
        return Same(ids.Destination, ids.Base)
            ? (FileCodeStatus.Missing, "The branch still has the version from before this pull request.")
            : null;
    }

    private static bool Same(string? a, string? b) =>
        a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static (FileCodeStatus, string) Conclude(int total, int present, int missing, bool viaQa)
    {
        if (present == total)
        {
            var all = total == 1 ? "The change is on the branch." : $"All {total} changes are on the branch.";
            return (FileCodeStatus.Present, viaQa ? all + " Some read as QA has them now, after later changes." : all);
        }
        if (missing == total)
        {
            return (FileCodeStatus.Missing,
                total == 1 ? "The change is not on the branch." : $"None of the {total} changes are on the branch.");
        }

        var altered = total - present - missing;
        return (FileCodeStatus.Partial,
            $"{present} of {total} changes are on the branch; {missing} missing, {altered} changed.");
    }
}
