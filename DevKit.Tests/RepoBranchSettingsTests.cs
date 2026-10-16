using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The repository settings the Code Merging Sheet remembers: each change is saved at once and is there
/// after a restart, a cleared entry is removed rather than left blank, and the older name-keyed defaults
/// fold into the id-keyed ones exactly once.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class RepoBranchSettingsTests : IDisposable
{
    private const string WebRepoId = "11111111-aaaa-4000-8000-000000000001";
    private const string ApiRepoId = "22222222-bbbb-4000-8000-000000000002";

    public RepoBranchSettingsTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        File.Delete(TestSettingsLocation.SettingsPath);
        File.Delete(TestSettingsLocation.SettingsPath + ".bak");
    }

    private static SettingsService NewService() => new(NullLogger<SettingsService>.Instance);

    private static TfsRepo Repo(string id, string name) => new() { Id = id, Name = name, Project = "Contoso" };

    [Fact]
    public void The_merging_target_and_the_selected_repositories_survive_a_restart()
    {
        var settings = NewService();

        settings.CodeMergingTargetBranch = "release/1.0";
        settings.SetCodeMergingRepoIds(new[] { WebRepoId, ApiRepoId, WebRepoId });

        var restarted = NewService();
        Assert.Equal("release/1.0", restarted.CodeMergingTargetBranch);
        Assert.Equal(new[] { WebRepoId, ApiRepoId }, restarted.CodeMergingRepoIds);
    }

    [Fact]
    public void A_name_keyed_target_is_trimmed_and_a_blank_one_removes_the_entry()
    {
        var settings = NewService();

        settings.SetRepoTargetBranch("Contoso.Web", "  qa  ");
        Assert.Equal("qa", NewService().RepoTargetBranches["Contoso.Web"]);

        settings.SetRepoTargetBranch("Contoso.Web", " ");
        Assert.False(NewService().RepoTargetBranches.ContainsKey("Contoso.Web"));
    }

    [Fact]
    public void Clearing_both_branches_of_a_repository_removes_its_entry()
    {
        var settings = NewService();
        settings.SetRepoQaBranch(WebRepoId, " qa ");
        settings.SetRepoMergingBranch(WebRepoId, "release/1.0");
        Assert.Equal("qa", NewService().GetRepoBranches(WebRepoId).QaBranch);

        settings.SetRepoQaBranch(WebRepoId, "");
        settings.SetRepoMergingBranch(WebRepoId, "");

        Assert.False(NewService().RepoBranches.ContainsKey(WebRepoId));
        Assert.True(settings.GetRepoBranches(WebRepoId).IsEmpty);
    }

    [Fact]
    public void A_blank_repository_id_is_ignored()
    {
        var settings = NewService();

        settings.SetRepoQaBranch(" ", "qa");

        Assert.Empty(settings.RepoBranches);
    }

    [Fact]
    public void Older_defaults_fold_into_the_id_keyed_map_once_and_never_overwrite()
    {
        var settings = NewService();
        settings.SetRepoTargetBranch("Contoso.Web", "qa");
        settings.SetRepoTargetBranch("Contoso.Api", "develop");
        settings.CodeMergingTargetBranch = "release/1.0";
        settings.SetCodeMergingRepoIds(new[] { WebRepoId });
        settings.SetRepoMergingBranch(ApiRepoId, "release/2.0");
        var repos = new[] { Repo(WebRepoId, "Contoso.Web"), Repo(ApiRepoId, "Contoso.Api"), Repo("33333333-cccc-4000-8000-000000000003", "Contoso.Docs") };

        var first = settings.MigrateRepoBranches(repos);
        var second = settings.MigrateRepoBranches(repos);

        Assert.True(first);
        Assert.False(second);
        var reloaded = NewService();
        Assert.Equal(("qa", "release/1.0"), (reloaded.GetRepoBranches(WebRepoId).QaBranch, reloaded.GetRepoBranches(WebRepoId).MergingBranch));
        Assert.Equal(("", "release/2.0"), (reloaded.GetRepoBranches(ApiRepoId).QaBranch, reloaded.GetRepoBranches(ApiRepoId).MergingBranch));
        Assert.Equal(2, reloaded.RepoBranches.Count);
    }

    [Fact]
    public void Folding_with_no_repositories_changes_nothing()
    {
        Assert.False(NewService().MigrateRepoBranches(Array.Empty<TfsRepo>()));
        Assert.False(File.Exists(TestSettingsLocation.SettingsPath));
    }
}
