namespace DevKit.Web.Models;

/// <summary>
/// The categories ticked in the Task Creation dialog, against the default set for the parent being
/// created under. Every tick stands on its own: the default set is selected only through
/// <see cref="ToggleDefaults"/>, never as a side effect of ticking a single category.
/// </summary>
public sealed class TaskCreationSelection
{
    private static readonly HashSet<string> Known =
        new(PlanningTaskCatalog.Categories.Select(c => c.Prefix), StringComparer.Ordinal);

    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> defaults;

    public TaskCreationSelection(IEnumerable<string> defaultTasks)
    {
        Defaults = defaultTasks.Where(Known.Contains).Distinct(StringComparer.Ordinal).ToList();
        defaults = new HashSet<string>(Defaults, StringComparer.Ordinal);
    }

    /// <summary>The default tasks for this parent, in the order they were given.</summary>
    public IReadOnlyList<string> Defaults { get; }

    public int Count => selected.Count;

    public int SelectedDefaults => defaults.Count(selected.Contains);

    public bool AllDefaultsSelected => Defaults.Count > 0 && SelectedDefaults == Defaults.Count;

    public bool Contains(string prefix) => selected.Contains(prefix);

    public bool IsDefault(string prefix) => defaults.Contains(prefix);

    /// <summary>Ticks or unticks one category; a prefix outside the catalog is ignored.</summary>
    public void Toggle(string prefix)
    {
        if (!Known.Contains(prefix)) return;
        if (!selected.Remove(prefix)) selected.Add(prefix);
    }

    /// <summary>Ticks every default task, or unticks them all when every one is already ticked.</summary>
    public void ToggleDefaults()
    {
        if (AllDefaultsSelected) selected.ExceptWith(defaults);
        else selected.UnionWith(defaults);
    }

    /// <summary>Keeps only these categories ticked — after a partial failure, the ones to retry.</summary>
    public void RetainOnly(IEnumerable<string> prefixes) => selected.IntersectWith(prefixes);

    /// <summary>The ticked categories in catalog order, which is the order their tasks are created in.</summary>
    public IReadOnlyList<PlanningCategory> InCatalogOrder() =>
        PlanningTaskCatalog.Categories.Where(c => selected.Contains(c.Prefix)).ToList();
}
