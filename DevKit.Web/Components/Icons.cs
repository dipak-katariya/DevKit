namespace DevKit.Web.Components;

/// <summary>
/// Inner markup for Feather-style line icons (24×24, stroke = currentColor).
/// Rendered through <see cref="Icon"/>, which supplies the wrapping &lt;svg&gt; element.
/// Values are static trusted constants — safe to emit as MarkupString.
/// </summary>
public static class Icons
{
    public const string Dashboard =
        "<rect x='3' y='3' width='7' height='7' rx='1'/><rect x='14' y='3' width='7' height='7' rx='1'/>" +
        "<rect x='14' y='14' width='7' height='7' rx='1'/><rect x='3' y='14' width='7' height='7' rx='1'/>";

    public const string GitBranch =
        "<line x1='6' y1='3' x2='6' y2='15'/><circle cx='18' cy='6' r='3'/><circle cx='6' cy='18' r='3'/><path d='M18 9a9 9 0 0 1-9 9'/>";

    public const string Trash =
        "<polyline points='3 6 5 6 21 6'/><path d='M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2'/>" +
        "<line x1='10' y1='11' x2='10' y2='17'/><line x1='14' y1='11' x2='14' y2='17'/>";

    public const string Planning =
        "<polyline points='9 11 12 14 22 4'/><path d='M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11'/>";

    public const string Insights =
        "<line x1='18' y1='20' x2='18' y2='10'/><line x1='12' y1='20' x2='12' y2='4'/><line x1='6' y1='20' x2='6' y2='14'/>";

    public const string GitMerge =
        "<circle cx='18' cy='18' r='3'/><circle cx='6' cy='6' r='3'/><path d='M6 21V9a9 9 0 0 0 9 9'/>";

    public const string FileText =
        "<path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'/><polyline points='14 2 14 8 20 8'/>" +
        "<line x1='16' y1='13' x2='8' y2='13'/><line x1='16' y1='17' x2='8' y2='17'/><polyline points='10 9 9 9 8 9'/>";

    public const string Clock =
        "<circle cx='12' cy='12' r='10'/><polyline points='12 6 12 12 16 14'/>";

    public const string Settings =
        "<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/>";

    public const string Search =
        "<circle cx='11' cy='11' r='8'/><line x1='21' y1='21' x2='16.65' y2='16.65'/>";

    public const string ChevronLeft = "<polyline points='15 18 9 12 15 6'/>";
    public const string ChevronRight = "<polyline points='9 18 15 12 9 6'/>";
    public const string ChevronDown = "<polyline points='6 9 12 15 18 9'/>";
    public const string ArrowRight = "<line x1='5' y1='12' x2='19' y2='12'/><polyline points='12 5 19 12 12 19'/>";

    public const string Bell =
        "<path d='M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9'/><path d='M13.73 21a2 2 0 0 1-3.46 0'/>";

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
}
