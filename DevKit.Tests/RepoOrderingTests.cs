using DevKit.Web.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

public class RepoOrderingTests
{
    private static List<TfsRepo> SampleRepos() => new()
    {
        new TfsRepo { Id = "r1", Name = "Zeta", Project = "Alpha" },
        new TfsRepo { Id = "r2", Name = "Beta", Project = "Alpha" },
        new TfsRepo { Id = "r3", Name = "Gamma", Project = "Bravo" },
        new TfsRepo { Id = "r4", Name = "Delta", Project = "Bravo" }
    };

    [Fact]
    public void Pinned_repositories_come_first_in_the_order_they_were_pinned()
    {
        var ordered = RepoOrdering.Prioritize(SampleRepos(), new[] { "r3", "r1" });

        Assert.Equal(new[] { "r3", "r1", "r2", "r4" }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void Unpinned_repositories_sort_by_project_then_name()
    {
        var ordered = RepoOrdering.Prioritize(SampleRepos(), Array.Empty<string>());

        Assert.Equal(new[] { "r2", "r1", "r4", "r3" }, ordered.Select(r => r.Id));
    }

    [Fact]
    public void A_pinned_id_that_matches_no_repository_is_ignored()
    {
        var ordered = RepoOrdering.Prioritize(SampleRepos(), new[] { "deleted-repo", "r4" });

        Assert.Equal("r4", ordered[0].Id);
        Assert.Equal(4, ordered.Count);
    }

    [Fact]
    public void A_null_repository_list_yields_an_empty_result_rather_than_throwing()
    {
        Assert.Empty(RepoOrdering.Prioritize(null, new[] { "r1" }));
    }

    [Fact]
    public void Blank_and_duplicate_pins_are_dropped_so_no_two_repositories_share_a_rank()
    {
        var ranks = RepoOrdering.BuildRanks(new[] { "r1", "r1", "  ", "", "r2" });

        Assert.Equal(2, ranks.Count);
        Assert.Equal(0, ranks["r1"]);
        Assert.Equal(1, ranks["r2"]);
    }

    [Fact]
    public void Rank_lookup_ignores_case_because_ids_come_from_a_hand_editable_file()
    {
        var ranks = RepoOrdering.BuildRanks(new[] { "AbC" });

        Assert.Equal(0, RepoOrdering.RankOf(ranks, "abc"));
        Assert.Equal(RepoOrdering.Unpinned, RepoOrdering.RankOf(ranks, "other"));
    }

    [Fact]
    public void Unknown_and_empty_ids_rank_last()
    {
        var ranks = RepoOrdering.BuildRanks(new[] { "r1" });

        Assert.Equal(RepoOrdering.Unpinned, RepoOrdering.RankOf(ranks, ""));
        Assert.Equal(RepoOrdering.Unpinned, RepoOrdering.RankOf(ranks, null));
    }
}

public class RepoSelectTests
{
    private static List<TfsRepo> SampleRepos() => new()
    {
        new TfsRepo { Id = "r1", Name = "Zeta", Project = "Alpha" },
        new TfsRepo { Id = "r2", Name = "Beta", Project = "Alpha" },
        new TfsRepo { Id = "r3", Name = "Gamma", Project = "Bravo" }
    };

    [Fact]
    public void Most_used_group_leads_and_holds_the_pins_in_order()
    {
        var groups = RepoSelect.Groups(SampleRepos(), new[] { "r3", "r1" });

        Assert.Equal(RepoSelect.MostUsedGroupLabel, groups[0].Label);
        Assert.Equal(new[] { "r3", "r1" }, groups[0].Options.Select(o => o.Value));
    }

    [Fact]
    public void A_pinned_repository_is_not_repeated_in_its_project_group()
    {
        // Two rows carrying the same value would both highlight as the current selection.
        var values = RepoSelect.Groups(SampleRepos(), new[] { "r3", "r1" })
            .SelectMany(g => g.Options).Select(o => o.Value).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.Equal(3, values.Count);
    }

    [Fact]
    public void No_most_used_group_appears_when_nothing_is_pinned()
    {
        var groups = RepoSelect.Groups(SampleRepos(), Array.Empty<string>());

        Assert.DoesNotContain(groups, g => g.Label == RepoSelect.MostUsedGroupLabel);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void Option_labels_keep_the_project_dash_name_format_the_pages_used_before()
    {
        var label = RepoSelect.Groups(SampleRepos(), Array.Empty<string>())
            .SelectMany(g => g.Options).First(o => o.Value == "r2").Label;

        Assert.Equal("Alpha — Beta", label);
    }

    [Fact]
    public void Flat_options_are_prioritized_and_labelled_by_repository_name()
    {
        var options = RepoSelect.Options(SampleRepos(), new[] { "r3" });

        Assert.Equal("r3", options[0].Value);
        Assert.Equal("Gamma", options[0].Label);
        Assert.Equal(3, options.Count);
    }
}
