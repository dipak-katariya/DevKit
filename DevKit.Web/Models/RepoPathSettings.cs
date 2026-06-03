namespace DevKit.Web.Models;

public class RepoPathSettings
{
    /// <summary>
    /// Maps TFS repo ID to local filesystem path where the repo is cloned.
    /// e.g. "abc-123" => "D:\Projects\CasepointARA\FX-CPFOIA-Migration"
    /// </summary>
    public Dictionary<string, string> RepoPaths { get; set; } = new();
}
