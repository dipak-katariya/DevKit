using System.Globalization;
using System.Net;
using System.Text;

namespace DevKit.Web.Models;

/// <summary>
/// The team's merge-tracking sheet row for a pull request, as Copy details puts it on the clipboard: the sheet's 19
/// columns in its order, filled only where DevKit knows the value (DATE, REQ/CHR TFS ID, Pull Request, Branch Name and
/// Commit IDs), so pasting into the first cell of an empty row fills that row and leaves the rest blank. It goes out
/// twice: as tab-separated text for any program, and as an HTML table that keeps the two links for Excel.
/// </summary>
public static class MergingSheetRows
{
    /// <summary>
    /// Name, DATE, REQ/CHR TFS ID, BUG, Environment Status, Need to merge, PT W, Migration Tool, CG Merge Done, QA Publish
    /// Request, Pull Request, W, QA (Need to publish)(Agents), REDIS K, SCRIPT ASD/CSD, QA Publish Request By, Branch
    /// Name, Commit IDs, Additional details.
    /// </summary>
    public const int ColumnCount = 19;

    public const int DateColumn = 1;
    public const int RequirementColumn = 2;
    public const int PullRequestColumn = 10;
    public const int BranchColumn = 16;
    public const int CommitsColumn = 17;

    /// <summary>
    /// The date as it goes out: year first, which Excel reads as the same day whatever the pasting PC's date order.
    /// A day-first PC would read "11/5/2026" as 11 May.
    /// </summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>How the sheet shows a date once Excel has it: 11/16/2026, with slashes whatever the PC uses.</summary>
    private const string DateCellStyle = "mso-number-format:'m\\/d\\/yyyy'";

    /// <summary>Keeps a cell's value as written: no number, date or formula read into it.</summary>
    private const string TextCellStyle = "mso-number-format:'\\@'";

    /// <summary>Excel shows ##### in place of a text-formatted cell longer than this, so a longer one stays General.</summary>
    private const int MaxTextCellLength = 255;

    /// <summary>Between rows of the text, as Excel writes them.</summary>
    private const string RowBreak = "\r\n";

    /// <summary>A line break that keeps Excel in the same cell, for the commit list.</summary>
    private const string CellLineBreak = "<br style=\"mso-data-placement:same-cell\">";

    // Excel reads a pasted cell that starts with one of these as a formula, so text from TFS could run one.
    private static readonly char[] FormulaStarts = { '=', '+', '-', '@' };

    /// <summary>
    /// One sheet row: its cells, and the links of the REQ/CHR TFS ID and Pull Request cells. It always has
    /// <see cref="ColumnCount"/> cells, so the text and the HTML copy hold the same columns.
    /// </summary>
    public sealed record Row(IReadOnlyList<string> Cells, string RequirementUrl, string PullRequestUrl)
    {
        /// <summary>The row's <see cref="ColumnCount"/> cells, in the sheet's order.</summary>
        public IReadOnlyList<string> Cells { get; } = FullWidth(Cells);

        private static IReadOnlyList<string> FullWidth(IReadOnlyList<string> cells)
        {
            ArgumentNullException.ThrowIfNull(cells);
            if (cells.Count != ColumnCount)
            {
                throw new ArgumentException($"A sheet row has {ColumnCount} cells, not {cells.Count}.", nameof(cells));
            }

            return cells;
        }
    }

    /// <summary>
    /// One row per pull request of <paramref name="workItem"/> in <paramref name="pullRequests"/>, in their order.
    /// Abandoned ones are left out: nothing of theirs reaches a branch. A pull request without
    /// <see cref="HasOwnCommits"/> gets an empty Commit IDs cell, which the caller has to explain.
    /// </summary>
    /// <param name="requirementName">The work item as REQ/CHR TFS ID shows it: "TEAM : id : title".</param>
    /// <param name="zone">
    /// The team's time zone, in which DATE is the day the pull request was merged. Pass the zone the team keeps the
    /// sheet in (India, as <c>TfsApiService</c> uses for its audit dates), not <see cref="TimeZoneInfo.Local"/>: in
    /// Blazor Server that is the DevKit server's zone, not the zone of the person copying.
    /// </param>
    public static List<Row> For(
        RequirementMergingRow workItem, IEnumerable<MergingPullRequest> pullRequests, string requirementName, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(pullRequests);
        ArgumentNullException.ThrowIfNull(zone);
        return pullRequests
            .Where(pr => !pr.IsAbandoned)
            .Select(pr => new Row(CellsOf(pr, OneLine(requirementName), zone), workItem.WorkItemUrl, pr.Url))
            .ToList();
    }

    /// <summary>The pull request's own commits, oldest first: merges from another branch and rebase commits are left out.</summary>
    public static IEnumerable<MergingCommit> OwnCommits(MergingPullRequest pr) =>
        pr.Commits.Where(commit => !commit.IsMergeFromCommit && !commit.IsRebaseCommit);

    /// <summary>
    /// Whether the pull request has a commit of its own to list. False when every commit read was a merge from another
    /// branch or a rebase, or none was read: its Commit IDs cell is then empty and does not mean nothing needs merging.
    /// </summary>
    public static bool HasOwnCommits(MergingPullRequest pr)
    {
        ArgumentNullException.ThrowIfNull(pr);
        return OwnCommits(pr).Any();
    }

    /// <summary>
    /// The rows as tab-separated text. A cell holding a line break or a quote is quoted, as Excel expects, and one
    /// that would start a formula opens with an apostrophe, which Excel keeps as plain text and does not show.
    /// </summary>
    public static string ToText(IEnumerable<Row> rows) =>
        string.Join(RowBreak, rows.Select(row => string.Join('\t', row.Cells.Select(TextCell))));

    /// <summary>
    /// The rows as an HTML table for Excel, with the work item and the pull request linked. The date carries the
    /// sheet's date format; a filled cell is marked as text where that keeps it as written (see <see cref="CellStyle"/>);
    /// an empty cell carries no format, so whatever the team types there later is read as usual.
    /// </summary>
    public static string ToHtml(IEnumerable<Row> rows)
    {
        var html = new StringBuilder("<table style=\"border-collapse:collapse;font-family:Calibri,Arial,sans-serif;font-size:11pt\">");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            for (var column = 0; column < ColumnCount; column++)
            {
                html.Append(HtmlCell(row, column));
            }

            html.Append("</tr>");
        }

        return html.Append("</table>").ToString();
    }

    private static string[] CellsOf(MergingPullRequest pr, string requirementName, TimeZoneInfo zone)
    {
        var cells = Enumerable.Repeat("", ColumnCount).ToArray();
        cells[DateColumn] = pr.IsCompleted && pr.ClosedDate is { } closed
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(closed, DateTimeKind.Utc), zone).ToString(DateFormat, CultureInfo.InvariantCulture)
            : "";
        cells[RequirementColumn] = requirementName;
        cells[PullRequestColumn] = OneLine($"Pull Request {pr.PullRequestId}: {pr.Title}");
        cells[BranchColumn] = OneLine(pr.SourceBranch);
        cells[CommitsColumn] = string.Join('\n', OwnCommits(pr).Select(commit => OneLine(commit.CommitId)));
        return cells;
    }

    /// <summary>A value from TFS on one line, with no tab to shift the columns after it.</summary>
    private static string OneLine(string? value) =>
        (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    private static string TextCell(string value)
    {
        var text = StartsLikeFormula(value) ? "'" + value : value;
        return text.Contains('"') || text.Contains('\n')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static string HtmlCell(Row row, int column)
    {
        var text = row.Cells[column];
        var link = column switch
        {
            RequirementColumn => row.RequirementUrl,
            PullRequestColumn => row.PullRequestUrl,
            _ => ""
        };
        var body = text.Length > 0 && IsWebAddress(link)
            ? $"<a href=\"{WebUtility.HtmlEncode(link)}\">{Encode(text)}</a>"
            : Encode(text);
        var style = column == DateColumn && text.Length > 0 ? DateCellStyle : CellStyle(text);
        return style is null ? $"<td>{body}</td>" : $"<td style=\"{style}\">{body}</td>";
    }

    /// <summary>
    /// The text format, where it keeps a value as written and Excel can still show it: a value that would start a
    /// formula always; otherwise a single line no longer than <see cref="MaxTextCellLength"/>. A list of commits over
    /// several lines can never be read as a number or a date, and an empty cell is left for the sheet's own format.
    /// </summary>
    private static string? CellStyle(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (StartsLikeFormula(text))
        {
            return TextCellStyle;
        }

        return text.Length <= MaxTextCellLength && !text.Contains('\n') ? TextCellStyle : null;
    }

    private static bool StartsLikeFormula(string text) => text.Length > 0 && Array.IndexOf(FormulaStarts, text[0]) >= 0;

    private static string Encode(string text) => WebUtility.HtmlEncode(text).Replace("\n", CellLineBreak);

    private static bool IsWebAddress(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
