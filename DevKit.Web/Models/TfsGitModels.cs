namespace DevKit.Web.Models;

/// <summary>The branches of a pull request, which is all Branch Cleanup needs to keep a branch with open work.</summary>
public class TfsPullRequest
{
    public int PullRequestId { get; set; }
    public string SourceRefName { get; set; } = "";
    public string TargetRefName { get; set; } = "";
}

/// <summary>
/// The branches a repository's active pull requests merge from or into. Truncated when there were more
/// active pull requests than are read, so a branch missing from the set is not known to be free.
/// </summary>
public sealed record ActivePullRequestBranches(IReadOnlySet<string> Branches, bool Truncated);

/// <summary>A commit's author or committer as TFS reports it.</summary>
public class TfsCommitPerson
{
    public string? Name { get; set; }
    public DateTime? Date { get; set; }
}
