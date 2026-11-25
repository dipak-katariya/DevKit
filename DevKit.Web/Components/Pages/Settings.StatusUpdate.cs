using System.Net;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// The settings of the Code Merging Sheet's Update status button: the tags it adds and the field it sets. Part of the
/// page's form like every other preference, so it is saved by Save settings. Its pickers list a project's tags and the
/// fields with a fixed list of values, read from TFS once one of the two parts is turned on.
/// </summary>
public sealed partial class Settings
{
    /// <summary>The field Set a field starts on when the project has one by that name.</summary>
    private const string PreferredStatusField = "Status";

    private const string TagsStillTypable = " You can still type tags.";

    private const string NoTagListText = "This TFS does not list a project's tags. Type the tags instead.";

    private const string NoStatusProjectText = "Choose a project above to list its tags and fields.";

    // The section as edited; the saved one is in `saved`.
    private StatusUpdateSettings statusForm = new();
    private HashSet<string> statusTagSet = new(StringComparer.OrdinalIgnoreCase);
    private string statusTagMsg = "";

    // Set a field was turned on while the lists were still being read: the Status field is chosen once they arrive.
    private bool statusPickPreferredField;

    // The pickers' lists, read from statusProject.
    private string statusProject = "";
    private IReadOnlyList<string> statusTagChoices = Array.Empty<string>();
    private IReadOnlyList<WorkItemTypeField> statusFieldChoices = Array.Empty<WorkItemTypeField>();
    private bool statusListsLoading;
    private bool statusListsRead;
    private string? statusListsError;

    // This TFS answered that it has no tag list; it is not asked again while the page is open.
    private bool statusNoTagList;

    // Counts the loads, so only the newest one (the project now chosen) fills the lists and ends the spinner.
    private int statusListsLoad;

    /// <summary>Fills the section from what is saved.</summary>
    private void ReadStatusUpdate()
    {
        SetStatusForm(saved.StatusUpdateOver(new StatusUpdateSettings()));
        statusTagMsg = "";
    }

    /// <summary>Puts this page's own edits to the section back after it followed a save made elsewhere.</summary>
    private void KeepStatusEdits(PortableSettings edits) => SetStatusForm(edits.StatusUpdateOver(statusForm));

    private void SetStatusForm(StatusUpdateSettings form)
    {
        statusForm = form;
        statusTagSet = new HashSet<string>(statusForm.Tags, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the section holds, in words, under it.</summary>
    private string StatusSummary
    {
        get
        {
            if (!statusForm.HasWork())
            {
                return "Update status is not set up, so the sheet's button is off.";
            }

            var summary = $"Update status {statusForm.Describe()}.";
            if (statusForm.HasTagsToAdd())
            {
                summary += " A work item keeps its other tags.";
            }

            if (statusForm.HasFieldToSet())
            {
                var name = statusForm.ShownFieldName();
                summary += $" A work item whose type has no {name} field, or does not allow {statusForm.Value}, keeps its {name}.";
            }

            return summary;
        }
    }

    private async Task OnStatusAddTagsChanged(ChangeEventArgs e)
    {
        statusForm.AddTags = e.Value is true;
        statusTagMsg = "";
        await EnsureStatusListsAsync();
    }

    private async Task OnStatusSetFieldChanged(ChangeEventArgs e)
    {
        statusForm.SetField = e.Value is true;
        statusPickPreferredField = statusForm.SetField && statusForm.Field.Length == 0;
        await EnsureStatusListsAsync();
        PickPreferredStatusField();
    }

    /// <summary>
    /// Turning Set a field on starts on the Status field, when the project has one and nothing was chosen before; when
    /// the lists are still being read, once they arrive.
    /// </summary>
    private void PickPreferredStatusField()
    {
        if (!statusPickPreferredField || statusListsLoading)
        {
            return;
        }

        statusPickPreferredField = false;
        if (statusForm.SetField && statusForm.Field.Length == 0
            && statusFieldChoices.FirstOrDefault(f => string.Equals(f.Name, PreferredStatusField, StringComparison.OrdinalIgnoreCase)) is { } preferred)
        {
            ChooseStatusField(preferred);
        }
    }

    /// <summary>The multi-select works with a set; the order tags were picked in is kept, and new ones go last.</summary>
    private void OnStatusTagsChanged(HashSet<string> selected)
    {
        var ordered = statusForm.Tags.Where(selected.Contains).ToList();
        ordered.AddRange(selected.Except(ordered, StringComparer.OrdinalIgnoreCase).ToList());
        statusTagMsg = ordered.Count > StatusUpdateSettings.MaxTags
            ? $"Only the first {StatusUpdateSettings.MaxTags} tags were kept."
            : "";
        SetStatusTags(StatusUpdateSettings.CleanTags(ordered));
    }

    /// <summary>A tag typed in the picker that the project does not have yet; TFS makes it when it is first added.</summary>
    private void OnStatusTagCreated(string tag)
    {
        statusTagMsg = StatusUpdateSettings.TagProblem(tag) ?? "";
        if (statusTagMsg.Length > 0)
        {
            return;
        }

        if (statusForm.Tags.Count >= StatusUpdateSettings.MaxTags)
        {
            statusTagMsg = $"At most {StatusUpdateSettings.MaxTags} tags. Remove one first.";
            return;
        }

        SetStatusTags(StatusUpdateSettings.CleanTags(statusForm.Tags.Append(tag)));
    }

    private void RemoveStatusTag(string tag)
    {
        statusTagMsg = "";
        SetStatusTags(statusForm.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
    }

    private void SetStatusTags(IEnumerable<string> tags)
    {
        statusForm.Tags = tags.ToList();
        statusTagSet = new HashSet<string>(statusForm.Tags, StringComparer.OrdinalIgnoreCase);
    }

    private void OnStatusFieldChange(ChangeEventArgs e)
    {
        var reference = e.Value?.ToString() ?? "";
        if (statusFieldChoices.FirstOrDefault(f => string.Equals(f.ReferenceName, reference, StringComparison.OrdinalIgnoreCase)) is { } choice)
        {
            ChooseStatusField(choice);
        }
    }

    /// <summary>A field change keeps the value only when the new field allows it.</summary>
    private void ChooseStatusField(WorkItemTypeField field)
    {
        statusForm.Field = field.ReferenceName;
        statusForm.FieldName = field.Name;
        if (!field.AllowedValues.Contains(statusForm.Value, StringComparer.OrdinalIgnoreCase))
        {
            statusForm.Value = "";
        }
    }

    private void OnStatusValueChange(ChangeEventArgs e) => statusForm.Value = e.Value?.ToString() ?? "";

    private async Task OnStatusProjectChange(ChangeEventArgs e)
    {
        statusProject = e.Value?.ToString() ?? "";
        await LoadStatusListsAsync();
    }

    /// <summary>Reads the lists once, when either part is on, so a visit to Settings costs nothing extra otherwise.</summary>
    private async Task EnsureStatusListsAsync()
    {
        if ((statusForm.AddTags || statusForm.SetField) && !statusListsRead && !statusListsLoading && SettingsSvc.IsConfigured)
        {
            await LoadStatusListsAsync();
        }
    }

    private async Task LoadStatusListsAsync()
    {
        if (statusProject.Length == 0)
        {
            statusProject = DefaultStatusProject();
        }

        if (statusProject.Length == 0)
        {
            Logger.LogDebug("No project to read the Update status tags and fields from: no default area, repository or project list yet");
            statusListsError = NoStatusProjectText;
            return;
        }

        var project = statusProject;
        var load = ++statusListsLoad;
        statusListsLoading = true;
        statusListsError = null;
        StateHasChanged();

        // Apart, so a server without the tags list still offers its fields; together, so neither waits for the other.
        var tagsRead = ReadStatusTagsAsync(project);
        var fieldsRead = ReadStatusFieldsAsync(project);
        await Task.WhenAll(tagsRead, fieldsRead);
        if (load != statusListsLoad)
        {
            return;
        }

        var (tags, fields) = (await tagsRead, await fieldsRead);
        statusListsLoading = false;
        statusTagChoices = tags.Value ?? Array.Empty<string>();
        statusFieldChoices = fields.Value ?? Array.Empty<WorkItemTypeField>();
        statusListsRead = (tags.Value is not null || statusNoTagList) && fields.Value is not null;
        var errors = new[] { tags.Error, fields.Error }.Where(error => error is not null).ToList();
        statusListsError = errors.Count == 0 ? null : string.Join(" ", errors);
        PickPreferredStatusField();
    }

    /// <summary>The project's tags, or why they could not be read; a TFS without a tag list is asked once.</summary>
    private async Task<(IReadOnlyList<string>? Value, string? Error)> ReadStatusTagsAsync(string project)
    {
        if (statusNoTagList)
        {
            return (null, NoTagListText);
        }

        return await ReadStatusListAsync(project, "tags", ct => StatusUpdates.GetTagsAsync(project, ct), ex =>
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest })
            {
                statusNoTagList = true;
                return NoTagListText;
            }

            return $"Couldn't read the tags of {project}: {TfsErrors.Describe(ex)}{TagsStillTypable}";
        });
    }

    private Task<(IReadOnlyList<WorkItemTypeField>? Value, string? Error)> ReadStatusFieldsAsync(string project) =>
        ReadStatusListAsync(project, "fields", ct => StatusUpdates.GetFieldChoicesAsync(project, ct),
            ex => $"Couldn't read the fields of {project}: {TfsErrors.Describe(ex)}");

    /// <summary>
    /// One of the two lists, or why it could not be read, in the words <paramref name="failure"/> gives. Leaving the page
    /// reads nothing and says nothing.
    /// </summary>
    private async Task<(T? Value, string? Error)> ReadStatusListAsync<T>(
        string project, string what, Func<CancellationToken, Task<T>> read, Func<Exception, string> failure)
        where T : class
    {
        try
        {
            return (await read(page.Token), null);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The page was left while the {What} of {Project} were being read", what, project);
            return (null, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The {What} of {Project} could not be read for Update status", what, project);
            return (null, failure(ex));
        }
    }

    /// <summary>The project of the default area, else of the default repository, else the first one.</summary>
    private string DefaultStatusProject() =>
        AreaSprintSelect.Parse(defaultAreaPath)?.Project
        ?? repos.FirstOrDefault(r => string.Equals(r.Id, defaultRepoId, StringComparison.OrdinalIgnoreCase))?.Project
        ?? PageState.TfsData.Projects.FirstOrDefault()?.Name
        ?? "";

    /// <summary>The chosen tags first, in their order, then the project's other tags by name.</summary>
    private List<MultiSelectDropdown.Option> StatusTagOptions() => statusForm.Tags
        .Concat(statusTagChoices.Where(tag => !statusTagSet.Contains(tag)))
        .Select(tag => new MultiSelectDropdown.Option { Value = tag, Label = tag })
        .ToList();

    /// <summary>The fields with a fixed list of values, by name, and the saved one even before TFS has answered.</summary>
    private List<GlassDropdown.DropdownOption> StatusFieldOptions()
    {
        var options = statusFieldChoices
            .Select(f => new GlassDropdown.DropdownOption { Value = f.ReferenceName, Label = f.Name })
            .ToList();
        var field = statusForm.Field;
        if (field.Length > 0 && !options.Exists(o => string.Equals(o.Value, field, StringComparison.OrdinalIgnoreCase)))
        {
            // Before TFS answers, the saved name; after, a field the project does not offer shows as what it is.
            var label = statusListsRead ? $"{field} (not offered for this project)" : statusForm.ShownFieldName();
            options.Insert(0, new GlassDropdown.DropdownOption { Value = field, Label = label });
        }

        return options;
    }

    /// <summary>The values the chosen field allows, and the saved one even before TFS has answered.</summary>
    private List<GlassDropdown.DropdownOption> StatusValueOptions()
    {
        var allowed = statusFieldChoices
            .FirstOrDefault(f => string.Equals(f.ReferenceName, statusForm.Field, StringComparison.OrdinalIgnoreCase))?.AllowedValues
            ?? (IReadOnlyList<string>)Array.Empty<string>();
        var values = allowed.ToList();
        if (statusForm.Value.Length > 0 && !values.Contains(statusForm.Value, StringComparer.OrdinalIgnoreCase))
        {
            values.Insert(0, statusForm.Value);
        }

        return values.Select(v => new GlassDropdown.DropdownOption { Value = v, Label = v }).ToList();
    }

    private List<GlassDropdown.DropdownOption> StatusProjectOptions() => PageState.TfsData.Projects
        .Select(p => new GlassDropdown.DropdownOption { Value = p.Name, Label = p.Name })
        .ToList();
}
