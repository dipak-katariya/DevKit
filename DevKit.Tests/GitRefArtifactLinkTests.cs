using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Branch links are how the Branch Creator learns which branches a work item already has. TFS
/// writes them in more than one shape, and a parser that misses one silently reports "no existing
/// branches" — which reads as "safe to create" when it is not.
/// </summary>
public class GitRefArtifactLinkTests
{
    private const string ProjectGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string RepoGuid = "8a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9";
    private const string Branch = "FXCPMGR/PI2026_5_2/1631102_excel_to_cpfoia_migration_verify";

    [Fact]
    public void Parses_the_percent_encoded_form_tfs_writes()
    {
        var link = GitRefArtifactLink.TryParse(
            $"vstfs:///Git/Ref/{ProjectGuid}/{RepoGuid}/GB{Uri.EscapeDataString(Branch)}");

        Assert.NotNull(link);
        Assert.Equal(Branch, link!.BranchName);
        Assert.Equal(RepoGuid, link.RepositoryId);
        Assert.Equal(ProjectGuid, link.ProjectId);
    }

    [Fact]
    public void Keeps_literal_slashes_when_the_branch_was_not_encoded()
    {
        var link = GitRefArtifactLink.TryParse($"vstfs:///Git/Ref/{ProjectGuid}/{RepoGuid}/GB{Branch}");

        Assert.NotNull(link);
        Assert.Equal(Branch, link!.BranchName);
    }

    [Fact]
    public void Parses_the_form_where_even_the_guid_separators_are_encoded()
    {
        var link = GitRefArtifactLink.TryParse(
            "vstfs:///Git/Ref/" + Uri.EscapeDataString($"{ProjectGuid}/{RepoGuid}/GB{Branch}"));

        Assert.NotNull(link);
        Assert.Equal(Branch, link!.BranchName);
        Assert.Equal(RepoGuid, link.RepositoryId);
    }

    [Fact]
    public void Round_trips_the_link_this_app_writes()
    {
        // Mirrors TfsApiService.LinkWorkItemAsync, so a change to either side fails here first.
        var written = $"vstfs:///Git/Ref/{Uri.EscapeDataString(ProjectGuid)}/" +
                      $"{Uri.EscapeDataString(RepoGuid)}/GB{Uri.EscapeDataString(Branch)}";

        var link = GitRefArtifactLink.TryParse(written);

        Assert.NotNull(link);
        Assert.Equal(Branch, link!.BranchName);
        Assert.Equal(RepoGuid, link.RepositoryId);
    }

    [Fact]
    public void Strips_a_refs_heads_prefix()
    {
        var link = GitRefArtifactLink.TryParse(
            $"vstfs:///Git/Ref/{ProjectGuid}/{RepoGuid}/GB{Uri.EscapeDataString("refs/heads/main")}");

        Assert.Equal("main", link?.BranchName);
    }

    [Theory]
    // Tags and commits share the Git/Ref shape; only GB denotes a branch.
    [InlineData("vstfs:///Git/Ref/{P}/{R}/GTv1.0.0")]
    [InlineData("vstfs:///Git/Ref/{P}/{R}/GCabc123def456")]
    // A pull-request link is a different artifact entirely.
    [InlineData("vstfs:///Git/PullRequestId/{P}/{R}/42")]
    [InlineData("vstfs:///Git/Ref/{P}/{R}/GB")]
    [InlineData("vstfs:///Git/Ref/notaguid/alsonotaguid/GBmain")]
    [InlineData("https://example.com/not-an-artifact-link")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_anything_that_is_not_a_branch_link(string template)
    {
        var url = template.Replace("{P}", ProjectGuid).Replace("{R}", RepoGuid);

        Assert.Null(GitRefArtifactLink.TryParse(url));
    }

    [Fact]
    public void Rejects_null()
    {
        Assert.Null(GitRefArtifactLink.TryParse(null));
    }
}
