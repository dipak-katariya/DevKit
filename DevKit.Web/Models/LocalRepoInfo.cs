namespace DevKit.Web.Models;

public class LocalRepoInfo
{
    public string FolderName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public string? TfsRepoId { get; set; }
    public string? TfsRepoName { get; set; }
    public string? TfsProject { get; set; }

    /// <summary>
    /// Display label: "Project / RepoName (FolderName)" or just "FolderName" if unmatched.
    /// </summary>
    public string DisplayLabel =>
        !string.IsNullOrEmpty(TfsProject)
            ? (FolderName.Equals(TfsRepoName, StringComparison.OrdinalIgnoreCase)
                ? $"{TfsProject} / {TfsRepoName}"
                : $"{TfsProject} / {TfsRepoName}  [{FolderName}]")
            : FolderName;

    public bool IsMapped => !string.IsNullOrEmpty(TfsRepoId);
}
