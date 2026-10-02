using DevKit.Web.Models;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Whether a pull request's change to one file is on the release branch, decided from the file's text.
/// The cases are the ones that made commit matching unreliable: code that arrived under another message,
/// a conflict resolved by dropping lines, a resolution that reformatted the code, and a later fix on QA
/// on top of the pull request's own lines.
/// </summary>
public class CodePresenceAnalyzerTests
{
    private static readonly string[] Before =
    {
        "using System;",
        "",
        "namespace Demo;",
        "",
        "public class Export",
        "{",
        "    public int Count(int[] items)",
        "    {",
        "        var total = 0;",
        "        foreach (var item in items)",
        "        {",
        "            total += item;",
        "        }",
        "        return total;",
        "    }",
        "",
        "    public string Name => \"export\";",
        "}"
    };

    /// <summary>The pull request: a null guard at the top of Count.</summary>
    private static readonly string[] After = Insert(Before, 8,
        "        if (items is null)",
        "        {",
        "            throw new ArgumentNullException(nameof(items));",
        "        }");

    [Fact]
    public void A_change_on_the_branch_is_present_whatever_else_the_branch_has()
    {
        // The release branch carries its own, unrelated edits around the change.
        var destination = Replace(After, "    public string Name => \"export\";", "    public string Name => \"export-v2\";")
            .Append("// release 26.4").ToArray();

        var result = Analyze(Before, After, destination);

        Assert.Equal(FileCodeStatus.Present, result.Status);
        Assert.Equal(1, result.HunkCount);
        Assert.Equal(1, result.HunksPresent);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void A_branch_that_still_has_the_old_code_is_missing_the_change()
    {
        var destination = Before.Append("// release 26.4").ToArray();

        var result = Analyze(Before, After, destination);

        Assert.Equal(FileCodeStatus.Missing, result.Status);
        var hunk = Assert.Single(result.Problems);
        Assert.Equal(HunkStatus.Missing, hunk.Status);
        Assert.Equal(9, hunk.QaLine);
        Assert.NotNull(hunk.DestinationLine);
        Assert.Equal(4, hunk.Added);
    }

    [Fact]
    public void Lines_dropped_while_resolving_a_conflict_are_caught()
    {
        // The guard is there, but the throw inside it was lost in the conflict.
        var destination = Remove(After, "            throw new ArgumentNullException(nameof(items));");

        var result = Analyze(Before, After, destination);

        Assert.Equal(FileCodeStatus.Partial, result.Status);
        var hunk = Assert.Single(result.Problems);
        Assert.Equal(HunkStatus.Altered, hunk.Status);
        Assert.Contains("1 of 2", hunk.Note);

        var missing = Assert.Single(hunk.Lines, l => l.Kind == CodeLineKind.Added && l.InDestination == false);
        Assert.Contains("ArgumentNullException", missing.Text);
        Assert.Contains(hunk.Lines, l => l.Kind == CodeLineKind.Added && l.InDestination == true);
    }

    [Fact]
    public void A_resolution_that_reformatted_the_code_still_counts_as_present()
    {
        var reformatted = string.Join("\r\n", After)
            .Replace("if (items is null)", "if(items is null)")
            .Replace("    ", "\t")
            .Replace("nameof(items)", "nameof( items )");

        var result = CodePresenceAnalyzer.Analyze(new FileVersions(Text(Before), Text(After), reformatted, null));

        Assert.Equal(FileCodeStatus.Present, result.Status);
    }

    [Fact]
    public void A_later_fix_on_qa_is_accepted_when_the_branch_has_it()
    {
        var qaNow = Replace(After, "            throw new ArgumentNullException(nameof(items));",
            "            throw new ArgumentNullException(nameof(items), \"Items are required.\");");
        var destination = qaNow.Append("// release 26.4").ToArray();

        var result = CodePresenceAnalyzer.Analyze(new FileVersions(Text(Before), Text(After), Text(destination), Text(qaNow)));

        Assert.Equal(FileCodeStatus.Present, result.Status);
        Assert.Contains("QA", result.Reason);
    }

    [Fact]
    public void A_later_fix_on_qa_does_not_excuse_a_branch_with_neither_version()
    {
        var qaNow = Replace(After, "            throw new ArgumentNullException(nameof(items));",
            "            throw new ArgumentNullException(nameof(items), \"Items are required.\");");
        var destination = Replace(After, "            throw new ArgumentNullException(nameof(items));",
            "            return 0;");

        var result = CodePresenceAnalyzer.Analyze(new FileVersions(Text(Before), Text(After), Text(destination), Text(qaNow)));

        Assert.NotEqual(FileCodeStatus.Present, result.Status);
        Assert.Contains("neither version", Assert.Single(result.Problems).Note);
    }

    [Fact]
    public void Lines_a_pull_request_removed_must_be_gone_from_the_branch()
    {
        var removed = Remove(Before, "    public string Name => \"export\";");

        Assert.Equal(FileCodeStatus.Missing, Analyze(Before, removed, Before).Status);
        Assert.Equal(FileCodeStatus.Present, Analyze(Before, removed, removed).Status);
    }

    [Fact]
    public void A_new_file_with_a_method_lost_on_the_branch_is_partly_merged()
    {
        var added = new[]
        {
            "public static class Totals",
            "{",
            "    public static int Sum(int a, int b) => a + b;",
            "    public static int Product(int a, int b) => a * b;",
            "}"
        };
        var destination = Remove(added, "    public static int Product(int a, int b) => a * b;");

        var result = CodePresenceAnalyzer.Analyze(new FileVersions(null, Text(added), Text(destination), null));

        Assert.Equal(FileCodeStatus.Partial, result.Status);
        Assert.Contains("2 of 3", Assert.Single(result.Problems).Note);
    }

    [Fact]
    public void A_change_of_only_whitespace_has_nothing_to_look_for()
    {
        var reindented = Before.Select(l => l.Replace("    ", "  ")).ToArray();

        var result = Analyze(Before, reindented, Before);

        Assert.Equal(FileCodeStatus.Present, result.Status);
        Assert.Equal(0, result.HunkCount);
    }

    [Fact]
    public void A_change_of_only_punctuation_is_not_flagged()
    {
        var after = Insert(Before, 14, "    ;");

        var result = Analyze(Before, after, Before);

        Assert.Equal(FileCodeStatus.Present, result.Status);
    }

    [Fact]
    public void Each_block_is_judged_on_its_own()
    {
        var after = Replace(After, "    public string Name => \"export\";", "    public string Name => \"export-all\";");
        // The branch took the null guard but not the rename.
        var destination = Replace(after, "    public string Name => \"export-all\";", "    public string Name => \"export\";");

        var result = Analyze(Before, after, destination);

        Assert.Equal(FileCodeStatus.Partial, result.Status);
        Assert.Equal(2, result.HunkCount);
        Assert.Equal(1, result.HunksPresent);
        Assert.Equal(HunkStatus.Missing, Assert.Single(result.Problems).Status);
    }

    [Fact]
    public void An_identical_branch_file_is_settled_by_its_object_id()
    {
        var verdict = CodePresenceAnalyzer.ClassifyByObjectId(FileChangeKind.Edit, new BlobIds("b", "a", "a", "a"));

        Assert.Equal(FileCodeStatus.Present, verdict?.Status);
    }

    [Theory]
    // deletion: branch deleted it too / still has the old file / has QA's re-added copy / has another version
    [InlineData(FileChangeKind.Delete, "b", null, null, null, FileCodeStatus.Present)]
    [InlineData(FileChangeKind.Delete, "b", null, "b", null, FileCodeStatus.Missing)]
    [InlineData(FileChangeKind.Delete, "b", null, "q", "q", FileCodeStatus.MatchesQa)]
    [InlineData(FileChangeKind.Delete, "b", null, "x", null, FileCodeStatus.Missing)]
    // new file: not on the branch
    [InlineData(FileChangeKind.Add, null, "a", null, "a", FileCodeStatus.Missing)]
    // edit: gone from QA and the branch (deleted or moved — left for a person) / branch lacks it / matches QA now / reverted on QA
    [InlineData(FileChangeKind.Edit, "b", "a", null, null, FileCodeStatus.Unverified)]
    [InlineData(FileChangeKind.Edit, "b", "a", null, "a", FileCodeStatus.Missing)]
    [InlineData(FileChangeKind.Edit, "b", "a", "q", "q", FileCodeStatus.MatchesQa)]
    [InlineData(FileChangeKind.Edit, "b", "a", "b", "b", FileCodeStatus.MatchesQa)]
    [InlineData(FileChangeKind.Edit, "b", "a", "b", "a", FileCodeStatus.Missing)]
    public void Object_ids_settle_the_clear_cases(
        FileChangeKind change, string? before, string? after, string? destination, string? qa, FileCodeStatus expected)
    {
        var verdict = CodePresenceAnalyzer.ClassifyByObjectId(change, new BlobIds(before, after, destination, qa));

        Assert.Equal(expected, verdict?.Status);
    }

    [Fact]
    public void A_qa_version_that_could_not_be_read_never_excuses_a_file_the_branch_lacks()
    {
        // Known to be gone from QA, a file the branch lacks is left for a person (QA may have moved it);
        // merely unknown, it is simply missing. Neither counts as present.
        Assert.Equal(FileCodeStatus.Unverified,
            CodePresenceAnalyzer.ClassifyByObjectId(FileChangeKind.Edit, new BlobIds("b", "a", null, null))?.Status);
        Assert.Equal(FileCodeStatus.Missing,
            CodePresenceAnalyzer.ClassifyByObjectId(FileChangeKind.Edit, new BlobIds("b", "a", null, null, QaKnown: false))?.Status);
    }

    [Fact]
    public void A_common_line_that_exists_elsewhere_does_not_prove_the_change()
    {
        string[] before =
        {
            "public string First()", "{", "var label = Build(1);", "var title = Build(3);", "}",
            "public string Other()", "{", "return null;", "}"
        };
        var after = Insert(before, 3, "return null;");
        // The branch changed the lines around the insertion and never took it; its only "return null;"
        // is the one Other() always had.
        var destination = Replace(Replace(before, "var label = Build(1);", "var label = Build(11);"),
            "var title = Build(3);", "var title = Build(33);");

        var result = Analyze(before, after, destination);

        Assert.Equal(FileCodeStatus.Missing, result.Status);
    }

    [Fact]
    public void A_removal_with_some_lines_left_behind_is_not_present()
    {
        string[] before = { "Start();", "Prepare();", "Foo1(alpha);", "Foo2(beta);", "Finish();", "Close();" };
        var after = Remove(Remove(before, "Foo1(alpha);"), "Foo2(beta);");
        // A conflict resolution that edited one of the removed lines and kept the other.
        var destination = Replace(before, "Foo1(alpha);", "Foo1(alpha, gamma);");

        var result = Analyze(before, after, destination);

        Assert.NotEqual(FileCodeStatus.Present, result.Status);
        Assert.Contains("1 of 2", Assert.Single(result.Problems).Note);
    }

    [Fact]
    public void A_later_qa_edit_next_to_a_deletion_does_not_excuse_the_deletion_being_undone()
    {
        string[] before = { "Start();", "Prepare();", "Foo1(alpha);", "Foo2(beta);", "Baz1(one);", "Baz2(two);" };
        var after = Remove(Remove(before, "Foo1(alpha);"), "Foo2(beta);");
        var qaNow = Replace(after, "Baz1(one);", "Baz1(one, uno);");
        // The branch has QA's later edit but still the code the pull request deleted.
        var destination = Replace(before, "Baz1(one);", "Baz1(one, uno);");

        var result = CodePresenceAnalyzer.Analyze(new FileVersions(Text(before), Text(after), Text(destination), Text(qaNow)));

        Assert.Equal(FileCodeStatus.Missing, result.Status);
    }

    [Fact]
    public void Identical_blocks_are_each_checked_against_their_own_copy()
    {
        string[] before = { "void A()", "{", "Log(entry);", "}", "void B()", "{", "Log(entry);", "}" };
        var after = Insert(Insert(before, 7, "Validate(entry);"), 3, "Validate(entry);");
        // Only the first handler got the new line; the second was lost in the merge.
        var destination = Insert(before, 3, "Validate(entry);");

        var result = Analyze(before, after, destination);

        Assert.Equal(FileCodeStatus.Partial, result.Status);
        Assert.Equal(2, result.HunkCount);
        Assert.Equal(1, result.HunksPresent);
    }

    [Fact]
    public void A_file_that_differs_from_every_version_needs_its_lines_compared()
    {
        Assert.Null(CodePresenceAnalyzer.ClassifyByObjectId(FileChangeKind.Edit, new BlobIds("b", "a", "x", "q")));
    }

    private static FileAnalysis Analyze(string[] before, string[] after, string[] destination) =>
        CodePresenceAnalyzer.Analyze(new FileVersions(Text(before), Text(after), Text(destination), null));

    private static string Text(IEnumerable<string> lines) => string.Join("\n", lines) + "\n";

    private static string[] Insert(string[] lines, int at, params string[] added) =>
        lines.Take(at).Concat(added).Concat(lines.Skip(at)).ToArray();

    private static string[] Remove(string[] lines, string line)
    {
        Assert.Contains(line, lines);
        return lines.Where(l => l != line).ToArray();
    }

    private static string[] Replace(string[] lines, string from, string to)
    {
        Assert.Contains(from, lines);
        return lines.Select(l => l == from ? to : l).ToArray();
    }
}
