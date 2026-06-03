namespace DevKit.Web.Models;

public class TfsPullRequest
{
    public int PullRequestId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "active";
    public string SourceRefName { get; set; } = "";
    public string TargetRefName { get; set; } = "";
    public DateTime? CreationDate { get; set; }
    public TfsPrAuthor? CreatedBy { get; set; }
    public string SourceBranch => SourceRefName.Replace("refs/heads/", "");
    public string TargetBranch => TargetRefName.Replace("refs/heads/", "");
    public string StatusLabel => Status.ToLower() switch
    {
        "completed" => "MERGED",
        "abandoned" => "ABANDONED",
        _ => "ACTIVE"
    };
    public string StatusCss => Status.ToLower() switch
    {
        "completed" => "pr-completed",
        "abandoned" => "pr-abandoned",
        _ => "pr-active"
    };
    public List<TfsCommit> Commits { get; set; } = new();
}

public class TfsPrAuthor
{
    public string? DisplayName { get; set; }
}

public class TfsCommit
{
    public string CommitId { get; set; } = "";
    public string? Comment { get; set; }
    public TfsCommitPerson? Author { get; set; }
    public TfsCommitPerson? Committer { get; set; }
    public string ShortSha => CommitId.Length >= 8 ? CommitId[..8] : CommitId;
    public string FirstLine => (Comment ?? "").Split('\n')[0];
    public string AuthorName => Author?.Name ?? "";
    public DateTime? AuthorDate => Author?.Date ?? Committer?.Date;
}

public class TfsCommitPerson
{
    public string? Name { get; set; }
    public DateTime? Date { get; set; }
}

public class RepoPrData
{
    public TfsRepo Repo { get; set; } = new();
    public List<TfsPullRequest> Prs { get; set; } = new();
}

public enum CherryPickStatus
{
    None,
    Done,
    Exists,
    Conflict,
    Resolving,
    Resolved,
    Error
}

public class CherryPickResult
{
    public CherryPickStatus Status { get; set; }
    public string Output { get; set; } = "";
    public string ErrorOutput { get; set; } = "";
    public string Branch { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class CherryPickHistoryEntry
{
    public CherryPickStatus Status { get; set; }
    public string Branch { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
