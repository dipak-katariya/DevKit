using DevKit.Web.Models;

namespace DevKit.Web.Services;

// How each task is classified, and the running tallies Build folds them into. Kept apart from
// the formula so the part people read to check the numbers stays short.
public static partial class PlanningCapacityCalculator
{
    // ─── Classification ───

    private enum Team { Dev = 0, Qa = 1, Other = 2 }

    private enum Bucket { Deliverable, NonDeliverable, Untagged }

    private readonly record struct TaskFacts(
        Team Team, Bucket Bucket, string? ExcludedTag, double Review, double Plan,
        bool IsBug, double Completed, double Remaining);

    /// <summary>
    /// Decides a task's team, deliverable bucket and over-plan exclusion from the settings. A tag
    /// counts whether it is on the task or on its parent Requirement / Change Request — the parent
    /// is where these categories are usually organised ("ALM Activities", "Regression Testing").
    /// </summary>
    private sealed class TaskClassifier
    {
        private readonly IReadOnlyList<PlanningTagRule> _rules;
        private readonly HashSet<string> _dev;
        private readonly HashSet<string> _qa;
        private readonly string _deliverableMarker;
        private readonly string _nonDeliverableMarker;
        private readonly TagIndex _tags;

        public TaskClassifier(CapacityPlanningSettings settings, TagIndex tags)
        {
            _rules = settings.TagRules;
            _dev = new HashSet<string>(settings.DevDisciplines, StringComparer.OrdinalIgnoreCase);
            _qa = new HashSet<string>(settings.QaDisciplines, StringComparer.OrdinalIgnoreCase);
            _deliverableMarker = settings.DeliverableMarkerTag;
            _nonDeliverableMarker = settings.NonDeliverableMarkerTag;
            _tags = tags;
        }

        public TaskFacts Classify(WorkItem task)
        {
            var own = _tags.Own(task);
            var parent = _tags.Parent(task.ParentId);

            // First rule the task carries decides deliverability; the first carried rule that is
            // carved out of the over-plan decides exclusion. A task is excluded once, however many
            // excluded tags it has — it used to be subtracted once per matching tag.
            PlanningTagRule? classifying = null;
            PlanningTagRule? excluding = null;
            foreach (var rule in _rules)
            {
                if (!own.Contains(rule.Tag) && !parent.Contains(rule.Tag)) continue;
                classifying ??= rule;
                if (!rule.ExcludeFromOverPlan) continue;
                excluding = rule;
                break;
            }

            return new TaskFacts(
                TeamOf(task.Discipline),
                BucketOf(classifying, parent),
                excluding?.Tag,
                task.EffectiveEstimate,
                task.OriginalEstimate ?? 0,
                BugWork.Is(task, _tags.ParentIsBug(task.ParentId)),
                task.CompletedWork ?? 0,
                task.RemainingWork ?? 0);
        }

        private Team TeamOf(string? discipline)
        {
            var d = discipline ?? "";
            if (_dev.Contains(d)) return Team.Dev;
            return _qa.Contains(d) ? Team.Qa : Team.Other;
        }

        private Bucket BucketOf(PlanningTagRule? rule, IReadOnlySet<string> parent)
        {
            if (rule != null) return rule.Deliverable ? Bucket.Deliverable : Bucket.NonDeliverable;
            if (parent.Contains(_deliverableMarker)) return Bucket.Deliverable;
            return parent.Contains(_nonDeliverableMarker) ? Bucket.NonDeliverable : Bucket.Untagged;
        }
    }

    /// <summary>
    /// Tag strings parsed once into case-insensitive sets. Many tasks share a parent, so parent
    /// sets are cached — the old code re-split the same tag string for every tag it checked.
    /// </summary>
    private sealed class TagIndex
    {
        private static readonly HashSet<string> None = new(StringComparer.OrdinalIgnoreCase);

        private readonly IReadOnlyDictionary<string, WorkItem> _parentItems;
        private readonly Dictionary<string, HashSet<string>> _parents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _observed = new(StringComparer.OrdinalIgnoreCase);

        public TagIndex(IReadOnlyDictionary<string, WorkItem> parents) => _parentItems = parents;

        public IReadOnlySet<string> Own(WorkItem task) => Remember(Parse(task.Tags));

        public IReadOnlySet<string> Parent(string? parentId)
        {
            if (string.IsNullOrEmpty(parentId) || !_parentItems.TryGetValue(parentId, out var item)) return None;
            if (!_parents.TryGetValue(parentId, out var set)) _parents[parentId] = set = Remember(Parse(item.Tags));
            return set;
        }

        /// <summary>A task under a Bug is bug work: it is how "Bug Resolution" time is booked.</summary>
        public bool ParentIsBug(string? parentId) =>
            !string.IsNullOrEmpty(parentId)
            && _parentItems.TryGetValue(parentId, out var item)
            && BugWork.IsBugType(item.Type);

        public IReadOnlyList<string> Observed(int max) =>
            _observed.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).Take(max).ToList();

        private HashSet<string> Remember(HashSet<string> tags)
        {
            _observed.UnionWith(tags);
            return tags;
        }

        private static HashSet<string> Parse(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(
                    raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
    }

    // ─── Accumulators ───

    /// <summary>Running totals for one member within one team.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, double> _excluded = new(StringComparer.OrdinalIgnoreCase);

        public int Tasks { get; private set; }
        public double Review { get; private set; }
        public double Plan { get; private set; }
        public double Deliverable { get; private set; }
        public double NonDeliverable { get; private set; }
        public double Untagged { get; private set; }
        public double Completed { get; private set; }
        public double Remaining { get; private set; }
        public double BugHours { get; private set; }
        public double ExcludedHours => _excluded.Values.Sum();

        public void Add(in TaskFacts task)
        {
            Tasks++;
            Review += task.Review;
            Plan += task.Plan;
            if (task.ExcludedTag != null) Exclude(task.ExcludedTag, task.Review);

            // Bug resolution is extra work, tracked but never progress against the plan: its hours
            // go to their own column and stay out of Completed and Remaining.
            if (task.IsBug)
            {
                BugHours += task.Completed;
            }
            else
            {
                Completed += task.Completed;
                Remaining += task.Remaining;
            }

            // Only positive hours are classified, as before: a zero or negative estimate has no
            // share of the deliverable split, though it still counts towards planned hours.
            if (task.Review <= 0) return;
            switch (task.Bucket)
            {
                case Bucket.Deliverable: Deliverable += task.Review; break;
                case Bucket.NonDeliverable: NonDeliverable += task.Review; break;
                default: Untagged += task.Review; break;
            }
        }

        public void Merge(Tally other)
        {
            Tasks += other.Tasks;
            Review += other.Review;
            Plan += other.Plan;
            Deliverable += other.Deliverable;
            NonDeliverable += other.NonDeliverable;
            Untagged += other.Untagged;
            Completed += other.Completed;
            Remaining += other.Remaining;
            BugHours += other.BugHours;
            foreach (var (tag, hours) in other._excluded) Exclude(tag, hours);
        }

        public IReadOnlyList<TagHours> ExcludedBreakdown() =>
            _excluded.Select(e => new TagHours(e.Key, PlanningMath.Round2(e.Value))).ToList();

        private void Exclude(string tag, double hours) =>
            _excluded[tag] = _excluded.TryGetValue(tag, out var sum) ? sum + hours : hours;
    }

    /// <summary>A member's tallies, one per team, so every scope is a merge rather than a re-scan.</summary>
    private sealed class MemberTally
    {
        private readonly Tally[] _byTeam = { new(), new(), new() };

        public MemberTally(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public string Key { get; }
        public string DisplayName { get; }

        public bool WorksAcrossTeams => _byTeam[(int)Team.Dev].Tasks > 0 && _byTeam[(int)Team.Qa].Tasks > 0;

        public void Add(in TaskFacts task) => _byTeam[(int)task.Team].Add(task);

        public Tally For(PlanningScope scope)
        {
            var result = new Tally();
            switch (scope)
            {
                case PlanningScope.Development: result.Merge(_byTeam[(int)Team.Dev]); break;
                case PlanningScope.Qa: result.Merge(_byTeam[(int)Team.Qa]); break;
                default: foreach (var t in _byTeam) result.Merge(t); break;
            }
            return result;
        }
    }
}
