using System.Text.RegularExpressions;

namespace DevKit.Web.Services;

/// <summary>The parts of a branch named by the team formula <c>{team}/{sprint}/{workItemId}_{title}</c>.</summary>
public sealed record BranchNameParts(string Team, string Sprint, int WorkItemId, string Title);

/// <summary>
/// The team's branch-naming formula, <c>{team}/{sprint}/{workItemId}_{title}</c>, in one place: Branch
/// Creator builds names with it and Branch Delete reads them back, so the two cannot disagree about
/// what a team branch looks like.
/// </summary>
public static class BranchNaming
{
    public const int MaxLength = 100;
    public const int MaxTitleLength = 60;

    private const int MaxTeamLength = 12;

    /// <summary>Longest name Parse will look at; git refs are far shorter, so anything longer is not ours.</summary>
    private const int MaxParseLength = 1024;

    private const string NoSprint = "no_sprint";
    private const string FallbackTeam = "TEAM";

    // Branch names come from TFS, so every pattern runs under a timeout instead of trusting its input.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex NonWordChars = new(@"[^a-zA-Z0-9\s]", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex SprintSeparators = new(@"[\s\-\.]+", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex PiPrefix = new(@"^(PI)_(\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Formula = new(
        @"^(?<team>[^/]+)/(?<sprint>[^/]+)/(?<id>\d{1,9})(?:_(?<title>.*))?$", RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly char[] TeamWordSeparators = { ' ', '-', '_' };
    private static readonly char[] BranchTokenSeparators = { '/', '-', '_', '.', ' ' };

    /// <summary>The team prefix: one word upper-cased and cut to 12 letters, or the initials of several words.</summary>
    public static string TeamSegment(string? team)
    {
        var words = (team ?? "").Split(TeamWordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return FallbackTeam;
        return words.Length == 1
            ? words[0].ToUpperInvariant()[..Math.Min(MaxTeamLength, words[0].Length)]
            : string.Concat(words.Select(w => w[0])).ToUpperInvariant();
    }

    /// <summary>A sprint as it appears in a branch: separators become underscores and "PI_2026" becomes "PI2026".</summary>
    public static string SprintSegment(string? sprint)
    {
        var normalized = SprintSeparators.Replace(sprint ?? "", "_").Trim('_');
        return PiPrefix.Replace(normalized, "$1$2");
    }

    /// <summary>A title as it appears in a branch: punctuation dropped, lower case, words joined by underscores.</summary>
    public static string TitleSegment(string? title)
    {
        // Each whole run of whitespace collapses to one underscore, so a removed " - " leaves one
        // separator rather than "__".
        var cleaned = NonWordChars.Replace(title ?? "", "");
        cleaned = Whitespace.Replace(cleaned, "_");
        return cleaned.ToLowerInvariant().Trim('_');
    }

    /// <summary>The branch name for a work item. A work item without a sprint gets "no_sprint".</summary>
    public static string Build(string team, string sprint, string workItemId, string title)
    {
        var sprintSegment = string.IsNullOrEmpty(sprint) ? NoSprint : SprintSegment(sprint);
        var titleSegment = TitleSegment(title);
        if (titleSegment.Length > MaxTitleLength) titleSegment = titleSegment[..MaxTitleLength];

        var name = $"{TeamSegment(team)}/{sprintSegment}/{workItemId}_{titleSegment}";
        return name.Length > MaxLength ? name[..MaxLength].TrimEnd('_', '/') : name;
    }

    /// <summary>Reads a branch name back into its parts; null for a name that does not follow the formula.</summary>
    public static BranchNameParts? Parse(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > MaxParseLength) return null;
        try
        {
            var match = Formula.Match(branch);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, out var id) || id <= 0) return null;
            return new BranchNameParts(match.Groups["team"].Value, match.Groups["sprint"].Value, id, match.Groups["title"].Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the team prefix is one of the branch's words — "FXCPMGR/…", "feature/fxcpmgr-fix" — so a
    /// short prefix such as "QA" does not claim every branch that merely contains those letters.
    /// </summary>
    public static bool MentionsTeam(string? branch, string? teamSegment) =>
        !string.IsNullOrWhiteSpace(branch)
        && !string.IsNullOrWhiteSpace(teamSegment)
        && branch.Split(BranchTokenSeparators, StringSplitOptions.RemoveEmptyEntries)
                 .Any(token => string.Equals(token, teamSegment, StringComparison.OrdinalIgnoreCase));
}
