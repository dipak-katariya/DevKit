namespace DevKit.Web.Components;

/// <summary>When in the work a tool is used; the Guide shows it as a coloured tag.</summary>
public enum GuidePhase
{
    Once,
    EveryDay,
    WholeSprint,
    SprintStart,
    DuringSprint,
    BeforeRelease,
    AfterSprint
}

/// <summary>The colour of a guide chip: neutral, good, take care, or missing and blocked.</summary>
public enum GuideTone
{
    Plain,
    Good,
    Care,
    Bad
}

public sealed record GuideStep(string Title, string Text);

public sealed record GuideChip(string Text, GuideTone Tone = GuideTone.Plain, bool Code = false);

public sealed record GuideChipGroup(string Label, IReadOnlyList<GuideChip> Chips);

/// <summary>
/// One tool on the Guide: why you would use it, the steps on its page, and what DevKit then does without being asked.
/// </summary>
public sealed record GuideTool(
    string Id, ToolEntry Tool, GuidePhase Phase, string Why,
    IReadOnlyList<GuideStep> Steps, IReadOnlyList<string> Automatic, IReadOnlyList<GuideChipGroup> Notes);

/// <summary>One Sprint Insights tab: what it shows, and what it does for you.</summary>
public sealed record GuideTab(string Name, string Icon, GuidePhase Phase, string Text, string Automatic);

/// <summary>One file in the Code Merging Sheet picture: its path and the state Compare Code gives it.</summary>
public sealed record GuideFileState(string Path, string State, GuideTone Tone);

/// <summary>A row of "Which tool do I need?": the job, and the section of the Guide that does it.</summary>
public sealed record GuideNeed(string Need, string SectionId, string Label, ToolEntry Tool);

/// <summary>
/// What the Guide page says about each tool. Written from what the pages do; when a page changes, its entry here changes
/// with it.
/// </summary>
public static class GuideContent
{
    /// <summary>The Branch Creator example on the Guide, put through the same naming code the page uses.</summary>
    public static readonly (string Team, string Sprint, string Id, string Title) BranchExample =
        ("Phoenix", "PI 2026.4 Sprint 2", "48213", "Fix login timeout on the SSO page");

    private static ToolEntry Tool(string path) => ToolCatalog.All().First(t => t.Path == path);

    public static readonly GuideTool Settings = new("settings", ToolCatalog.Settings, GuidePhase.Once,
        "Every tool starts from these choices, so you set your team, area, sprint and repositories here once instead of on every page.",
        new GuideStep[]
        {
            new("Enter the Server URL", "Then pick how to sign in to TFS: your Windows account or a Personal Access Token."),
            new("Click Save & Connect", "TFS checks the details first, and nothing is saved if it says no. Test Connection only checks."),
            new("Set Team Name and defaults", "Default Area Path, Default Sprint (the current one, or one you choose) and Default Repository."),
            new("Pin your most-used repositories", "They come first in every repository list."),
            new("Set merging branches if you need to", "QA branch and merging target for each repository. Leave them empty and DevKit picks them."),
            new("Set up Update status if your team uses it", "Under Code Merging Sheet: Update Status, pick the tags to add and the field to set, for example PT Merged and Status In PT."),
        },
        new[]
        {
            "Opens with your saved settings every time you sign in, and saves only what you changed when you click Save settings.",
            "Puts your Team Name at the start of every branch name and uses it as Branch Cleanup's team.",
            "Opens Sprint Insights and Code Merging Sheet with your area and sprint already picked.",
            "Works out the current sprint from today's date and the sprint dates in TFS.",
            "With empty merging branches, takes the newest release branch as the target and main as the QA branch.",
            "Protects your QA and merging branches in Branch Cleanup.",
        },
        new[]
        {
            new GuideChipGroup("Good to know", new GuideChip[]
            {
                new("Export and Import move your settings to another PC"),
                new("Export never includes your token or server", GuideTone.Care),
                new("On shared DevKit your settings are kept encrypted on the server, so they follow you to every browser", GuideTone.Care),
            }),
        });

    public static readonly GuideTool BranchCreator = new("branch-creator", Tool("/branch-creator"), GuidePhase.SprintStart,
        "Every branch gets the team's name format, starts from the right base branch and is linked to its work item. Nobody types branch names by hand, and Code Merging Sheet and Branch Cleanup can find the branch later.",
        new GuideStep[]
        {
            new("Pick Area Path and Sprint, then Load", "The sprint's Requirements, Change Requests and Bugs are listed. Search, Type and Assignee narrow the list."),
            new("Check the Team Name", "It comes from Settings. Change it here and Settings keeps the new name too."),
            new("Click a work item", "The New Branch form on the right fills itself in. One branch per work item."),
            new("Look at Existing Branches", "If the work item already has a branch, it is listed. Click it to use it instead of making a new one."),
            new("Check the repository and base branch", "Both are picked for you. Change them only if this work needs another."),
            new("Click Check, then Create Branch", "Check tells you if the name is free. Create Branch makes the branch and links it to the work item."),
        },
        new[]
        {
            "Builds the branch name from your team, the sprint, the work item id and its title.",
            "Suggests a pull request title: TEAM : id : title.",
            "Picks your Default Repository, and as base branch the repository's QA branch from Settings, else main or the usual names.",
            "Lists the branches already linked to the work item, in any repository, and marks the ones that were deleted.",
            "Checks the name is valid (100 characters at most) and not already taken.",
            "Creates the branch from the latest commit of the base branch.",
            "Links the new branch to the work item in TFS, so the work item shows it.",
        },
        new[]
        {
            new GuideChipGroup("How the name is made", new GuideChip[]
            {
                new("Team: one word in capitals (12 letters at most), or the first letters of several words"),
                new("Sprint: spaces, dots and dashes become _"),
                new("Title: lower case, punctuation removed, 60 letters at most"),
                new("No sprint on the work item: no_sprint"),
                new("A link to a deleted branch? Remove it with ✕", GuideTone.Care),
            }),
        });

    public static readonly GuideTool Insights = new("insights", Tool("/insights"), GuidePhase.WholeSprint,
        "One place to plan the sprint, follow it day by day and catch work items that are filled in wrong, without building TFS queries yourself.",
        new GuideStep[]
        {
            new("Pick Area Path and Sprint", "Your defaults from Settings are picked when you open the page. The same pickers serve every tab."),
            new("Open a tab", "Six tabs, from planning at the start of the sprint to checks near the end."),
            new("Click the tab's Load or Run Checks", "Results stay on each tab while you switch between them, as long as the area and sprint stay the same."),
        },
        new[]
        {
            "Reads the sprint's work items, tasks, capacity and history from TFS for you.",
            "Writes to TFS only when you ask: Create Tasks in Task Creation and Apply in Size Check.",
            "Saves your Capacity Planning choices (mode, tag rules) as soon as you change them.",
        },
        Array.Empty<GuideChipGroup>());

    public static readonly IReadOnlyList<GuideTab> InsightsTabs = new GuideTab[]
    {
        new("Task Creation", Icons.TabTaskCreation, GuidePhase.SprintStart,
            "Create the standard child tasks for a Requirement, Change Request or Bug in one step. Default tasks or pick your own.",
            "Fills in each task: title \"Prefix : parent title\", area, sprint, discipline, tag and the link to its parent."),
        new("Capacity Planning", Icons.TabCapacity, GuidePhase.SprintStart,
            "Each member's planned hours against their capacity, for the Dev team, the QA team and the whole team.",
            "Reads capacity and days off from TFS, and works the numbers out again as you change the mode or the tag rules."),
        new("Size Check", Icons.TabSize, GuidePhase.SprintStart,
            "Compares each requirement's size with the hours of its tasks and shows the ones that do not match.",
            "Works out the right size and writes it to TFS, for one row or all of them with Apply All Sizes."),
        new("Burndown", Icons.Insights, GuidePhase.DuringSprint,
            "Hours each member logged per day or per week. Bug hours are shown apart, and a day off in TFS with no hours reads Leave.",
            "Reads every change to Completed Work from task history and compares each day with the member's hours per day."),
        new("Tasks", Icons.TabTasks, GuidePhase.DuringSprint,
            "Tasks with no discipline or estimate, a wrong title prefix, or hours that do not add up.",
            "Run Checks fills Tasks and Bugs together. It only reads; nothing is changed in TFS."),
        new("Bugs", Icons.TabBugs, GuidePhase.DuringSprint,
            "Bugs with no root cause, no proposed fix or no development task, and bugs flagged as published.",
            "Leaves out duplicate, deferred, rejected and cannot-reproduce bugs."),
    };

    public static readonly GuideTool CodeMerging = new("merging", Tool("/code-merging"), GuidePhase.BeforeRelease,
        "Before a release you need to know no work was left behind or lost in a conflict. The sheet checks every work item of the sprint for you, down to the line, instead of you opening pull requests one by one.",
        new GuideStep[]
        {
            new("Pick area, sprints and repositories", "Several sprints and up to 10 repositories at once."),
            new("Choose Deliverable or All", "Deliverable keeps Requirements tagged Deliverable, plus every Bug and Change Request."),
            new("Check Branch Setup", "The QA branch and merging branch of each repository. Save as defaults keeps them for next time."),
            new("Click Load Data", "Each work item is listed with its pull requests and commits."),
            new("Click Verify All", "The quick check: are the commits on the merging branch?"),
            new("Click Compare Code", "The sure check: each pull request's code against the merging branch, file by file. Stop keeps what is done."),
            new("Copy details into your merge sheet", "A work item's copy button copies one row per pull request, and the one beside a pull request copies its row. Click the Name cell of an empty row in the sheet and paste."),
            new("Click Update status once it is merged", "It shows what will change first: the tags and the field set up in Settings. Update writes them to the work item."),
        },
        new[]
        {
            "Finds pull requests from the work item's links, and from branch names or titles that hold its id.",
            "Shows only pull requests into the QA branch, unless you turn on All PRs.",
            "Finds commits even when they were cherry-picked, and takes reverts into account.",
            "Checks a row again when you open it.",
            "Picks the branches when none are saved: the newest release branch as target, main as QA.",
            "Copy details fills DATE (the day the pull request was merged), REQ/CHR TFS ID, Pull Request, Branch Name and Commit IDs, and leaves the other columns empty.",
            "Leaves merges from other branches and rebase commits out of Commit IDs, and abandoned pull requests out of the copy.",
            "Writes to TFS only when you click Update in Update status, and then only the tags and field it showed. The work item keeps its other tags.",
        },
        Array.Empty<GuideChipGroup>());

    public static readonly GuideTool BranchCleanup = new("cleanup", Tool("/branch-delete"), GuidePhase.AfterSprint,
        "Old branches pile up every sprint and make every branch list long and slow. Cleanup finds your team's finished branches and deletes them together, while it protects anything still in use.",
        new GuideStep[]
        {
            new("Pick area, old sprints, repositories and Team", "Team is the same Team Name as in Settings."),
            new("Click Load Branches", "Branches linked to work items of those sprints, and team branches no work item links."),
            new("Filter the list", "Linked or Unattached, sprint, Created by, or Search."),
            new("Tick the branches to remove", "Or Select all shown. Protected branches carry a 🔒."),
            new("Click Delete selected", "Read the warning, then confirm."),
        },
        new[]
        {
            "Marks each branch Linked (a work item links it) or Unattached (your team's name, but no link).",
            "Checks pull requests and Settings again just before it deletes.",
            "Skips a branch someone pushed to after the list loaded.",
            "Deletes in batches of 50 per repository and shows a result for each branch.",
            "Leaves the work items' links in place: Branch Creator shows them as deleted and can remove them.",
        },
        new[]
        {
            new GuideChipGroup("Never deleted", new GuideChip[]
            {
                new("main", GuideTone.Bad, true), new("master", GuideTone.Bad, true), new("develop", GuideTone.Bad, true),
                new("release", GuideTone.Bad, true), new("release/*", GuideTone.Bad, true), new("hotfix", GuideTone.Bad, true),
                new("QA and merging branches from Settings", GuideTone.Bad),
                new("Branches in an active pull request", GuideTone.Bad),
            }),
            new GuideChipGroup("Take care", new GuideChip[]
            {
                new("Deleting cannot be undone", GuideTone.Care),
            }),
        });

    public static readonly GuideTool Workday = new("workday", Tool("/workday"), GuidePhase.EveryDay,
        "No adding up swipe times by hand: paste them in and you see at once how long you have worked and when you can leave.",
        new GuideStep[]
        {
            new("Pick your day", "Full Day 8h 30m, Half Day 4h 30m or Movement 7h 30m."),
            new("Type or paste your Time Entries", "One per line: in, out, in, out. The last one can be an in you have not closed yet."),
            new("Read the results", "Time worked, time remaining and your estimated departure."),
        },
        new[]
        {
            "Keeps the count running every second while you are clocked in.",
            "Reads 9:22, 09:22:22, 9:22 AM or 13:05.",
            "Says which line is wrong when a time cannot be read or is out of order.",
        },
        new[]
        {
            new GuideChipGroup("Take care", new GuideChip[] { new("Nothing is saved: closing the page clears it", GuideTone.Care) }),
        });

    public static readonly GuideTool Markdown = new("markdown", Tool("/markdown"), GuidePhase.EveryDay,
        "See how a README or wiki page will look before you commit it, or turn notes into HTML or a PDF.",
        new GuideStep[]
        {
            new("Type, paste or Import", "Import opens .md, .markdown and .txt files."),
            new("Pick Editor, Split or Preview", "In Split, drag the divider to resize."),
            new("Export or copy", "Export as .md, .html or PDF, or Copy as Text."),
        },
        new[]
        {
            "Updates the preview as you type.",
            "Counts lines, words and characters.",
        },
        new[]
        {
            new GuideChipGroup("Take care", new GuideChip[] { new("Nothing is saved: export before you close the page", GuideTone.Care) }),
        });

    /// <summary>The files in the Code Merging Sheet picture, one for each state Compare Code can give a file.</summary>
    public static readonly IReadOnlyList<GuideFileState> MergingFiles = new GuideFileState[]
    {
        new("src/Auth/LoginService.cs", "Present", GuideTone.Good),
        new("src/Auth/SsoHandler.cs", "Matches QA", GuideTone.Good),
        new("src/Web/login.js", "Partly merged", GuideTone.Care),
        new("src/Auth/TokenCache.cs", "Missing", GuideTone.Bad),
        new("src/Auth/SessionStore.cs", "Code lost in a conflict fix", GuideTone.Bad),
        new("assets/login-banner.png", "Not compared", GuideTone.Plain),
    };

    /// <summary>The sprint tools in the order the team uses them, then the day tools.</summary>
    public static readonly IReadOnlyList<GuideTool> SprintWork = new[] { BranchCreator, Insights, CodeMerging, BranchCleanup };

    public static readonly IReadOnlyList<GuideTool> YourDay = new[] { Workday, Markdown };

    public static readonly IReadOnlyList<GuideNeed> Needs = new GuideNeed[]
    {
        new("Start work on a Requirement, Change Request or Bug", BranchCreator.Id, "Branch Creator", BranchCreator.Tool),
        new("Create the standard tasks for a work item", Insights.Id, "Sprint Insights › Task Creation", Insights.Tool),
        new("See if the team is over or under planned", Insights.Id, "Sprint Insights › Capacity Planning", Insights.Tool),
        new("Fix requirement sizes", Insights.Id, "Sprint Insights › Size Check", Insights.Tool),
        new("See who logged hours each day", Insights.Id, "Sprint Insights › Burndown", Insights.Tool),
        new("Find tasks and bugs with missing details", Insights.Id, "Sprint Insights › Tasks, Bugs", Insights.Tool),
        new("Make sure all code reached the release branch", CodeMerging.Id, "Code Merging Sheet", CodeMerging.Tool),
        new("Remove old sprint branches", BranchCleanup.Id, "Branch Cleanup", BranchCleanup.Tool),
        new("Know when I can leave today", Workday.Id, "Workday Calculator", Workday.Tool),
        new("Preview or export a Markdown file", Markdown.Id, "Markdown Viewer", Markdown.Tool),
    };

    public static string PhaseLabel(GuidePhase phase) => phase switch
    {
        GuidePhase.Once => "Once, before anything else",
        GuidePhase.EveryDay => "Any day",
        GuidePhase.WholeSprint => "All through the sprint",
        GuidePhase.SprintStart => "Sprint start",
        GuidePhase.DuringSprint => "During the sprint",
        GuidePhase.BeforeRelease => "Before a release",
        _ => "After the sprint"
    };
}
