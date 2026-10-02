using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The commit check: is a pull request's commit — or a copy of it — on the release branch? The cases
/// are the ones that produced wrong answers: a cherry-pick whose message was changed while fixing a
/// conflict (read as missing), and an unrelated commit with a common message (read as merged).
/// </summary>
public class BranchHistoryIndexTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string CopySha = "2222222222222222222222222222222222222222";
    private const string OtherSha = "3333333333333333333333333333333333333333";
    private const string Subject = "FXCPMGR : 1631102 : Excel to CPFOIA Migration - Version Update";

    private static readonly DateTime Authored = new(2026, 9, 14, 10, 30, 15, DateTimeKind.Utc);
    private static readonly string Author = BranchHistoryIndex.AuthorKey("Dev.One@Example.com", Authored)!;

    [Fact]
    public void The_same_commit_is_found_by_its_sha()
    {
        var index = Index((Sha, Subject, null));

        Assert.Equal(new CommitLookup(CommitPresence.Present, CommitMatch.Sha), index.Classify(Sha, Subject));
    }

    [Fact]
    public void A_cherry_pick_reworded_while_fixing_a_conflict_is_found_by_its_author_and_time()
    {
        var index = Index((CopySha, "Merge conflict fixed for release", Author));

        var lookup = index.Classify(Sha, Subject, Author);

        Assert.Equal(new CommitLookup(CommitPresence.Present, CommitMatch.Author), lookup);
    }

    [Fact]
    public void A_cherry_pick_made_with_x_is_found_by_the_commit_it_names()
    {
        var index = Index((CopySha, $"Different words\n\n(cherry picked from commit {Sha})", null));

        Assert.Equal(new CommitLookup(CommitPresence.Present, CommitMatch.CherryPick), index.Classify(Sha, Subject));
    }

    [Fact]
    public void A_common_message_does_not_make_an_unrelated_commit_count()
    {
        var index = Index((OtherSha, "fix typo", null));

        var lookup = index.Classify(Sha, "fix typo");

        Assert.Equal(new CommitLookup(CommitPresence.Absent, CommitMatch.AmbiguousMessage), lookup);
    }

    [Fact]
    public void A_distinctive_message_found_once_still_counts()
    {
        var index = Index((CopySha, Subject, null));

        Assert.Equal(new CommitLookup(CommitPresence.Present, CommitMatch.Message), index.Classify(Sha, Subject));
    }

    [Fact]
    public void A_message_on_two_branch_commits_cannot_say_which_is_this_one()
    {
        var index = Index((CopySha, Subject, null), (OtherSha, Subject, null));

        Assert.Equal(CommitPresence.Absent, index.Classify(Sha, Subject).Presence);
    }

    [Fact]
    public void A_message_two_checked_commits_share_stops_counting()
    {
        var index = Index((CopySha, Subject, null));
        index.MarkShared(Subject);

        Assert.Equal(new CommitLookup(CommitPresence.Absent, CommitMatch.AmbiguousMessage), index.Classify(Sha, Subject));
    }

    [Fact]
    public void A_revert_takes_the_commit_off_and_a_revert_of_the_revert_puts_it_back()
    {
        var reverted = Index(
            (Sha, Subject, null),
            (OtherSha, $"Revert \"{Subject}\"\n\nThis reverts commit {Sha}.", null));
        Assert.Equal(CommitPresence.Reverted, reverted.Classify(Sha, Subject).Presence);

        var reapplied = Index(
            (Sha, Subject, null),
            (OtherSha, $"Revert \"{Subject}\"\n\nThis reverts commit {Sha}.", null),
            ("4444444444444444444444444444444444444444", $"Revert \"Revert \"{Subject}\"\"\n\nThis reverts commit {OtherSha}.", null));
        Assert.Equal(CommitPresence.Present, reapplied.Classify(Sha, Subject).Presence);
    }

    [Fact]
    public void Reverting_a_cherry_picked_copy_takes_the_original_off_too()
    {
        // The revert names the copy's SHA, which the original pull request commit never had.
        var index = Index(
            (CopySha, "Reworded during the merge", Author),
            (OtherSha, $"Revert \"Reworded during the merge\"\n\nThis reverts commit {CopySha}.", null));

        Assert.Equal(CommitPresence.Reverted, index.Classify(Sha, Subject, Author).Presence);
    }

    [Fact]
    public void A_revert_of_a_multi_line_message_is_matched_by_its_subject()
    {
        var index = Index(
            (CopySha, $"{Subject}\n\nLong description of the change.", null),
            (OtherSha, $"Revert \"{Subject}\"\n\nThis reverts commit {CopySha}.", null));

        Assert.Equal(CommitPresence.Reverted, index.Classify(Sha, $"{Subject}\n\nLong description of the change.").Presence);
    }

    [Fact]
    public void A_commit_nobody_copied_is_absent()
    {
        var index = Index((OtherSha, "Something else entirely, long enough to count", Author));

        Assert.Equal(new CommitLookup(CommitPresence.Absent, CommitMatch.None),
            index.Classify(Sha, Subject, BranchHistoryIndex.AuthorKey("dev.one@example.com", Authored.AddSeconds(1))));
    }

    [Fact]
    public void The_author_key_ignores_email_case_and_time_zone_but_not_the_second()
    {
        Assert.Equal(Author, BranchHistoryIndex.AuthorKey("dev.one@EXAMPLE.com", Authored.ToLocalTime()));
        Assert.NotEqual(Author, BranchHistoryIndex.AuthorKey("dev.one@example.com", Authored.AddSeconds(1)));
        Assert.Null(BranchHistoryIndex.AuthorKey("", Authored));
        Assert.Null(BranchHistoryIndex.AuthorKey("dev.one@example.com", null));
    }

    /// <summary>Builds an index from branch commits given oldest first, as the service adds them.</summary>
    private static BranchHistoryIndex Index(params (string Sha, string Message, string? Author)[] commits)
    {
        var index = new BranchHistoryIndex();
        foreach (var (sha, message, author) in commits)
        {
            index.Add(sha, message, author);
        }
        return index;
    }
}
