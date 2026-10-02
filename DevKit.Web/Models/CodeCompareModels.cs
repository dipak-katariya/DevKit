namespace DevKit.Web.Models;

/// <summary>
/// How much one code comparison covers. The scope also sets the size limits, so a pull request
/// too large for the sprint-wide run is skipped there with a pointer to its own check, which
/// allows far more.
/// </summary>
public enum CodeCompareScope
{
    Sprint,
    WorkItem,
    PullRequest
}

/// <summary>A pull request's code, compared with its repository's merging branch.</summary>
public enum CodeVerdict
{
    /// <summary>A comparison is running for this pull request.</summary>
    Checking,

    /// <summary>Every change the pull request made is on the merging branch.</summary>
    Present,

    /// <summary>None of it is.</summary>
    Missing,

    /// <summary>Some of it is; the rest is missing or was changed, typically while resolving a conflict.</summary>
    Partial,

    /// <summary>Nothing was found missing, but some files could not be compared.</summary>
    Unverified,

    /// <summary>Not compared, and nothing to do about it: not completed, abandoned, or no merging branch.</summary>
    Skipped,

    /// <summary>Changes more files than this scope compares — needs a check of this pull request on its own.</summary>
    TooLarge,

    /// <summary>The comparison itself failed; running it again may succeed.</summary>
    Failed
}

public enum FileChangeKind
{
    Add,
    Edit,
    Delete,
    Rename
}

public enum FileCodeStatus
{
    /// <summary>The pull request's change to this file is on the merging branch.</summary>
    Present,

    /// <summary>The branch has QA's current version of the file, which changed again after this pull request.</summary>
    MatchesQa,

    Missing,
    Partial,

    /// <summary>The file differs, but it is binary or too large to compare line by line.</summary>
    Unverified,

    Error
}

public enum HunkStatus
{
    Present,

    /// <summary>The pull request's lines are there, but the code around them differs.</summary>
    PresentAdjusted,

    /// <summary>QA changed these lines again later, and the branch has QA's current version.</summary>
    MatchesQa,

    Missing,

    /// <summary>Some of the lines are there and some are not — usually a conflict resolved by hand.</summary>
    Altered
}

public enum CodeLineKind
{
    Context,
    Added,
    Removed
}

/// <summary>
/// One line of a change, shown for review. <see cref="InDestination"/> says whether the line exists in
/// the branch's version of the file; it is null for lines too common to say (braces, punctuation).
/// </summary>
public readonly record struct CodeLine(CodeLineKind Kind, int LineNumber, string Text, bool? InDestination);

/// <summary>One block of a pull request's change to a file, and whether the merging branch has it.</summary>
public sealed class HunkCheck
{
    public HunkStatus Status { get; init; }
    public string Note { get; init; } = "";

    /// <summary>Where the block starts in the file as the pull request left it on QA (1-based).</summary>
    public int QaLine { get; init; }

    /// <summary>Where it was found on the branch — or where the old code still is. Null when nowhere.</summary>
    public int? DestinationLine { get; init; }

    public int Added { get; init; }
    public int Removed { get; init; }
    public IReadOnlyList<CodeLine> Lines { get; init; } = Array.Empty<CodeLine>();

    /// <summary>Lines of the block left out of <see cref="Lines"/> to keep the page light.</summary>
    public int HiddenLines { get; init; }

    public bool IsPresent => Status is HunkStatus.Present or HunkStatus.PresentAdjusted or HunkStatus.MatchesQa;
}

/// <summary>One file a pull request changed, and whether the merging branch has that change.</summary>
public sealed record FileCodeCheck
{
    public string Path { get; init; } = "";

    /// <summary>The path before a rename; null otherwise.</summary>
    public string? OriginalPath { get; init; }

    public FileChangeKind Change { get; init; }
    public FileCodeStatus Status { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>The blocks that need attention. Blocks found on the branch are counted but not kept.</summary>
    public IReadOnlyList<HunkCheck> Hunks { get; init; } = Array.Empty<HunkCheck>();

    public int HunkCount { get; init; }
    public int HunksPresent { get; init; }

    /// <summary>The line diff hit its time budget, so the blocks are coarser than usual.</summary>
    public bool Approximate { get; init; }

    /// <summary>Set when the line detail was asked for and could not be produced.</summary>
    public string DetailError { get; init; } = "";

    // Git object ids of the four versions, kept so the line detail can be produced on demand.
    public string? BaseBlobId { get; init; }
    public string? AfterBlobId { get; init; }
    public string? DestinationBlobId { get; init; }
    public string? QaBlobId { get; init; }

    public bool IsPresent => Status is FileCodeStatus.Present or FileCodeStatus.MatchesQa;

    /// <summary>
    /// Whether the line detail can still be produced: the verdict came from comparing object ids, so
    /// no lines were read. A deleted file has no lines of its own to show.
    /// </summary>
    public bool CanExplain =>
        Hunks.Count == 0 && AfterBlobId is not null
        && Status is FileCodeStatus.Missing or FileCodeStatus.Partial;
}

/// <summary>The result of comparing one pull request's code with its merging branch.</summary>
public sealed record PrCodeCheck
{
    public CodeVerdict Verdict { get; init; }
    public string Summary { get; init; } = "";
    public CodeCompareScope Scope { get; init; }
    public IReadOnlyList<FileCodeCheck> Files { get; init; } = Array.Empty<FileCodeCheck>();
    public string DestinationBranch { get; init; } = "";

    /// <summary>The branch commit the comparison read, so a later push cannot make the result ambiguous.</summary>
    public string DestinationCommit { get; init; } = "";

    public DateTime CheckedAt { get; init; } = DateTime.Now;

    /// <summary>True when the comparison reached an answer about the code, rather than none.</summary>
    public bool IsConclusive => Verdict is CodeVerdict.Present or CodeVerdict.Missing or CodeVerdict.Partial;

    public bool NeedsAttention =>
        Verdict is CodeVerdict.Missing or CodeVerdict.Partial or CodeVerdict.Unverified
            or CodeVerdict.Failed or CodeVerdict.TooLarge;

    public int FilesPresent => Files.Count(f => f.IsPresent);
    public int FilesMissing => Files.Count(f => f.Status == FileCodeStatus.Missing);
    public int FilesPartial => Files.Count(f => f.Status == FileCodeStatus.Partial);
    public int FilesUnverified => Files.Count(f => f.Status is FileCodeStatus.Unverified or FileCodeStatus.Error);

    public static PrCodeCheck InProgress(CodeCompareScope scope, string branch) =>
        new() { Verdict = CodeVerdict.Checking, Scope = scope, DestinationBranch = branch, Summary = "Comparing the code…" };

    public static PrCodeCheck NotCompared(CodeVerdict verdict, string summary, CodeCompareScope scope, string branch) =>
        new() { Verdict = verdict, Scope = scope, DestinationBranch = branch, Summary = summary };

    /// <summary>The verdict for a finished comparison, read off its files.</summary>
    public static PrCodeCheck FromFiles(
        IReadOnlyList<FileCodeCheck> files, CodeCompareScope scope, string branch, string destinationCommit)
    {
        var tally = Tally.Of(files);
        return new PrCodeCheck
        {
            Verdict = tally.Verdict,
            Summary = tally.Describe(branch),
            Scope = scope,
            Files = files,
            DestinationBranch = branch,
            DestinationCommit = destinationCommit
        };
    }

    /// <summary>A copy with one file replaced — how line detail produced on demand is attached.</summary>
    public PrCodeCheck WithFile(FileCodeCheck replacement) =>
        this with { Files = Files.Select(f => f.Path == replacement.Path ? replacement : f).ToList() };

    private readonly record struct Tally(int Total, int Present, int Missing, int Partial)
    {
        private int Unverified => Total - Present - Missing - Partial;

        public static Tally Of(IReadOnlyList<FileCodeCheck> files) => new(
            files.Count,
            files.Count(f => f.IsPresent),
            files.Count(f => f.Status == FileCodeStatus.Missing),
            files.Count(f => f.Status == FileCodeStatus.Partial));

        public CodeVerdict Verdict
        {
            get
            {
                if (Missing == 0 && Partial == 0)
                {
                    return Unverified > 0 ? CodeVerdict.Unverified : CodeVerdict.Present;
                }

                // Nothing on the branch plus a file that could not be compared is still missing: the
                // one unknown cannot make the pull request partly merged.
                return Present == 0 && Partial == 0 ? CodeVerdict.Missing : CodeVerdict.Partial;
            }
        }

        public string Describe(string branch)
        {
            if (Total == 0)
            {
                return "This pull request changed no files.";
            }
            if (Present == Total)
            {
                return $"All {Files(Total)} have this pull request's change on {branch}.";
            }
            if (Missing == Total)
            {
                return $"None of the {Files(Total)} have this pull request's change on {branch}.";
            }

            var parts = new[] { (Missing, "missing"), (Partial, "partly merged"), (Unverified, "not compared") }
                .Where(p => p.Item1 > 0)
                .Select(p => $"{p.Item1} {p.Item2}");
            return $"{Present} of {Files(Total)} match {branch} · " + string.Join(" · ", parts) + ".";
        }

        private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
    }
}

/// <summary>Where a running comparison has got to, for the progress bar.</summary>
public readonly record struct CodeCompareProgress(
    int PullRequestsDone,
    int PullRequestsTotal,
    int FilesDone,
    int FilesKnown,
    double Fraction,
    TimeSpan Elapsed,
    string Current)
{
    /// <summary>Below this much progress an estimate would be noise, so none is given.</summary>
    private const double MinFractionForEstimate = 0.03;

    public int Percent => (int)Math.Round(Math.Clamp(Fraction, 0, 1) * 100);

    public TimeSpan? Remaining =>
        Fraction < MinFractionForEstimate
            ? null
            : TimeSpan.FromTicks((long)(Elapsed.Ticks * (1 - Math.Min(Fraction, 1)) / Fraction));
}

/// <summary>What one comparison run did, for the status line and the log.</summary>
public sealed record CodeCompareRunSummary(
    CodeCompareScope Scope,
    int PullRequests,
    int Present,
    int Missing,
    int Partial,
    int Unverified,
    int Skipped,
    int TooLarge,
    int Failed,
    int Files,
    TimeSpan Elapsed,
    bool Stopped,
    int Requests)
{
    public int NeedsAttention => Missing + Partial + Unverified + TooLarge + Failed;
}

/// <summary>
/// Size limits for one scope. The sprint-wide run stays quick by skipping outliers; a single pull
/// request's own check allows far more, which is what it is for.
/// </summary>
public sealed record CodeCompareLimits(int MaxFilesPerPullRequest, long MaxTextBytes)
{
    private const long MegaByte = 1024 * 1024;

    public long MaxTextMegabytes => MaxTextBytes / MegaByte;

    public static CodeCompareLimits For(CodeCompareScope scope) => scope switch
    {
        CodeCompareScope.PullRequest => new CodeCompareLimits(20_000, 10 * MegaByte),
        CodeCompareScope.WorkItem => new CodeCompareLimits(2_000, 4 * MegaByte),
        _ => new CodeCompareLimits(500, 2 * MegaByte)
    };
}
