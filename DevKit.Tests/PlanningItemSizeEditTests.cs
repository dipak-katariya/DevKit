using System.Globalization;
using DevKit.Web.Models;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What the Size Check editor starts from and which typed text it will write to TFS. A number input reads and
/// reports its value in invariant format whatever the user's locale, so the server's culture must not matter.
/// </summary>
public class PlanningItemSizeEditTests
{
    private const string CommaDecimalCulture = "de-DE";

    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(8.0, "8")]
    [InlineData(null, "")]
    public void The_editor_opens_on_the_current_size_as_a_number_input_reads_it(double? size, string expected)
    {
        var item = new PlanningItem { WorkItem = new WorkItem { Effort = size } };

        InCulture(CommaDecimalCulture, item.BeginSizeEdit);

        Assert.Equal(expected, item.EditingSize);
    }

    [Theory]
    [InlineData("8", 8.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("0", 0.0)]
    public void The_size_saved_is_the_text_typed(string typed, double expected)
    {
        var item = new PlanningItem { EditingSize = typed };

        Assert.Equal(expected, InCulture(CommaDecimalCulture, () => item.EditedSize));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Text_that_is_not_a_size_of_zero_or_more_is_never_saved(string? typed)
    {
        var item = new PlanningItem { EditingSize = typed };

        Assert.Null(item.EditedSize);
    }

    [Fact]
    public void A_size_opened_and_saved_unchanged_is_the_same_size()
    {
        var item = new PlanningItem { WorkItem = new WorkItem { Effort = 2.5 } };

        var saved = InCulture(CommaDecimalCulture, () =>
        {
            item.BeginSizeEdit();
            return item.EditedSize;
        });

        Assert.Equal(2.5, saved);
    }

    private static void InCulture(string name, Action action) => InCulture(name, () =>
    {
        action();
        return true;
    });

    private static T InCulture<T>(string name, Func<T> read)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try
        {
            return read();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
