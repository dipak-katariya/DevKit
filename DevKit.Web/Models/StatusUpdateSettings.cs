using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevKit.Web.Models;

/// <summary>
/// What the Code Merging Sheet's Update status button does to a work item once its pull requests are merged: the tags
/// it adds, and the one field it sets, such as Status to "In PT". Kept per person with the other settings; off until
/// the person sets it up. Methods rather than computed properties, since every property here is written to the file.
/// </summary>
public sealed class StatusUpdateSettings
{
    /// <summary>Most tags one click adds, so a hand-edited settings file cannot make it add hundreds.</summary>
    public const int MaxTags = 20;

    /// <summary>Longest tag kept; TFS takes longer ones, but nobody types one near this.</summary>
    public const int MaxTagLength = 100;

    /// <summary>Longest field name or value kept.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Longest field reference name accepted, well above any TFS has.</summary>
    private const int MaxReferenceLength = 128;

    /// <summary>The separator TFS keeps tags apart with, so it can never be part of one.</summary>
    public const char TagSeparator = ';';

    /// <summary>Required by the settings deserializer.</summary>
    public StatusUpdateSettings()
    {
    }

    private StatusUpdateSettings(StatusUpdateSettings source)
    {
        UnknownFields = source.UnknownFields is null ? null : new Dictionary<string, JsonElement>(source.UnknownFields);
        AddTags = source.AddTags;
        Tags = source.Tags?.ToList() ?? new List<string>();
        SetField = source.SetField;
        Field = source.Field ?? "";
        FieldName = source.FieldName ?? "";
        Value = source.Value ?? "";
    }

    /// <summary>Fields this build does not know, written back as they were (see UserSettings).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    /// <summary>Whether a click adds <see cref="Tags"/>.</summary>
    public bool AddTags { get; set; }

    /// <summary>The tags a click adds to those the work item has, in the order they were picked.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Whether a click sets <see cref="Field"/> to <see cref="Value"/>.</summary>
    public bool SetField { get; set; }

    /// <summary>The field's reference name, such as "Casepoint.TFS.CustomFields.Status".</summary>
    public string Field { get; set; } = "";

    /// <summary>The field's name as people know it, such as "Status", for the page before TFS has answered.</summary>
    public string FieldName { get; set; } = "";

    /// <summary>The value a click sets, one the field allows, such as "In PT".</summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// A copy cleaned as it is kept: tags trimmed, without blanks, repeats or separators, and at most
    /// <see cref="MaxTags"/>; a field reference that is not one dropped with its name; names and values capped.
    /// <see cref="NormalizingDrops"/> says what this leaves out.
    /// </summary>
    public StatusUpdateSettings Normalized()
    {
        var copy = new StatusUpdateSettings(this) { Tags = CleanTags(Tags) };
        if (!IsFieldReference(copy.Field))
        {
            copy.Field = "";
            copy.FieldName = "";
        }

        copy.FieldName = Cap(copy.FieldName.Trim());
        copy.Value = Cap(copy.Value.Trim());
        return copy;
    }

    /// <summary>
    /// <see cref="Normalized()"/>, with a Warning in <paramref name="logger"/> for each thing it leaves out, so a settings
    /// file that loses part of its Update status on import or save says why. Only setting names and counts are logged.
    /// </summary>
    public StatusUpdateSettings Normalized(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var drop in NormalizingDrops())
        {
            logger.LogWarning("Update status settings were cleaned: {Drop}", drop);
        }

        return Normalized();
    }

    /// <summary>What <see cref="Normalized()"/> leaves out or cuts, in words for the log or the page; empty when nothing.</summary>
    public IReadOnlyList<string> NormalizingDrops()
    {
        var drops = new List<string>();
        var tags = Tags ?? new List<string>();
        var refused = tags.Count - CleanTags(tags).Count;
        if (refused > 0)
        {
            drops.Add($"{refused} tag(s) left out: blank, repeated, too long, containing '{TagSeparator}' or past the first {MaxTags}");
        }

        if (!string.IsNullOrEmpty(Field) && !IsFieldReference(Field))
        {
            drops.Add("the field was left out: it is not a field reference name such as System.State");
        }

        if ((FieldName ?? "").Trim().Length > MaxValueLength || (Value ?? "").Trim().Length > MaxValueLength)
        {
            drops.Add($"the field name or value was cut to {MaxValueLength} characters");
        }

        return drops;
    }

    /// <summary>Whether a click has anything to do: tags to add, or a field and a value to set.</summary>
    public bool HasWork() => HasTagsToAdd() || HasFieldToSet();

    /// <summary>Whether a click adds tags: Add tags is on, with at least one tag.</summary>
    public bool HasTagsToAdd() => AddTags && Tags.Count > 0;

    /// <summary>Whether a click sets a field: one is chosen, with a value.</summary>
    public bool HasFieldToSet() => SetField && Field.Length > 0 && Value.Length > 0;

    /// <summary>What a click does, in words: "adds PT Merged and sets Status to In PT".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (HasTagsToAdd())
        {
            parts.Add($"adds {string.Join(", ", Tags)}");
        }

        if (HasFieldToSet())
        {
            parts.Add($"sets {ShownFieldName()} to {Value}");
        }

        return parts.Count == 0 ? "does nothing yet" : string.Join(" and ", parts);
    }

    /// <summary>The field's name, or its reference name before TFS has said what it is called.</summary>
    public string ShownFieldName() => FieldName.Length > 0 ? FieldName : Field;

    /// <summary>
    /// Whether <paramref name="reference"/> is a field reference name: dotted words of letters, digits and underscores,
    /// such as "System.State". It goes into the path of the write, so nothing else may pass.
    /// </summary>
    public static bool IsFieldReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxReferenceLength)
        {
            return false;
        }

        var words = reference.Split('.');
        return words.Length > 1
            && words.All(word => word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            && !char.IsAsciiDigit(words[0][0]);
    }

    /// <summary>Why <paramref name="tag"/> cannot be kept, in words for the page; null when it can.</summary>
    public static string? TagProblem(string? tag)
    {
        var trimmed = (tag ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Type a tag first.";
        }

        if (trimmed.Contains(TagSeparator))
        {
            return $"A tag cannot contain '{TagSeparator}', which TFS puts between tags.";
        }

        return trimmed.Length > MaxTagLength ? $"A tag is at most {MaxTagLength} characters." : null;
    }

    /// <summary>Tags trimmed, without blanks, separators or repeats in any case, in their order, and at most <see cref="MaxTags"/>.</summary>
    public static List<string> CleanTags(IEnumerable<string?>? tags) => (tags ?? Enumerable.Empty<string?>())
        .Select(tag => (tag ?? "").Trim())
        .Where(tag => TagProblem(tag) is null)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxTags)
        .ToList();

    private static string Cap(string value) => value.Length > MaxValueLength ? value[..MaxValueLength] : value;
}

/// <summary>
/// One work item as Update status reads it before writing: its revision, so the write fails rather than overwrite a
/// change made meanwhile, where it lives, and the values the write may change.
/// </summary>
/// <param name="FieldValue">The configured field's value now; empty when it has none.</param>
public sealed record WorkItemForUpdate(int Id, int Rev, string Project, string Type, string Title, string Tags, string FieldValue);

/// <summary>What a click does to the configured field.</summary>
public enum StatusFieldOutcome
{
    /// <summary>The field is written with the new value.</summary>
    Set,

    /// <summary>The field already has the value; nothing is written to it.</summary>
    AlreadySet,

    /// <summary>The field is left as it is although it was asked for; the change says why.</summary>
    Skipped
}

/// <summary>What a click does to the configured field of one work item. Made only through its three factories.</summary>
public sealed class StatusFieldChange
{
    private StatusFieldChange(string reference, string name, string current, StatusFieldOutcome outcome, string? newValue, string? note)
    {
        Reference = reference;
        Name = name;
        Current = current;
        Outcome = outcome;
        NewValue = newValue;
        Note = note;
    }

    /// <summary>The field's reference name.</summary>
    public string Reference { get; }

    /// <summary>The field's name, such as "Status".</summary>
    public string Name { get; }

    /// <summary>Its value now; empty when it has none.</summary>
    public string Current { get; }

    /// <summary>What the click does to it.</summary>
    public StatusFieldOutcome Outcome { get; }

    /// <summary>The value written; set only when <see cref="Outcome"/> is <see cref="StatusFieldOutcome.Set"/>.</summary>
    public string? NewValue { get; }

    /// <summary>Why the field is left alone; set only when <see cref="Outcome"/> is <see cref="StatusFieldOutcome.Skipped"/>.</summary>
    public string? Note { get; }

    /// <summary>The field goes from <paramref name="current"/> to <paramref name="newValue"/>.</summary>
    public static StatusFieldChange Set(string reference, string name, string current, string newValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(newValue);
        return new StatusFieldChange(reference, name, current, StatusFieldOutcome.Set, newValue, null);
    }

    /// <summary>The field already has the value.</summary>
    public static StatusFieldChange AlreadySet(string reference, string name, string current) =>
        new(reference, name, current, StatusFieldOutcome.AlreadySet, null, null);

    /// <summary>The field cannot be set on this work item, for the reason in <paramref name="note"/>.</summary>
    public static StatusFieldChange Skipped(string reference, string name, string current, string note)
    {
        ArgumentException.ThrowIfNullOrEmpty(note);
        return new StatusFieldChange(reference, name, current, StatusFieldOutcome.Skipped, null, note);
    }

    /// <summary>What happens to the field, in words: "In Dev → In PT", "Already In PT", or why it is left alone.</summary>
    public string Describe() => Outcome switch
    {
        StatusFieldOutcome.Set => $"{StatusUpdatePlanner.Shown(Current)} → {NewValue}",
        StatusFieldOutcome.AlreadySet => $"Already {StatusUpdatePlanner.Shown(Current)}",
        _ => Note!
    };
}

/// <summary>
/// What one click changes on a work item, worked out from what it holds now. Made only by <see cref="StatusUpdatePlanner"/>,
/// and the tag field written is worked out from the tags the work item has, so a plan can only ever add tags.
/// </summary>
public sealed class StatusUpdatePlan
{
    internal StatusUpdatePlan(WorkItemForUpdate item, IReadOnlyList<string> tagsToAdd, StatusFieldChange? field)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(tagsToAdd);
        Item = item;
        TagsToAdd = tagsToAdd;
        Field = field;
    }

    /// <summary>The work item as it was read.</summary>
    public WorkItemForUpdate Item { get; }

    /// <summary>The configured tags the work item does not have yet.</summary>
    public IReadOnlyList<string> TagsToAdd { get; }

    /// <summary>What happens to the configured field; null when no field is configured.</summary>
    public StatusFieldChange? Field { get; }

    /// <summary>The whole tag field written: the tags it has, then the new ones; null when nothing is added.</summary>
    public string? NewTags => TagsToAdd.Count == 0
        ? null
        : string.Join(StatusUpdatePlanner.TagJoiner, WorkItem.SplitTags(Item.Tags).Concat(TagsToAdd));

    /// <summary>Whether the click writes anything.</summary>
    public bool HasChanges => TagsToAdd.Count > 0 || Field?.Outcome == StatusFieldOutcome.Set;

    /// <summary>
    /// Whether this plan, read again after <paramref name="approved"/> was shown, writes nothing the person did not
    /// approve: its tags are among the approved ones (someone else may have added the rest), and the field is set from
    /// the same value to the same value, or was not to be set, or already has the approved value. Any other change to
    /// the work item does not count, since writing this plan keeps it.
    /// </summary>
    public bool StaysWithin(StatusUpdatePlan approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var approvedTags = new HashSet<string>(approved.TagsToAdd, StringComparer.OrdinalIgnoreCase);
        return TagsToAdd.All(approvedTags.Contains) && FieldStaysWithin(approved.Field);
    }

    private bool FieldStaysWithin(StatusFieldChange? approved)
    {
        if (approved is not { Outcome: StatusFieldOutcome.Set })
        {
            return Field?.Outcome != StatusFieldOutcome.Set;
        }

        // The approved value reached meanwhile counts as done: nothing more is written to the field.
        if (Field is not { Outcome: StatusFieldOutcome.Set } field)
        {
            return string.Equals(Field?.Current, approved.NewValue, StringComparison.Ordinal);
        }

        return string.Equals(field.Current, approved.Current, StringComparison.Ordinal)
            && string.Equals(field.NewValue, approved.NewValue, StringComparison.Ordinal);
    }

    /// <summary>What the click changes, in words: "added PT Merged · Status In Dev → In PT".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (TagsToAdd.Count > 0)
        {
            parts.Add($"added {string.Join(", ", TagsToAdd)}");
        }

        if (Field is { Outcome: StatusFieldOutcome.Set } field)
        {
            parts.Add($"{field.Name} {field.Describe()}");
        }

        return parts.Count == 0 ? "nothing to change" : string.Join(" · ", parts);
    }
}

/// <summary>What a click on Update came to.</summary>
public enum StatusUpdateResult
{
    /// <summary>The change was written, by this click or by an earlier one whose answer never came.</summary>
    Written,

    /// <summary>Nothing was written: the work item already had everything that can be set on it.</summary>
    NothingToChange,

    /// <summary>
    /// Nothing was written: the work item changed after the dialog read it, and the change is now another than the one
    /// the dialog showed, so the person sees the new one first.
    /// </summary>
    ChangedMeanwhile
}

/// <summary>A click on Update: what came of it, and the plan it ended with, the one written or the new one to show.</summary>
public sealed record StatusUpdateOutcome(StatusUpdateResult Result, StatusUpdatePlan Plan);

/// <summary>
/// Works out what Update status changes on one work item, from the person's settings and what the work item holds now.
/// Kept apart from the page and the TFS calls so the rules can be tested on their own.
/// </summary>
public static class StatusUpdatePlanner
{
    /// <summary>How TFS writes the tag field: the tags one after another, each followed by "; " but the last.</summary>
    public const string TagJoiner = "; ";

    /// <summary>
    /// The change for <paramref name="item"/>. Tags are added to those it has, never replacing them; ones it has in any
    /// case are not added again. The field is set only when its type has it with a fixed list of values that has the
    /// value: a type without the field, a field without a fixed list of values on this type, or a value the list does
    /// not have, leaves the field as it is and says why.
    /// </summary>
    /// <param name="typeFields">The fields of the work item's type; only read when a field is configured.</param>
    public static StatusUpdatePlan Plan(StatusUpdateSettings settings, WorkItemForUpdate item, IReadOnlyCollection<WorkItemTypeField> typeFields)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(typeFields);

        // Add answers false for a tag the work item has in any case, or one listed twice, so each is added once.
        var have = new HashSet<string>(WorkItem.SplitTags(item.Tags), StringComparer.OrdinalIgnoreCase);
        var toAdd = settings.HasTagsToAdd() ? settings.Tags.Where(have.Add).ToList() : new List<string>();
        return new StatusUpdatePlan(item, toAdd, settings.HasFieldToSet() ? PlanField(settings, item, typeFields) : null);
    }

    /// <summary>A field value as the page shows it: "(empty)" for none.</summary>
    public static string Shown(string value) => value.Length == 0 ? "(empty)" : value;

    private static StatusFieldChange PlanField(StatusUpdateSettings settings, WorkItemForUpdate item, IReadOnlyCollection<WorkItemTypeField> typeFields)
    {
        var field = typeFields.FirstOrDefault(f => string.Equals(f.ReferenceName, settings.Field, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            var name = settings.ShownFieldName();
            return StatusFieldChange.Skipped(settings.Field, name, item.FieldValue, $"A {item.Type} has no {name} field, so this one has none to set.");
        }

        // Only a value from the field's own list, in its spelling. Settings offers only fields with a list; one without a
        // list on this type (free text, a person, a path, the tags) is never written, whatever a settings file says.
        var value = field.AllowedValues.FirstOrDefault(v => string.Equals(v, settings.Value, StringComparison.OrdinalIgnoreCase));
        if (value is null)
        {
            var why = field.AllowedValues.Count == 0
                ? $"{field.Name} has no fixed list of values on a {item.Type}, so Update status does not set it."
                : $"{field.Name} cannot be \"{settings.Value}\" on a {item.Type}, so it stays {Shown(item.FieldValue)}.";
            return StatusFieldChange.Skipped(field.ReferenceName, field.Name, item.FieldValue, why);
        }

        return string.Equals(item.FieldValue, value, StringComparison.Ordinal)
            ? StatusFieldChange.AlreadySet(field.ReferenceName, field.Name, item.FieldValue)
            : StatusFieldChange.Set(field.ReferenceName, field.Name, item.FieldValue, value);
    }
}
