namespace DevKit.Web.Models;

/// <summary>How far above capacity a sprint is meant to be planned.</summary>
public enum PlanningMode
{
    /// <summary>Plan to capacity exactly: 100% means every available hour is planned.</summary>
    Regular = 0,

    /// <summary>Plan above capacity by a percentage, on the premise AI tooling delivers the extra hours.</summary>
    AiOverPlan = 1
}

/// <summary>Where the number of working days in a sprint comes from.</summary>
public enum SprintLengthSource
{
    /// <summary>Working weekdays between the iteration's start and finish dates in TFS.</summary>
    IterationDates = 0,

    /// <summary>A fixed number of weeks times the working days in a week.</summary>
    Fixed = 1
}

/// <summary>How one TFS tag is treated by capacity planning.</summary>
public sealed class PlanningTagRule
{
    /// <summary>Required by the settings deserializer.</summary>
    public PlanningTagRule() { }

    private PlanningTagRule(PlanningTagRule other)
    {
        Tag = other.Tag;
        Deliverable = other.Deliverable;
        ExcludeFromOverPlan = other.ExcludeFromOverPlan;
    }

    public string Tag { get; set; } = "";

    /// <summary>Hours on work carrying this tag count as Deliverable; otherwise Non-Deliverable.</summary>
    public bool Deliverable { get; set; }

    /// <summary>
    /// Hours on work carrying this tag are taken out of the base the AI over-plan percentage is
    /// applied to — ceremony and support work AI tooling does not speed up.
    /// </summary>
    public bool ExcludeFromOverPlan { get; set; }

    public PlanningTagRule Clone() => new(this);
}

/// <summary>
/// Everything the Capacity Planning tab's formula depends on, configured once and persisted.
/// The defaults reproduce the formula that was previously hard-coded — 40% AI over-plan,
/// 8.5 h a day, Monday to Friday, ALM / Team B Support / Engineering Operations &amp; Support
/// carved out of the over-plan base — so an existing setup reads the same numbers after upgrading.
/// </summary>
public sealed class CapacityPlanningSettings
{
    public const double DefaultHoursPerDay = 8.5;
    public const double DefaultAiOverPlanPercent = 40;
    public const int DefaultSprintWeeks = 2;
    public const string DefaultDeliverableMarker = "Deliverable";
    public const string DefaultNonDeliverableMarker = "Non-Deliverable";
    private const string DefaultDevDiscipline = "Development";
    private const string DefaultQaDiscipline = "Test";

    // Bounds keep a hand-edited settings file from producing nonsense — or a UI that renders
    // thousands of chips. They are generous enough never to get in a real team's way.
    public const double MaxOverPlanPercent = 200;
    public const double MaxHoursPerDay = 24;
    public const int MaxSprintWeeks = 8;
    public const int MaxTagRules = 50;
    public const int MaxDisciplines = 20;
    public const int MaxNameLength = 100;

    /// <summary>Required by the settings deserializer.</summary>
    public CapacityPlanningSettings() { }

    private CapacityPlanningSettings(CapacityPlanningSettings other)
    {
        Mode = other.Mode;
        AiOverPlanPercent = other.AiOverPlanPercent;
        HoursPerDay = other.HoursPerDay;
        UseTfsMemberCapacity = other.UseTfsMemberCapacity;
        SprintLength = other.SprintLength;
        SprintWeeks = other.SprintWeeks;
        WorkingDays = new List<DayOfWeek>(other.WorkingDays ?? DefaultWorkingDays());
        TagRules = (other.TagRules ?? DefaultTagRules()).Where(r => r is not null).Select(r => r.Clone()).ToList();
        DeliverableMarkerTag = other.DeliverableMarkerTag;
        NonDeliverableMarkerTag = other.NonDeliverableMarkerTag;
        DevDisciplines = new List<string>(other.DevDisciplines ?? new List<string>());
        QaDisciplines = new List<string>(other.QaDisciplines ?? new List<string>());
    }

    public PlanningMode Mode { get; set; } = PlanningMode.AiOverPlan;

    /// <summary>Over-plan applied in <see cref="PlanningMode.AiOverPlan"/>; kept while in Regular mode.</summary>
    public double AiOverPlanPercent { get; set; } = DefaultAiOverPlanPercent;

    /// <summary>The working day's length for this organisation.</summary>
    public double HoursPerDay { get; set; } = DefaultHoursPerDay;

    /// <summary>
    /// When set, a member's capacity-per-day configured in TFS wins and <see cref="HoursPerDay"/> is
    /// the fallback for members without one. When cleared, <see cref="HoursPerDay"/> applies to all.
    /// </summary>
    public bool UseTfsMemberCapacity { get; set; } = true;

    public SprintLengthSource SprintLength { get; set; } = SprintLengthSource.IterationDates;

    /// <summary>Sprint length when <see cref="SprintLength"/> is Fixed, or the iteration has no dates.</summary>
    public int SprintWeeks { get; set; } = DefaultSprintWeeks;

    /// <summary>Weekdays that count as working days — for sprint length and for leave alike.</summary>
    public List<DayOfWeek> WorkingDays { get; set; } = DefaultWorkingDays();

    /// <summary>
    /// Tag rules in precedence order: when work carries several configured tags, the first rule in
    /// this list decides whether it is Deliverable.
    /// </summary>
    public List<PlanningTagRule> TagRules { get; set; } = DefaultTagRules();

    /// <summary>Tag on a parent Requirement / Change Request marking its work Deliverable.</summary>
    public string DeliverableMarkerTag { get; set; } = DefaultDeliverableMarker;

    /// <summary>Tag on a parent Requirement / Change Request marking its work Non-Deliverable.</summary>
    public string NonDeliverableMarkerTag { get; set; } = DefaultNonDeliverableMarker;

    /// <summary>Task disciplines that make up the development team.</summary>
    public List<string> DevDisciplines { get; set; } = new() { DefaultDevDiscipline };

    /// <summary>Task disciplines that make up the QA team.</summary>
    public List<string> QaDisciplines { get; set; } = new() { DefaultQaDiscipline };

    /// <summary>The over-plan actually in force: zero in Regular mode whatever the AI percentage says.</summary>
    public double EffectiveOverPlanPercent => Mode == PlanningMode.AiOverPlan ? AiOverPlanPercent : 0;

    public int DaysPerWeek => WorkingDays.Count;

    public int FixedSprintDays => SprintWeeks * DaysPerWeek;

    public static List<DayOfWeek> DefaultWorkingDays() => new()
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
    };

    /// <summary>
    /// The four tags the tab always offered, in the precedence they were checked. Regression was
    /// never carved out of the over-plan base; the other three always were.
    /// </summary>
    public static List<PlanningTagRule> DefaultTagRules() => new()
    {
        new() { Tag = "ALM", ExcludeFromOverPlan = true },
        new() { Tag = "Team B Support", ExcludeFromOverPlan = true },
        new() { Tag = "Engineering Operations & Support", ExcludeFromOverPlan = true },
        new() { Tag = "Regression", ExcludeFromOverPlan = false }
    };

    /// <summary>
    /// First-run settings for a machine that only has the older per-tag Deliverable toggles. Those
    /// toggles carry over, so the chips read exactly as they did before the upgrade.
    /// </summary>
    public static CapacityPlanningSettings FromLegacy(IReadOnlyDictionary<string, bool>? legacyDeliverable)
    {
        var settings = new CapacityPlanningSettings();
        if (legacyDeliverable is null || legacyDeliverable.Count == 0) return settings;

        var legacy = new Dictionary<string, bool>(legacyDeliverable, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in settings.TagRules)
            if (legacy.TryGetValue(rule.Tag, out var deliverable)) rule.Deliverable = deliverable;

        return settings;
    }

    public CapacityPlanningSettings Clone() => new(this);

    /// <summary>
    /// A sanitised deep copy. Applied on load and before every save, because the settings file is
    /// hand-editable and every value here ends up in arithmetic or in the rendered page.
    /// </summary>
    public CapacityPlanningSettings Normalized()
    {
        // Every line reads the copy, not this: the copy constructor has already replaced a null
        // collection from a damaged file with its default, and reading this again would undo it.
        var n = new CapacityPlanningSettings(this);
        n.Mode = Enum.IsDefined(n.Mode) ? n.Mode : PlanningMode.AiOverPlan;
        n.AiOverPlanPercent = Bounded(n.AiOverPlanPercent, 0, MaxOverPlanPercent, DefaultAiOverPlanPercent);
        n.HoursPerDay = n.HoursPerDay > 0 ? Bounded(n.HoursPerDay, 0, MaxHoursPerDay, DefaultHoursPerDay) : DefaultHoursPerDay;
        n.SprintLength = Enum.IsDefined(n.SprintLength) ? n.SprintLength : SprintLengthSource.IterationDates;
        n.SprintWeeks = Math.Clamp(n.SprintWeeks, 1, MaxSprintWeeks);
        n.WorkingDays = NormalizeWorkingDays(n.WorkingDays);
        n.TagRules = NormalizeTagRules(n.TagRules);
        n.DeliverableMarkerTag = NormalizeName(n.DeliverableMarkerTag, DefaultDeliverableMarker);
        n.NonDeliverableMarkerTag = NormalizeName(n.NonDeliverableMarkerTag, DefaultNonDeliverableMarker);
        n.DevDisciplines = NormalizeNames(n.DevDisciplines, DefaultDevDiscipline);
        n.QaDisciplines = NormalizeNames(n.QaDisciplines, DefaultQaDiscipline);
        return n;
    }

    /// <summary>Sorting key that puts Monday first and Sunday last.</summary>
    public static int MondayFirst(DayOfWeek day) => ((int)day + 6) % 7;

    private static double Bounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>Defined weekdays only, deduplicated, Monday first; an empty list falls back to Mon–Fri.</summary>
    private static List<DayOfWeek> NormalizeWorkingDays(IEnumerable<DayOfWeek>? days)
    {
        var valid = (days ?? Enumerable.Empty<DayOfWeek>())
            .Where(d => Enum.IsDefined(d))
            .Distinct()
            .OrderBy(MondayFirst)
            .ToList();
        return valid.Count > 0 ? valid : DefaultWorkingDays();
    }

    /// <summary>Trimmed, non-blank, bounded and unique by tag — the first rule for a tag wins.</summary>
    private static List<PlanningTagRule> NormalizeTagRules(IEnumerable<PlanningTagRule?>? rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PlanningTagRule>();
        foreach (var rule in rules ?? Enumerable.Empty<PlanningTagRule?>())
        {
            if (result.Count >= MaxTagRules) break;
            if (rule is null) continue;

            var trimmed = (rule.Tag ?? "").Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || !seen.Add(trimmed)) continue;

            var copy = rule.Clone();
            copy.Tag = trimmed;
            result.Add(copy);
        }
        return result;
    }

    private static string NormalizeName(string? value, string fallback)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 || trimmed.Length > MaxNameLength ? fallback : trimmed;
    }

    private static List<string> NormalizeNames(IEnumerable<string?>? names, string fallback)
    {
        var result = (names ?? Enumerable.Empty<string?>())
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0 && n.Length <= MaxNameLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxDisciplines)
            .ToList();
        return result.Count > 0 ? result : new List<string> { fallback };
    }
}
