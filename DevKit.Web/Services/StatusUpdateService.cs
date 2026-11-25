using System.Net;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// The Code Merging Sheet's Update status. It reads a work item, works out what the person's settings change on it
/// (<see cref="StatusUpdatePlanner"/>), and writes only that, guarded by the revision it read. It also gives the
/// Settings page its lists: a project's tags, and the fields with a fixed list of values on the work item types the
/// sheet shows. Lists are kept for the circuit for <see cref="ListLifetime"/>, so a dialog or a Settings visit costs
/// no new reads. Nothing here logs a failure it throws: the page that called logs it once.
/// </summary>
public sealed class StatusUpdateService
{
    /// <summary>Tags and fields change rarely; a list this old is read again.</summary>
    private static readonly TimeSpan ListLifetime = TimeSpan.FromMinutes(10);

    private readonly TfsApiService _tfs;
    private readonly TimeProvider _clock;
    private readonly ILogger<StatusUpdateService> _logger;

    // One circuit's page events run one at a time, but a list read can finish on another thread, so the maps are locked.
    private readonly object _sync = new();
    private readonly Dictionary<string, Kept<IReadOnlyList<string>>> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Kept<IReadOnlyList<string>>> _typeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Kept<IReadOnlyList<WorkItemTypeField>>> _typeFields = new(StringComparer.OrdinalIgnoreCase);

    // Work items whose last write got no answer, so a later click that finds nothing to do can say that write went through.
    private readonly HashSet<int> _unanswered = new();

    public StatusUpdateService(TfsApiService tfs, TimeProvider clock, ILogger<StatusUpdateService> logger)
    {
        _tfs = tfs;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The tags used in <paramref name="project"/>, by name.</summary>
    public Task<IReadOnlyList<string>> GetTagsAsync(string project, CancellationToken ct) =>
        KeptAsync(_tags, project, async token => (IReadOnlyList<string>)await _tfs.GetProjectTagsAsync(project, token), ct);

    /// <summary>
    /// The fields that have a fixed list of values on the sheet's work item types in <paramref name="project"/>, each
    /// with every value any of those types allows, by name. These are what Update status can set.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemTypeField>> GetFieldChoicesAsync(string project, CancellationToken ct)
    {
        var names = await KeptAsync(_typeNames, project, async token => (IReadOnlyList<string>)await _tfs.GetWorkItemTypeNamesAsync(project, token), ct);
        var present = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var perType = await Task.WhenAll(CodeMergingService.SheetWorkItemTypes
            .Where(present.Contains)
            .Select(type => GetTypeFieldsAsync(project, type, ct)));
        return MergeChoices(perType);
    }

    /// <summary>
    /// Reads work item <paramref name="workItemId"/> and works out what <paramref name="settings"/> change on it, for the
    /// person to see before anything is written.
    /// </summary>
    public async Task<StatusUpdatePlan> PreviewAsync(int workItemId, StatusUpdateSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var item = await _tfs.GetWorkItemForUpdateAsync(workItemId, settings.HasFieldToSet() ? settings.Field : null, ct);
        IReadOnlyCollection<WorkItemTypeField> fields = settings.HasFieldToSet()
            ? await GetTypeFieldsAsync(item.Project, item.Type, ct)
            : Array.Empty<WorkItemTypeField>();
        return StatusUpdatePlanner.Plan(settings, item, fields);
    }

    /// <summary>
    /// Writes <paramref name="plan"/>, guarded by the revision it was made from. When the work item changed in the
    /// meantime TFS refuses that write, and the work item is read again. The new change is written at once when it
    /// stays within the one the dialog showed (a tag or field someone else changed is kept, and a configured tag someone
    /// else added is simply not added again); when it is another, nothing is written and the new plan comes back for the
    /// person to see first. When an earlier click on this same plan got no answer and the work item now has it all, that
    /// click wrote it, and the outcome says so.
    /// </summary>
    public async Task<StatusUpdateOutcome> ApplyAsync(StatusUpdatePlan plan, StatusUpdateSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        if (!plan.HasChanges)
        {
            return new StatusUpdateOutcome(StatusUpdateResult.NothingToChange, plan);
        }

        _logger.LogInformation("Updating work item {WorkItemId}: {TagCount} tag(s) to add, field {Field} to set",
            plan.Item.Id, plan.TagsToAdd.Count, plan.Field?.NewValue is null ? "none" : plan.Field.Reference);
        try
        {
            return await WriteAsync(plan, ct);
        }
        catch (HttpRequestException ex) when (IsConflict(ex))
        {
            _logger.LogError(ex, "Work item {WorkItemId} changed after it was read for Update status; it is read again", plan.Item.Id);
            return await RetryAfterConflictAsync(plan, settings, ct);
        }
    }

    private async Task<StatusUpdateOutcome> RetryAfterConflictAsync(StatusUpdatePlan plan, StatusUpdateSettings settings, CancellationToken ct)
    {
        var fresh = await PreviewAsync(plan.Item.Id, settings, ct);
        if (!fresh.HasChanges)
        {
            return NothingLeftToWrite(plan, fresh);
        }

        if (!fresh.StaysWithin(plan))
        {
            _logger.LogInformation("Work item {WorkItemId} was not updated: the change is another after it was read again", plan.Item.Id);
            return new StatusUpdateOutcome(StatusUpdateResult.ChangedMeanwhile, fresh);
        }

        try
        {
            return await WriteAsync(fresh, ct);
        }
        catch (HttpRequestException ex) when (IsConflict(ex))
        {
            // Changed again between the read and the write: the person sees the latest plan rather than a raw refusal.
            _logger.LogError(ex, "Work item {WorkItemId} changed again before Update status could write it", plan.Item.Id);
            return new StatusUpdateOutcome(StatusUpdateResult.ChangedMeanwhile, fresh);
        }
    }

    private StatusUpdateOutcome NothingLeftToWrite(StatusUpdatePlan plan, StatusUpdatePlan fresh)
    {
        if (ForgetUnanswered(plan))
        {
            _logger.LogInformation("Work item {WorkItemId} was updated by the earlier click whose answer never came: {Summary}",
                plan.Item.Id, plan.Summary());
            return new StatusUpdateOutcome(StatusUpdateResult.Written, plan);
        }

        _logger.LogInformation("Work item {WorkItemId} was not updated: it already had the values when it was read again", plan.Item.Id);
        return new StatusUpdateOutcome(StatusUpdateResult.NothingToChange, fresh);
    }

    private async Task<StatusUpdateOutcome> WriteAsync(StatusUpdatePlan plan, CancellationToken ct)
    {
        try
        {
            await _tfs.UpdateWorkItemStatusAsync(plan, ct);
        }
        catch (Exception ex) when (NoteFailedWrite(plan, ex))
        {
            // NoteFailedWrite returns false, so this never runs: the failure goes to the page, which logs it once.
            throw;
        }

        ForgetUnanswered(plan);
        _logger.LogInformation("Updated work item {WorkItemId}: {Summary}", plan.Item.Id, plan.Summary());
        return new StatusUpdateOutcome(StatusUpdateResult.Written, plan);
    }

    /// <summary>
    /// Marks the failure as one of the write itself, for <see cref="TfsErrors.AnswerNeverCame"/>, and remembers a plan
    /// whose answer never came, so the next click on it can tell that click's work from nothing to do. Returns false.
    /// </summary>
    private bool NoteFailedWrite(StatusUpdatePlan plan, Exception ex)
    {
        TfsErrors.NoteWriteSent(ex);
        if (TfsErrors.AnswerNeverCame(ex))
        {
            lock (_sync)
            {
                _unanswered.Add(plan.Item.Id);
            }
        }

        return false;
    }

    /// <summary>Forgets a plan's unanswered write; whether there was one.</summary>
    private bool ForgetUnanswered(StatusUpdatePlan plan)
    {
        lock (_sync)
        {
            return _unanswered.Remove(plan.Item.Id);
        }
    }

    // TFS refuses a write guarded by an old revision with 412; some versions answer 409 instead.
    private static bool IsConflict(HttpRequestException ex) =>
        ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;

    private Task<IReadOnlyList<WorkItemTypeField>> GetTypeFieldsAsync(string project, string type, CancellationToken ct) =>
        KeptAsync(_typeFields, $"{project}|{type}",
            async token => (IReadOnlyList<WorkItemTypeField>)await _tfs.GetWorkItemTypeFieldsAsync(project, type, token), ct);

    /// <summary>The fields with a fixed list of values, once each, with every value any type allows, in their order.</summary>
    private static List<WorkItemTypeField> MergeChoices(IEnumerable<IReadOnlyList<WorkItemTypeField>> perType)
    {
        var merged = new Dictionary<string, (string Name, List<string> Values, HashSet<string> Seen)>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in perType.SelectMany(fields => fields).Where(field => field.AllowedValues.Count > 0))
        {
            if (!merged.TryGetValue(field.ReferenceName, out var entry))
            {
                entry = (field.Name, new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                merged[field.ReferenceName] = entry;
            }

            entry.Values.AddRange(field.AllowedValues.Where(entry.Seen.Add));
        }

        return merged
            .Select(pair => new WorkItemTypeField(pair.Key, pair.Value.Name, pair.Value.Values))
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A list from what is kept, or read now and kept when it is not there or too old. A failed read is not kept. The
    /// key includes the TFS connection, so after a switch to another server the same project name is read again.
    /// </summary>
    private async Task<T> KeptAsync<T>(Dictionary<string, Kept<T>> kept, string listKey, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var key = $"{_tfs.ConnectionKey}|{listKey}";
        var now = _clock.GetUtcNow();
        lock (_sync)
        {
            if (kept.TryGetValue(key, out var entry) && now - entry.ReadAt < ListLifetime)
            {
                return entry.Value;
            }
        }

        var value = await read(ct);
        lock (_sync)
        {
            kept[key] = new Kept<T>(value, now);
        }

        return value;
    }

    private sealed record Kept<T>(T Value, DateTimeOffset ReadAt);
}
