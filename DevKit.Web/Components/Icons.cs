namespace DevKit.Web.Components;

/// <summary>
/// Inner markup for Feather-style line icons (24×24, stroke = currentColor).
/// Rendered through <see cref="Icon"/>, which supplies the wrapping &lt;svg&gt; element.
/// Values are static trusted constants, so they are safe to emit as MarkupString.
/// </summary>
public static class Icons
{
    public const string Dashboard =
        "<rect x='3' y='3' width='7' height='7' rx='1'/><rect x='14' y='3' width='7' height='7' rx='1'/>" +
        "<rect x='14' y='14' width='7' height='7' rx='1'/><rect x='3' y='14' width='7' height='7' rx='1'/>";

    public const string GitBranch =
        "<line x1='6' y1='3' x2='6' y2='15'/><circle cx='18' cy='6' r='3'/><circle cx='6' cy='18' r='3'/><path d='M18 9a9 9 0 0 1-9 9'/>";

    public const string Insights =
        "<line x1='18' y1='20' x2='18' y2='10'/><line x1='12' y1='20' x2='12' y2='4'/><line x1='6' y1='20' x2='6' y2='14'/>";

    public const string FileText =
        "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/>" +
        "<line x1='16' y1='13' x2='8' y2='13'/><line x1='16' y1='17' x2='8' y2='17'/><polyline points='10 9 9 9 8 9'/>";

    public const string Clock =
        "<circle cx='12' cy='12' r='10'/><polyline points='12 6 12 12 16 14'/>";

    // The gear's outline, drawn twice by ToolSettings: once filled as the dim plate, once as the stroke.
    private const string GearOutline =
        "M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z";

    public const string Search =
        "<circle cx='11' cy='11' r='8'/><line x1='21' y1='21' x2='16.65' y2='16.65'/>";

    public const string ChevronLeft = "<polyline points='15 18 9 12 15 6'/>";
    public const string ChevronRight = "<polyline points='9 18 15 12 9 6'/>";
    public const string ChevronDown = "<polyline points='6 9 12 15 18 9'/>";
    public const string ArrowRight = "<line x1='5' y1='12' x2='19' y2='12'/><polyline points='12 5 19 12 12 19'/>";

    public const string Menu =
        "<line x1='3' y1='6' x2='21' y2='6'/><line x1='3' y1='12' x2='21' y2='12'/><line x1='3' y1='18' x2='21' y2='18'/>";

    public const string Copy =
        "<rect x='9' y='9' width='13' height='13' rx='2' ry='2'/><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'/>";

    public const string List =
        "<line x1='8' y1='6' x2='21' y2='6'/><line x1='8' y1='12' x2='21' y2='12'/><line x1='8' y1='18' x2='21' y2='18'/>" +
        "<line x1='3' y1='6' x2='3.01' y2='6'/><line x1='3' y1='12' x2='3.01' y2='12'/><line x1='3' y1='18' x2='3.01' y2='18'/>";

    public const string Sheet =
        "<rect x='3' y='3' width='18' height='18' rx='2'/><line x1='3' y1='9' x2='21' y2='9'/>" +
        "<line x1='3' y1='15' x2='21' y2='15'/><line x1='9' y1='9' x2='9' y2='21'/>";

    public const string CheckCircle =
        "<path d='M22 11.08V12a10 10 0 1 1-5.93-9.14'/><polyline points='22 4 12 14.01 9 11.01'/>";

    public const string XCircle =
        "<circle cx='12' cy='12' r='10'/><line x1='15' y1='9' x2='9' y2='15'/><line x1='9' y1='9' x2='15' y2='15'/>";

    public const string MinusCircle =
        "<circle cx='12' cy='12' r='10'/><line x1='8' y1='12' x2='16' y2='12'/>";

    public const string HelpCircle =
        "<circle cx='12' cy='12' r='10'/><path d='M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3'/>" +
        "<line x1='12' y1='17' x2='12.01' y2='17'/>";

    public const string Check = "<polyline points='20 6 9 17 4 12'/>";

    public const string Refresh =
        "<polyline points='23 4 23 10 17 10'/><polyline points='1 20 1 14 7 14'/>" +
        "<path d='M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15'/>";

    public const string Code =
        "<polyline points='16 18 22 12 16 6'/><polyline points='8 6 2 12 8 18'/>";

    public const string GitCommit =
        "<circle cx='12' cy='12' r='4'/><line x1='1.05' y1='12' x2='7' y2='12'/><line x1='17.01' y1='12' x2='22.96' y2='12'/>";

    public const string Stop =
        "<rect x='5' y='5' width='14' height='14' rx='2'/>";

    public const string AlertTriangle =
        "<path d='M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z'/>" +
        "<line x1='12' y1='9' x2='12' y2='13'/><line x1='12' y1='17' x2='12.01' y2='17'/>";

    // The tools' own glyphs, each drawn for what the tool does. They use the layer classes in app.css
    // (g-back, g-solid, g-dim, g-up, g-off), so the moving part lifts or drifts on hover. The dashboard and
    // settings are drawn the same way, so the sidebar and the search show one family.

    /// <summary>The dashboard's tiles, the first one solid.</summary>
    public const string ToolDashboard =
        "<rect class='g-solid g-up' x='2' y='2' width='9.5' height='9.5' rx='2.5'/><rect x='13.5' y='3' width='7.5' height='7.5' rx='1.5'/>" +
        "<rect x='3' y='13.5' width='7.5' height='7.5' rx='1.5'/><rect x='13.5' y='13.5' width='7.5' height='7.5' rx='1.5'/>";

    /// <summary>A gear on a dim plate, with a solid hub.</summary>
    public const string ToolSettings =
        "<path class='g-back' d='" + GearOutline + "'/><path d='" + GearOutline + "'/>" +
        "<circle class='g-solid g-up' cx='12' cy='12' r='3.5'/>";

    /// <summary>A clock with the time worked so far filled in, from the start hand to the hand that shows now.</summary>
    public const string ToolWorkday =
        "<path class='g-back g-up' d='M12 12V3A9 9 0 0 1 19.79 16.5Z'/><circle cx='12' cy='12' r='9'/>" +
        "<path d='M12 7v5l3.5 2'/>";

    /// <summary>A page carrying the Markdown mark (M and a down arrow).</summary>
    public const string ToolMarkdown =
        "<path class='g-back' d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/>" +
        "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><path d='M14 2v6h6'/>" +
        "<g class='g-up'><path d='M7.5 18v-5l2.25 2.5L12 13v5'/><path d='M16 13v5m-1.75-1.75L16 18l1.75-1.75'/></g>";

    /// <summary>A branch whose new tip is the solid node.</summary>
    public const string ToolBranchCreator =
        "<path d='M6 3v12'/><circle cx='6' cy='18' r='3'/><path d='M18 9a9 9 0 0 1-9 9'/>" +
        "<circle class='g-solid g-up' cx='18' cy='6' r='4'/>";

    /// <summary>Sprint bars on a baseline; the current sprint's bar is the solid one.</summary>
    public const string ToolInsights =
        "<path d='M3 21h18'/><rect class='g-back' x='3.5' y='11' width='4' height='7.5' rx='1.2'/>" +
        "<rect class='g-back' x='10' y='4' width='4' height='14.5' rx='1.2'/>" +
        "<rect class='g-solid g-up' x='16.5' y='7.5' width='4' height='11' rx='1.2'/>";

    /// <summary>A line of work bending into the release branch; the solid node is where it arrived.</summary>
    public const string ToolMerging =
        "<path d='M17 3v18'/><circle cx='7' cy='5' r='2.5'/><path d='M7 7.5v.5a7 7 0 0 0 7 7'/>" +
        "<circle class='g-solid g-up' cx='17' cy='15' r='3.5'/>";

    /// <summary>A branch whose tip is crossed out; the faded part is what goes, and it drifts away.</summary>
    public const string ToolCleanup =
        "<path d='M6 3v12'/><circle cx='6' cy='18' r='3'/>" +
        "<g class='g-off'><path class='g-dim' d='M18 9a9 9 0 0 1-9 9'/><path d='M15.5 3.5l5 5m0-5l-5 5'/></g>";

    // The Sprint Insights tabs, drawn like the tool glyphs so the two sets read as one family.

    /// <summary>A work item with its tasks hanging off it, and a plus for the ones to add.</summary>
    public const string TabTaskCreation =
        "<rect class='g-back' x='3' y='3' width='13' height='5' rx='1.5'/><rect x='3' y='3' width='13' height='5' rx='1.5'/>" +
        "<path d='M6.5 8v9.5H10M6.5 13H10'/><path class='g-up' d='M17.5 12.5v7m-3.5-3.5h7'/>";

    /// <summary>A gauge filled to the planned share of capacity.</summary>
    public const string TabCapacity =
        "<path class='g-back' d='M12 17H4a8 8 0 0 1 13.66-5.66Z'/><path d='M4 17a8 8 0 0 1 16 0'/>" +
        "<path class='g-up' d='M12 17l4.24-4.24'/><circle class='g-solid' cx='12' cy='17' r='1.75'/>";

    /// <summary>A ruler laid at an angle, for sizes measured against estimates.</summary>
    public const string TabSize =
        "<g transform='rotate(-45 12 12)'><rect class='g-back' x='2' y='8' width='20' height='8' rx='1.5'/>" +
        "<rect x='2' y='8' width='20' height='8' rx='1.5'/><path d='M6 8v3M10 8v4.5M14 8v3M18 8v4.5'/></g>";

    /// <summary>A checklist with two items ticked and the last one flagged.</summary>
    public const string TabTasks =
        "<path d='M11 6h10M11 12h10M11 18h10'/><path d='M3.5 6l1.5 1.5 3-3M3.5 12l1.5 1.5 3-3'/>" +
        "<rect class='g-solid g-up' x='3.5' y='16' width='4.5' height='4.5' rx='1.2'/>";

    /// <summary>A bug: body, head and legs.</summary>
    public const string TabBugs =
        "<rect class='g-back' x='7' y='8' width='10' height='13' rx='5'/><rect x='7' y='8' width='10' height='13' rx='5'/>" +
        "<path d='M9.5 8.5V7a2.5 2.5 0 0 1 5 0v1.5'/>" +
        "<path d='M12 13v8M3 14h4M17 14h4M4 8.5l3 2M20 8.5l-3 2M4 20.5l3-2.5M20 20.5l-3-2.5'/>";
}
