namespace DevKit.Web.Models;

/// <summary>
/// What Branch Creator says after Check, Create or removing a stale link. Every part is plain text that the page renders
/// through Razor, which encodes it, so nothing a branch name, a repository or a TFS message holds is ever read as markup.
/// </summary>
/// <param name="Tone">ok, warn or bad: the colour of a one-line result.</param>
/// <param name="Message">A one-line result; empty for a created branch, which shows <paramref name="Rows"/>.</param>
/// <param name="Rows">A created branch's details, one labelled row each.</param>
/// <param name="Created">Whether a branch was created, which heads the rows.</param>
public sealed record BranchResult(string Tone, IReadOnlyList<ResultText> Message, IReadOnlyList<ResultRow> Rows, bool Created = false)
{
    /// <summary>A one-line result in <paramref name="tone"/>.</summary>
    public static BranchResult Line(string tone, params ResultText[] message) => new(tone, message, []);

    /// <summary>A created branch, with its details.</summary>
    public static BranchResult CreatedWith(IReadOnlyList<ResultRow> rows) => new("ok", [], rows, Created: true);
}

/// <summary>A run of a result's text; code shows in the code font, as branch names and refs do.</summary>
public readonly record struct ResultText(string Text, bool IsCode = false)
{
    public static ResultText Code(string text) => new(text, IsCode: true);

    public static ResultText Plain(string text) => new(text);
}

/// <summary>A labelled row of a created branch's details; <paramref name="ValueClass"/> styles its value.</summary>
public sealed record ResultRow(string Label, string ValueClass, IReadOnlyList<ResultText> Value);
