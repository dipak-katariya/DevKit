using System.Text.RegularExpressions;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// A branch's recent history, indexed so a commit can be classified as present, reverted, or absent.
/// Code usually reaches a release branch by cherry-pick, which produces a different SHA for the same
/// change, so a commit is looked up by more than its SHA.
///
/// The evidence is weighed by what it proves. A SHA, or the "(cherry picked from commit …)" line that
/// <c>git cherry-pick -x</c> writes, names the commit itself. The author's email and author time survive
/// a cherry-pick and a rebase — even when the message was changed while fixing a conflict — and one
/// person writing two different commits in the same second is vanishingly rare. A message proves least:
/// "fix typo" is written every week, so a message counts only when it is long enough to be specific,
/// occurs once on the branch, and no two commits being checked share it.
///
/// A plain "is this SHA on the branch" lookup is also not enough because a revert does not remove the
/// original commit, it adds a second one that undoes it. The original stays findable forever and would
/// read as merged long after its code is gone. This index records the <em>latest</em> state of each key
/// instead, so a revert flips it to reverted and a revert of that revert flips it back.
/// </summary>
public sealed class BranchHistoryIndex
{
    /// <summary>Subjects shorter than this are too generic to identify a commit on their own.</summary>
    public const int MinDistinctiveSubjectLength = 20;

    private const string RevertPrefix = "Revert \"";
    private const string ShaKey = "sha:";
    private const string AuthorKeyPrefix = "author:";
    private const string SubjectKey = "subject:";

    /// <summary>Git writes this trailer naming the commit being undone.</summary>
    private static readonly Regex RevertsTrailer = new(
        @"This reverts commit ([0-9a-fA-F]{7,40})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>git cherry-pick -x</c> writes this trailer naming the commit that was copied.</summary>
    private static readonly Regex CherryPickTrailer = new(
        @"cherry picked from commit ([0-9a-fA-F]{7,40})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// key (SHA, author or subject) -> the verdict, stamped with the position in history that set it.
    /// The stamp is what makes the index correct: one commit is looked up under several keys, and they
    /// can disagree — a directly merged commit leaves its SHA present while a later revert marks its
    /// author reverted. Taking whichever key was written last resolves that, and resolves
    /// revert-then-reapply with it.
    /// </summary>
    private readonly Dictionary<string, Evidence> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Author of each branch commit, so a revert naming a cherry-picked copy also reverts the original.</summary>
    private readonly Dictionary<string, string> _authorBySha = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> _subjectCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sharedSubjects = new(StringComparer.OrdinalIgnoreCase);
    private int _seq;

    /// <summary>How many branch commits went into this index, for the accuracy note.</summary>
    public int ScannedCommits { get; set; }

    /// <summary>True when the scan stopped at its cap rather than at the end of the history.</summary>
    public bool HitWindowCap { get; set; }

    /// <summary>How far back the scan reached, for the accuracy note. Null when unbounded.</summary>
    public DateTime? ScannedFrom { get; set; }

    /// <summary>The key a commit's author is matched by — email and author time to the second — or null.</summary>
    public static string? AuthorKey(string? email, DateTime? authorDate) =>
        string.IsNullOrWhiteSpace(email) || authorDate is null
            ? null
            : $"{email.Trim().ToLowerInvariant()}|{authorDate.Value.ToUniversalTime():yyyyMMddTHHmmss}";

    /// <summary>
    /// Records one branch commit. Callers must add <b>oldest first</b>: later commits overwrite earlier
    /// ones, which is what makes "revert, then re-apply" resolve to present.
    /// </summary>
    public void Add(string commitId, string message, string? authorKey = null)
    {
        _seq++;
        var sha = (commitId ?? "").Trim();
        var subject = SubjectOf(message);

        // The commit itself is on the branch whatever it does to earlier ones.
        Set(ShaKey, sha, true, CommitMatch.Sha);
        Set(AuthorKeyPrefix, authorKey, true, CommitMatch.Author);
        if (sha.Length > 0 && !string.IsNullOrWhiteSpace(authorKey))
        {
            _authorBySha[sha] = authorKey;
        }
        if (subject.Length > 0)
        {
            _subjectCount[subject] = _subjectCount.GetValueOrDefault(subject) + 1;
            Set(SubjectKey, subject, true, CommitMatch.Message);
        }

        // Nesting alternates: one Revert undoes, a Revert of that Revert re-applies.
        var (inner, depth) = Unwrap(subject);
        if (depth > 0)
        {
            Set(SubjectKey, inner, depth % 2 == 0, CommitMatch.Message);
        }

        foreach (Match m in RevertsTrailer.Matches(message ?? ""))
        {
            var reverted = m.Groups[1].Value;
            Set(ShaKey, reverted, false, CommitMatch.Sha);
            if (_authorBySha.TryGetValue(reverted, out var revertedAuthor))
            {
                Set(AuthorKeyPrefix, revertedAuthor, false, CommitMatch.Author);
            }
        }
        foreach (Match m in CherryPickTrailer.Matches(message ?? ""))
        {
            Set(ShaKey, m.Groups[1].Value, true, CommitMatch.CherryPick);
        }
    }

    /// <summary>
    /// Marks a message as shared by more than one of the commits being checked. Such a message cannot
    /// tell them apart, so it no longer counts as evidence for either.
    /// </summary>
    public void MarkShared(string message)
    {
        var subject = SubjectOf(message);
        if (subject.Length > 0)
        {
            _sharedSubjects.Add(subject);
        }
    }

    /// <summary>
    /// Whether the commit's change is on the branch now, was reverted off it, or was never there, and
    /// what that rests on. When keys disagree, the one written later in history wins.
    /// </summary>
    public CommitLookup Classify(string commitId, string message, string? authorKey = null)
    {
        var subject = SubjectOf(message);
        Evidence? best = null;
        Consider(ref best, StateOf(ShaKey, commitId));
        Consider(ref best, StateOf(AuthorKeyPrefix, authorKey));
        if (IsDistinctive(subject))
        {
            Consider(ref best, StateOf(SubjectKey, subject));
        }

        if (best is { } found)
        {
            return new CommitLookup(found.Present ? CommitPresence.Present : CommitPresence.Reverted, found.Match);
        }
        return StateOf(SubjectKey, subject) is not null
            ? new CommitLookup(CommitPresence.Absent, CommitMatch.AmbiguousMessage)
            : new CommitLookup(CommitPresence.Absent, CommitMatch.None);
    }

    /// <summary>Ties keep the earlier candidate, so on equal footing a SHA beats an author and an author beats a message.</summary>
    private static void Consider(ref Evidence? best, Evidence? candidate)
    {
        if (candidate is { } c && (best is null || c.Seq > best.Value.Seq))
        {
            best = c;
        }
    }

    private bool IsDistinctive(string subject) =>
        subject.Length >= MinDistinctiveSubjectLength
        && !_sharedSubjects.Contains(subject)
        && _subjectCount.GetValueOrDefault(subject) == 1;

    private void Set(string kind, string? value, bool present, CommitMatch match)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        _state[kind + value.Trim()] = new Evidence(present, _seq, match);
    }

    private Evidence? StateOf(string kind, string? value) =>
        !string.IsNullOrWhiteSpace(value) && _state.TryGetValue(kind + value.Trim(), out var v) ? v : null;

    /// <summary>
    /// Strips the <c>Revert "…"</c> wrappers off a subject line, returning the innermost subject and how
    /// many wrappers were removed.
    /// </summary>
    private static (string Subject, int Depth) Unwrap(string subject)
    {
        var depth = 0;
        while (subject.StartsWith(RevertPrefix, StringComparison.OrdinalIgnoreCase)
               && subject.EndsWith("\"", StringComparison.Ordinal)
               && subject.Length > RevertPrefix.Length)
        {
            subject = subject[RevertPrefix.Length..^1].Trim();
            depth++;
        }
        return (subject, depth);
    }

    /// <summary>A message's first line, trimmed — what a cherry-pick keeps and a revert quotes.</summary>
    public static string SubjectOf(string? message)
    {
        var text = (message ?? "").Trim();
        var breakAt = text.IndexOfAny(new[] { '\r', '\n' });
        return (breakAt < 0 ? text : text[..breakAt]).Trim();
    }

    private readonly record struct Evidence(bool Present, int Seq, CommitMatch Match);
}

/// <summary>Where a pull-request commit stands relative to a branch, and what that rests on.</summary>
public readonly record struct CommitLookup(CommitPresence Presence, CommitMatch Match);

/// <summary>Where a pull-request commit stands relative to a branch.</summary>
public enum CommitPresence
{
    /// <summary>Never reached the branch.</summary>
    Absent,

    /// <summary>Reached the branch and is still there.</summary>
    Present,

    /// <summary>Reached the branch and was later reverted off it.</summary>
    Reverted
}
