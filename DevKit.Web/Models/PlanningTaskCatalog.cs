using DevKit.Web.Services;

namespace DevKit.Web.Models;

public record PlanningCategory(string Prefix, string Discipline, string Group, string[] ExecTypes);

/// <summary>
/// Single source of truth for planning task prefixes — shared by Task Creation (§6) and the
/// Tasks audit (§4) so the two can never drift out of sync.
/// </summary>
public static class PlanningTaskCatalog
{
    public const string DevelopmentPrefix = "Code development";

    /// <summary>Tag on tasks created under a Requirement or Change Request.</summary>
    public const string ProductTag = "Product";

    public static readonly PlanningCategory[] Categories =
    {
        new("Plan Preparation", "Development", "dev", new[] { "AI-Dev-Development" }),
        new("Plan Review", "Development", "dev", new[] { "Manual" }),
        new("Technical Analysis", "Development", "dev", new[] { "Manual", "AI-Dev-Analysis" }),
        new("Code development", "Development", "dev", new[] { "AI-Dev-Development" }),
        new("Support", "Development", "dev", new[] { "Manual" }),
        new("Verification", "Development", "dev", new[] { "Manual" }),
        new("Wiki: Functional/Technical details", "Development", "dev", new[] { "Manual", "AI-Dev-Development" }),
        new("Wiki Review", "Development", "dev", new[] { "Manual" }),
        new("Code Review", "Development", "dev", new[] { "Manual", "AI-Dev-Code Review" }),
        new("Code Support", "Development", "dev", new[] { "Manual" }),
        new("L1 Testing", "Development", "dev", new[] { "Manual" }),
        new("Bug Resolution", "Development", "dev", new[] { "Manual" }),
        new("L2 Testing", "Test", "test", new[] { "Manual" }),
        new("L3 Testing", "Test", "test", new[] { "Manual" }),
        new("QA Verification", "Test", "test", new[] { "Manual" }),
        new("TC Creation", "Test", "test", new[] { "Manual" }),
        new("TC Review", "Test", "test", new[] { "Manual" }),
        new("Bug Retesting", "Test", "test", new[] { "Manual" })
    };

    // What "Default tasks" in Task Creation ticks in one go (§6.3). Ticking "Code development"
    // used to pull the rest in as a side effect; it now selects only itself, so asking for one
    // task creates one task.
    private static readonly IReadOnlyList<string> PlannedDefaultTasks = Array.AsReadOnly(new[]
    {
        DevelopmentPrefix, "Wiki: Functional/Technical details", "Wiki Review", "Code Review",
        "L1 Testing", "L2 Testing", "L3 Testing", "TC Creation", "TC Review"
    });

    // A bug is fixed and retested; the planning, wiki and test-case tasks belong to the
    // requirement that introduced the work, not to fixing it.
    private static readonly IReadOnlyList<string> BugDefaultTasks =
        Array.AsReadOnly(new[] { "Bug Resolution", "Bug Retesting" });

    // Older prefixes the audit still accepts during the naming transition (so pre-existing
    // tasks aren't flagged). New tasks should use the catalog prefixes above.
    public static readonly string[] LegacyDevPrefixes =
        { "Development", "Technical Feasibility", "Plan Generation", "Code Quality", "L1", "Wiki Creation" };
    public static readonly string[] LegacyTestPrefixes =
        { "L2", "L3", "TC Generation" };

    /// <summary>
    /// The tag a new task carries. Under a Bug it is the maintenance tag, so the task's hours count
    /// as bug work and the Bugs audit's missing-tag check passes; anywhere else it is <see cref="ProductTag"/>.
    /// </summary>
    public static string TagFor(string? parentType) =>
        BugWork.IsBugType(parentType) ? BugWork.MaintenanceTag : ProductTag;

    /// <summary>The tasks "Default tasks" ticks for this kind of parent.</summary>
    public static IReadOnlyList<string> DefaultTasksFor(string? parentType) =>
        BugWork.IsBugType(parentType) ? BugDefaultTasks : PlannedDefaultTasks;

    public static string[] DevPrefixes => Categories.Where(c => c.Discipline == "Development").Select(c => c.Prefix).ToArray();
    public static string[] TestPrefixes => Categories.Where(c => c.Discipline == "Test").Select(c => c.Prefix).ToArray();
}
