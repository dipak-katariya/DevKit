using System.Text.RegularExpressions;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Branch Creator names branches with this formula and Branch Delete reads them back, so the formula
/// must produce exactly what Branch Creator always produced — pinned here against the code it replaced.
/// </summary>
public class BranchNamingTests
{
    /// <summary>Branch Creator's naming code as it was before the formula moved, kept verbatim.</summary>
    private static class LegacyBranchCreator
    {
        public static string Build(string team, string sprint, string id, string title)
        {
            var sprintPart = sprint.Length > 0 ? NormSprint(sprint) : "no_sprint";
            var titlePart = NormTitle(title);
            if (titlePart.Length > 60) titlePart = titlePart[..60];

            var generated = $"{AreaShort(team)}/{sprintPart}/{id}_{titlePart}";
            return generated.Length > 100 ? generated[..100].TrimEnd('_', '/') : generated;
        }

        private static string NormTitle(string s)
        {
            var cleaned = Regex.Replace(s, @"[^a-zA-Z0-9\s]", "");
            cleaned = Regex.Replace(cleaned, @"\s+", "_");
            return cleaned.ToLowerInvariant().Trim('_');
        }

        private static string NormSprint(string s)
        {
            var norm = Regex.Replace(s, @"[\s\-\.]+", "_").Trim('_');
            return Regex.Replace(norm, @"^(PI)_(\d)", "$1$2", RegexOptions.IgnoreCase);
        }

        public static string AreaShort(string name)
        {
            var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "TEAM";
            return words.Length == 1 ? words[0].ToUpperInvariant()[..Math.Min(12, words[0].Length)] : string.Concat(words.Select(w => w[0])).ToUpperInvariant();
        }
    }

    public static IEnumerable<object[]> Names() => new[]
    {
        new object[] { "FXCPMGR", "PI-2026-3_3", "1538999", "FX to CPFOIA Migration - Allow migration to inuse CPFOIA squish commit" },
        new object[] { "FX to CPFOIA Migration", "PI 2026.5_2", "1561246", "FOIA request folders are not rolled back" },
        new object[] { "team", "", "42", "No sprint yet" },
        new object[] { "", "PI-2026-5_1", "7", "Empty team" },
        new object[] { "SUPERLONGTEAMNAME", "Sprint 12", "99", "A title with — dashes, commas & symbols!" },
        new object[] { "FXCPMGR", "PI-2026-3_3", "123456789", new string('x', 120) },
        new object[] { "a-b_c d", "pi_2026-1", "5", "   leading and trailing   " },
        new object[] { "FXCPMGR", "PI-2026-3_3", "1", "" }
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Build_produces_exactly_what_branch_creator_produced_before(string team, string sprint, string id, string title)
    {
        Assert.Equal(LegacyBranchCreator.Build(team, sprint, id, title), BranchNaming.Build(team, sprint, id, title));
    }

    [Theory]
    [InlineData("FXCPMGR")]
    [InlineData("FX to CPFOIA Migration")]
    [InlineData("")]
    [InlineData("SUPERLONGTEAMNAME")]
    public void The_team_segment_matches_the_old_prefix(string team)
    {
        Assert.Equal(LegacyBranchCreator.AreaShort(team), BranchNaming.TeamSegment(team));
    }

    [Fact]
    public void A_real_branch_reads_back_as_expected()
    {
        var name = BranchNaming.Build("FXCPMGR", "PI-2026-3_3", "1538999", "FX to CPFOIA Migration - Allow migration");

        Assert.Equal("FXCPMGR/PI2026_3_3/1538999_fx_to_cpfoia_migration_allow_migration", name);
    }

    [Fact]
    public void A_long_title_is_cut_to_sixty_characters()
    {
        var name = BranchNaming.Build("FXCPMGR", "PI-2026-3_3", "1538999", "FX to CPFOIA Migration - Allow migration to inuse CPFOIA squish commit");

        Assert.Equal("FXCPMGR/PI2026_3_3/1538999_fx_to_cpfoia_migration_allow_migration_to_inuse_cpfoia_squis", name);
    }

    [Theory]
    [InlineData("FXCPMGR/PI2026_3_3/1538999_fx_to_cpfoia", "FXCPMGR", "PI2026_3_3", 1538999, "fx_to_cpfoia")]
    [InlineData("FXCPMGR/PI2026_3_3/1561246", "FXCPMGR", "PI2026_3_3", 1561246, "")]
    [InlineData("TEAM/no_sprint/42_no_sprint_yet", "TEAM", "no_sprint", 42, "no_sprint_yet")]
    public void Parse_reads_a_formula_name_into_its_parts(string branch, string team, string sprint, int id, string title)
    {
        var parts = BranchNaming.Parse(branch);

        Assert.NotNull(parts);
        Assert.Equal(new BranchNameParts(team, sprint, id, title), parts);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/FXCPMGR_26.4.2.x")]
    [InlineData("users/dipak/feature")]
    [InlineData("FXCPMGR/PI2026_3_3/abc_title")]
    [InlineData("FXCPMGR/PI2026_3_3/0_zero_id")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_rejects_names_that_do_not_follow_the_formula(string? branch)
    {
        Assert.Null(BranchNaming.Parse(branch));
    }

    [Fact]
    public void Parse_refuses_absurdly_long_input_instead_of_matching_it()
    {
        Assert.Null(BranchNaming.Parse("FXCPMGR/PI2026_3_3/1_" + new string('a', 5000)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_built_name_parses_back_to_its_work_item(string team, string sprint, string id, string title)
    {
        var parts = BranchNaming.Parse(BranchNaming.Build(team, sprint, id, title));

        Assert.NotNull(parts);
        Assert.Equal(int.Parse(id), parts.WorkItemId);
        Assert.Equal(BranchNaming.TeamSegment(team), parts.Team);
    }

    [Theory]
    [InlineData("FXCPMGR/PI2026_3_3/1538999_title", "FXCPMGR", true)]
    [InlineData("feature/fxcpmgr-fix", "FXCPMGR", true)]
    [InlineData("hotfix/FXCPMGR.patch", "FXCPMGR", true)]
    [InlineData("feature/wolfxcpmgr", "FXCPMGR", false)]
    [InlineData("qa-regression", "QA", true)]
    [InlineData("aqua/branch", "QA", false)]
    [InlineData("FXCPMGR/PI2026_3_3/1_title", "", false)]
    [InlineData("", "FXCPMGR", false)]
    public void The_team_counts_only_as_a_whole_word_of_the_branch(string branch, string team, bool expected)
    {
        Assert.Equal(expected, BranchNaming.MentionsTeam(branch, team));
    }
}
