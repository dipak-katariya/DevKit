using System.Text;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Every code verdict stands on the line diff: a wrong edit script would put a pull request's change
/// in the wrong place and report code missing — or present — by mistake. The randomised test holds the
/// implementation to a brute-force LCS, so correctness and minimality are proven, not sampled.
/// </summary>
public class LineDiffTests
{
    [Fact]
    public void Identical_sequences_have_no_hunks()
    {
        var result = LineDiff.Compute(new[] { 1, 2, 3 }, new[] { 1, 2, 3 });

        Assert.Empty(result.Hunks);
        Assert.False(result.Approximate);
    }

    [Fact]
    public void An_insertion_is_one_hunk_that_removes_nothing()
    {
        var hunk = Assert.Single(LineDiff.Compute(new[] { 1, 2, 3 }, new[] { 1, 9, 9, 2, 3 }).Hunks);

        Assert.Equal(new DiffHunk(1, 1, 1, 3), hunk);
    }

    [Fact]
    public void A_deletion_is_one_hunk_that_adds_nothing()
    {
        var hunk = Assert.Single(LineDiff.Compute(new[] { 1, 2, 3, 4 }, new[] { 1, 4 }).Hunks);

        Assert.Equal(new DiffHunk(1, 3, 1, 1), hunk);
    }

    [Fact]
    public void Separate_edits_stay_separate_hunks()
    {
        var hunks = LineDiff.Compute(new[] { 1, 2, 3, 4, 5 }, new[] { 1, 7, 3, 4, 8 }).Hunks;

        Assert.Equal(new[] { new DiffHunk(1, 2, 1, 2), new DiffHunk(4, 5, 4, 5) }, hunks);
    }

    [Fact]
    public void Empty_on_either_side_is_one_wholesale_hunk()
    {
        Assert.Equal(new DiffHunk(0, 0, 0, 2), Assert.Single(LineDiff.Compute(Array.Empty<int>(), new[] { 1, 2 }).Hunks));
        Assert.Equal(new DiffHunk(0, 2, 0, 0), Assert.Single(LineDiff.Compute(new[] { 1, 2 }, Array.Empty<int>()).Hunks));
        Assert.Empty(LineDiff.Compute(Array.Empty<int>(), Array.Empty<int>()).Hunks);
    }

    [Fact]
    public void Every_diff_is_a_valid_minimal_edit_script()
    {
        var random = new Random(20261002);
        for (var trial = 0; trial < 3000; trial++)
        {
            var alphabet = random.Next(1, 7);
            var a = RandomSequence(random, random.Next(0, 45), alphabet);
            var b = trial % 2 == 0 ? Mutate(random, a, alphabet) : RandomSequence(random, random.Next(0, 45), alphabet);

            var result = LineDiff.Compute(a, b);

            AssertValidScript(a, b, result.Hunks);
            Assert.Equal(LcsLength(a, b), a.Length - result.Hunks.Sum(h => h.OldLength));
        }
    }

    [Fact]
    public void An_exhausted_budget_still_gives_a_valid_script_and_says_so()
    {
        var random = new Random(7);
        var a = RandomSequence(random, 400, 3);
        var b = RandomSequence(random, 400, 3);

        var result = LineDiff.Compute(a, b, budget: 50);

        Assert.True(result.Approximate);
        AssertValidScript(a, b, result.Hunks);
    }

    [Fact]
    public void A_large_mostly_equal_file_diffs_quickly_and_exactly()
    {
        var a = Enumerable.Range(0, 50_000).ToArray();
        var b = a.Where(i => i % 997 != 0).Concat(new[] { -1, -2 }).ToArray();

        var result = LineDiff.Compute(a, b);

        Assert.False(result.Approximate);
        AssertValidScript(a, b, result.Hunks);
        Assert.Equal(LcsLengthOfSubsequence(a, b), a.Length - result.Hunks.Sum(h => h.OldLength));
    }

    [Fact]
    public void A_block_is_found_nearest_where_it_is_expected()
    {
        var index = new LineIndex(new[] { 5, 1, 2, 9, 5, 1, 2, 9 });

        Assert.Equal(0, index.Find(new[] { 5, 1, 2 }, near: 1));
        Assert.Equal(4, index.Find(new[] { 5, 1, 2 }, near: 6));
    }

    [Fact]
    public void A_block_must_be_contiguous_and_whole()
    {
        var index = new LineIndex(new[] { 1, 2, 3, 4 });

        Assert.Equal(-1, index.Find(new[] { 1, 3 }, near: 0));
        Assert.Equal(-1, index.Find(new[] { 3, 4, 5 }, near: 0));
        Assert.Equal(-1, index.Find(Array.Empty<int>(), near: 0));
        Assert.Equal(2, index.Find(new[] { 3, 4 }, near: 0));
    }

    [Fact]
    public void Whitespace_and_blank_lines_do_not_change_what_a_line_is()
    {
        var interner = new LineInterner();

        var tidy = interner.Map("if (x == 1)\n{\n    Run(a, b);\n}\n");
        var messy = interner.Map("  if(x==1)\r\n\r\n{\r\n\tRun( a,b );   \r\n\r\n}");

        Assert.Equal(tidy.Ids, messy.Ids);
    }

    [Fact]
    public void Line_numbers_and_text_survive_for_display()
    {
        var sequence = new LineInterner().Map("first\n\n   \n  second line  \n");

        Assert.Equal(new[] { 1, 4 }, sequence.Numbers);
        Assert.Equal(new[] { "first", "  second line" }, sequence.Texts);
        Assert.Equal(5, sequence.NumberAt(2));
    }

    [Fact]
    public void Braces_and_punctuation_are_not_significant()
    {
        var interner = new LineInterner();
        var ids = interner.Map("}\n);\nreturn;\nx\n").Ids;

        Assert.False(interner.IsSignificant(ids[0]));
        Assert.False(interner.IsSignificant(ids[1]));
        Assert.True(interner.IsSignificant(ids[2]));
        Assert.False(interner.IsSignificant(ids[3]));
        Assert.False(interner.AnySignificant(new[] { ids[0], ids[1] }));
    }

    [Fact]
    public void Binary_content_is_recognised_and_text_encodings_are_decoded()
    {
        Assert.Null(TextBlob.Decode(new byte[] { 0x47, 0x49, 0x46, 0x00, 0x01 }));
        Assert.Equal("héllo", TextBlob.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("héllo")).ToArray()));
        Assert.Equal("hi", TextBlob.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hi")).ToArray()));
        Assert.Equal("", TextBlob.Decode(Array.Empty<byte>()));
    }

    private static int[] RandomSequence(Random random, int length, int alphabet) =>
        Enumerable.Range(0, length).Select(_ => random.Next(alphabet)).ToArray();

    private static int[] Mutate(Random random, int[] source, int alphabet)
    {
        var list = source.ToList();
        for (var edits = random.Next(0, 6); edits > 0; edits--)
        {
            var at = list.Count == 0 ? 0 : random.Next(list.Count);
            switch (random.Next(3))
            {
                case 0:
                    list.Insert(at, random.Next(alphabet));
                    break;
                case 1 when list.Count > 0:
                    list.RemoveAt(at);
                    break;
                default:
                    if (list.Count > 0)
                    {
                        list[at] = random.Next(alphabet);
                    }
                    break;
            }
        }
        return list.ToArray();
    }

    /// <summary>The hunks are ordered, disjoint and non-empty, and everything between them is equal.</summary>
    private static void AssertValidScript(int[] a, int[] b, IReadOnlyList<DiffHunk> hunks)
    {
        int oldAt = 0, newAt = 0;
        foreach (var h in hunks)
        {
            Assert.True(h.OldStart >= oldAt && h.NewStart >= newAt, "hunks out of order");
            Assert.True(h.OldLength + h.NewLength > 0, "empty hunk");
            Assert.Equal(h.OldStart - oldAt, h.NewStart - newAt);
            Assert.True(a.AsSpan(oldAt, h.OldStart - oldAt).SequenceEqual(b.AsSpan(newAt, h.NewStart - newAt)));
            oldAt = h.OldEnd;
            newAt = h.NewEnd;
        }
        Assert.Equal(a.Length - oldAt, b.Length - newAt);
        Assert.True(a.AsSpan(oldAt).SequenceEqual(b.AsSpan(newAt)));
    }

    private static int LcsLength(int[] a, int[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }
        return dp[0, 0];
    }

    /// <summary>LCS of two sequences with distinct values, where it is the longest increasing run of matches.</summary>
    private static int LcsLengthOfSubsequence(int[] a, int[] b)
    {
        var inB = b.ToHashSet();
        return a.Count(inB.Contains);
    }
}
