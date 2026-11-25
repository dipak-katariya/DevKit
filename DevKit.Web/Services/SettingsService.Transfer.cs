using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>What an import did: whether the settings were taken, and what to tell the person.</summary>
public sealed record SettingsImportResult(bool Imported, string Message);

/// <summary>
/// The preferences: what the settings file people download and load again holds, and what the Settings page edits.
/// Never the TFS connection: the server address, how DevKit signs in and the personal access token stay out of it, so
/// a shared or mislaid file gives nobody access to TFS, and loading one never changes the connection in use. A null
/// field is one a file does not name, or (as changes) one that did not change, and is left as it is.
/// </summary>
public sealed class PortableSettings
{
    public const string FormatName = "DevKit settings";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = "";
    public int Version { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public string? TeamName { get; set; }
    public string? DefaultAreaPath { get; set; }
    public string? DefaultSprint { get; set; }
    public DefaultSprintMode? DefaultSprintMode { get; set; }
    public string? DefaultRepoId { get; set; }
    public int? MostUsedRepoCount { get; set; }
    public List<string>? MostUsedRepoIds { get; set; }
    public CapacityPlanningSettings? CapacityPlanning { get; set; }
    public List<string>? CodeMergingRepoIds { get; set; }
    public Dictionary<string, RepoBranchDefaults>? RepoBranches { get; set; }

    // What the Code Merging Sheet's Update status button does (StatusUpdateSettings), so a form that changed the tags
    // saves the tags alone. The field, its name and its value are one choice: they are named together or not at all.
    public bool? StatusAddTags { get; set; }
    public List<string>? StatusTags { get; set; }
    public bool? StatusSetField { get; set; }
    public string? StatusField { get; set; }
    public string? StatusFieldName { get; set; }
    public string? StatusValue { get; set; }

    /// <summary>Update status settings a later DevKit added, carried through export and import as they are.</summary>
    public Dictionary<string, JsonElement>? StatusUnknownFields { get; set; }

    /// <summary>Whether any preference is named: false for changes that change nothing.</summary>
    public bool HasChanges => new object?[]
    {
        TeamName, DefaultAreaPath, DefaultSprint, DefaultSprintMode, DefaultRepoId, MostUsedRepoCount, MostUsedRepoIds,
        CapacityPlanning, CodeMergingRepoIds, RepoBranches,
        StatusAddTags, StatusTags, StatusSetField, StatusField, StatusFieldName, StatusValue, StatusUnknownFields
    }.Any(preference => preference is not null);

    /// <summary>
    /// Names the Update status settings of <paramref name="status"/>; names nothing when it is null. With
    /// <paramref name="isNamed"/>, only the ones it names by their <see cref="StatusUpdateSettings"/> property name, as
    /// a file to import does: naming the field, its name or its value names all three, since they go together.
    /// </summary>
    public PortableSettings WithStatusUpdate(StatusUpdateSettings? status, Func<string, bool>? isNamed = null)
    {
        if (status is null)
        {
            return this;
        }

        var named = isNamed ?? (_ => true);
        StatusAddTags = named(nameof(StatusUpdateSettings.AddTags)) ? status.AddTags : null;
        StatusTags = named(nameof(StatusUpdateSettings.Tags)) ? status.Tags.ToList() : null;
        StatusSetField = named(nameof(StatusUpdateSettings.SetField)) ? status.SetField : null;
        var fieldNamed = FieldChoice.Any(named);
        StatusField = fieldNamed ? status.Field : null;
        StatusFieldName = fieldNamed ? status.FieldName : null;
        StatusValue = fieldNamed ? status.Value : null;
        StatusUnknownFields = status.UnknownFields is { Count: > 0 } unknown ? new Dictionary<string, JsonElement>(unknown) : null;
        return this;
    }

    private static readonly string[] FieldChoice =
    {
        nameof(StatusUpdateSettings.Field), nameof(StatusUpdateSettings.FieldName), nameof(StatusUpdateSettings.Value)
    };

    /// <summary>
    /// The Update status settings named here laid over <paramref name="current"/>, as a new, cleaned copy (what cleaning drops is logged to <paramref name="logger"/> when given); what is not
    /// named keeps its value there. <paramref name="current"/> itself is not changed.
    /// </summary>
    public StatusUpdateSettings StatusUpdateOver(StatusUpdateSettings current, ILogger? logger = null)
    {
        var copy = LaidOver(current);
        return logger is null ? copy.Normalized() : copy.Normalized(logger);
    }

    /// <summary>What cleaning leaves out of the Update status settings named here laid over <paramref name="current"/>.</summary>
    public IReadOnlyList<string> StatusUpdateDropsOver(StatusUpdateSettings current) => LaidOver(current).NormalizingDrops();

    private StatusUpdateSettings LaidOver(StatusUpdateSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var copy = current.Normalized();
        copy.AddTags = StatusAddTags ?? copy.AddTags;
        copy.Tags = StatusTags?.ToList() ?? copy.Tags;
        copy.SetField = StatusSetField ?? copy.SetField;
        copy.Field = StatusField ?? copy.Field;
        copy.FieldName = StatusFieldName ?? copy.FieldName;
        copy.Value = StatusValue ?? copy.Value;
        foreach (var (name, value) in StatusUnknownFields ?? new Dictionary<string, JsonElement>())
        {
            copy.UnknownFields ??= new Dictionary<string, JsonElement>();
            copy.UnknownFields[name] = value;
        }

        return copy;
    }

    /// <summary>
    /// What changed from <paramref name="saved"/> to these preferences (a form as edited): only the fields that differ,
    /// and only the repositories whose branches differ. The sprint and how it is chosen go together, and so do the
    /// Update status field, its name and its value, so a save never pairs one tab's field with another tab's value.
    /// </summary>
    public PortableSettings ChangesFrom(PortableSettings saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var sprint = Changed(DefaultSprint, saved.DefaultSprint);
        var fieldChoice = Changed(StatusField, saved.StatusField) is not null
            || Changed(StatusFieldName, saved.StatusFieldName) is not null
            || Changed(StatusValue, saved.StatusValue) is not null;
        return new PortableSettings
        {
            TeamName = Changed(TeamName, saved.TeamName),
            DefaultAreaPath = Changed(DefaultAreaPath, saved.DefaultAreaPath),
            DefaultSprint = sprint,
            DefaultSprintMode = sprint is not null || DefaultSprintMode != saved.DefaultSprintMode ? DefaultSprintMode : null,
            DefaultRepoId = Changed(DefaultRepoId, saved.DefaultRepoId),
            MostUsedRepoCount = MostUsedRepoCount != saved.MostUsedRepoCount ? MostUsedRepoCount : null,
            MostUsedRepoIds = Changed(MostUsedRepoIds, saved.MostUsedRepoIds),
            CodeMergingRepoIds = Changed(CodeMergingRepoIds, saved.CodeMergingRepoIds),
            RepoBranches = ChangedBranches(RepoBranches, saved.RepoBranches),
            StatusAddTags = Changed(StatusAddTags, saved.StatusAddTags),
            StatusTags = Changed(StatusTags, saved.StatusTags),
            StatusSetField = Changed(StatusSetField, saved.StatusSetField),
            StatusField = fieldChoice ? StatusField : null,
            StatusFieldName = fieldChoice ? StatusFieldName : null,
            StatusValue = fieldChoice ? StatusValue : null
        };
    }

    private static string? Changed(string? now, string? before) =>
        now is not null && !string.Equals(now, before, StringComparison.Ordinal) ? now : null;

    private static bool? Changed(bool? now, bool? before) => now is not null && now != before ? now : null;

    private static List<string>? Changed(List<string>? now, List<string>? before) =>
        now is not null && !now.SequenceEqual(before ?? new List<string>(), StringComparer.Ordinal) ? now : null;

    /// <summary>The repositories whose branches differ, each with its branches now (both empty when they were cleared).</summary>
    private static Dictionary<string, RepoBranchDefaults>? ChangedBranches(
        Dictionary<string, RepoBranchDefaults>? now, Dictionary<string, RepoBranchDefaults>? before)
    {
        if (now is null)
        {
            return null;
        }

        var kept = before ?? new Dictionary<string, RepoBranchDefaults>();
        var changed = new Dictionary<string, RepoBranchDefaults>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in now.Keys.Union(kept.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var after = now.GetValueOrDefault(id) ?? new RepoBranchDefaults();
            var was = kept.GetValueOrDefault(id) ?? new RepoBranchDefaults();
            if (!string.Equals(after.QaBranch, was.QaBranch, StringComparison.Ordinal)
                || !string.Equals(after.MergingBranch, was.MergingBranch, StringComparison.Ordinal))
            {
                changed[id] = new RepoBranchDefaults { QaBranch = after.QaBranch, MergingBranch = after.MergingBranch };
            }
        }

        return changed.Count > 0 ? changed : null;
    }
}

/// <summary>
/// Exporting the preferences to a file, importing them from one (<see cref="PortableSettings"/>, or the whole settings
/// file an earlier DevKit kept on a PC), and saving the ones the Settings page changed.
/// </summary>
public partial class SettingsService
{
    /// <summary>The largest settings file DevKit reads; a real one is a few kilobytes.</summary>
    public const int MaxImportBytes = 256 * 1024;

    // A settings file never names more repositories than a TFS collection has; this keeps a crafted one small.
    private const int MaxImportedRepositories = 2000;

    private const string NotSettingsFileMessage = "This file is not a DevKit settings file.";

    // Files people may have edited by hand: names in any case, comments and trailing commas are read.
    private static readonly JsonDocumentOptions ImportDocument = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions ImportFormat = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// The preferences as saved now, as a copy: what the Settings page starts from, and compares its form with to know
    /// what changed.
    /// </summary>
    public PortableSettings CurrentPreferences()
    {
        lock (_sync)
        {
            return new PortableSettings
            {
                Format = PortableSettings.FormatName,
                Version = PortableSettings.CurrentVersion,
                TeamName = _settings.TeamName,
                DefaultAreaPath = _settings.DefaultAreaPath,
                DefaultSprint = _settings.DefaultSprint,
                DefaultSprintMode = DefaultSprintIsCurrent ? DefaultSprintMode.Current : DefaultSprintMode.Chosen,
                DefaultRepoId = _settings.DefaultRepoId,
                MostUsedRepoCount = MostUsedRepoCount,
                MostUsedRepoIds = _settings.MostUsedRepoIds.ToList(),
                CapacityPlanning = _settings.CapacityPlanning?.Normalized(),
                CodeMergingRepoIds = _settings.CodeMergingRepoIds.ToList(),
                RepoBranches = CleanBranches(_settings.RepoBranches)
                    .Where(pair => !pair.Value.IsEmpty)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            }.WithStatusUpdate(_settings.StatusUpdate.Normalized());
        }
    }

    /// <summary>The settings as a file to download, without the TFS connection.</summary>
    public string ExportSettings()
    {
        var portable = CurrentPreferences();
        portable.ExportedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Settings exported: {Repositories} repositories with branch defaults", portable.RepoBranches?.Count ?? 0);
        return JsonSerializer.Serialize(portable, FileFormat);
    }

    /// <summary>
    /// Saves the preferences <paramref name="changes"/> names, together in one save, and leaves every other setting as
    /// it is. Returns false, with the reason in <see cref="LastSaveError"/>, when they were not kept; the settings in use
    /// then stay the ones stored, so a page that puts its form back as saved shows what is really stored.
    /// </summary>
    public bool SavePreferences(PortableSettings changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return UpdateOrRollBack(() => ApplyPreferences(changes));
    }

    /// <summary>
    /// Takes the preferences in <paramref name="text"/> and saves them: a file made by <see cref="ExportSettings"/>, or
    /// the whole settings file an earlier DevKit kept on a PC. Only what the file names changes; everything else is kept.
    /// The TFS connection is never read from the file nor changed. A file that is not DevKit settings, or comes from a
    /// newer DevKit, changes nothing.
    /// </summary>
    public SettingsImportResult ImportSettings(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxImportBytes)
        {
            return new SettingsImportResult(false, "This file is too large to be a DevKit settings file.");
        }

        var file = ReadSettingsFile(text);
        if (file.Preferences is not { } preferences)
        {
            return new SettingsImportResult(false, file.Refusal ?? NotSettingsFileMessage);
        }

        var statusDrops = preferences.StatusUpdateDropsOver(_settings.StatusUpdate);

        // Not kept means nothing of the file is in use either: what the page shows as saved is what is stored.
        var kept = UpdateOrRollBack(() =>
        {
            if (file.EarlierFile is { } earlier)
            {
                CarryNameKeyedBranches(earlier);
            }

            ApplyPreferences(preferences);
        });
        if (!kept)
        {
            return new SettingsImportResult(false, LastSaveError ?? SaveFailedMessage);
        }

        var repositories = preferences.RepoBranches?.Count(pair => !pair.Value.IsEmpty) ?? 0;
        _logger.LogInformation("Settings imported from {Kind}: {Repositories} repositories with branch defaults",
            file.EarlierFile is null ? "an exported file" : "an earlier settings file", repositories);
        var imported = file.EarlierFile is null
            ? $"Settings imported, including branch defaults for {repositories} repositories. Settings the file does not name were kept, and your TFS connection was not changed."
            : $"Settings imported from an earlier DevKit settings file, including branch defaults for {repositories} repositories. Its TFS connection and token were not used, and settings it does not name were kept.";
        return new SettingsImportResult(true, imported + StatusDropsNote(statusDrops));
    }

    private static string StatusDropsNote(IReadOnlyList<string> drops) =>
        drops.Count == 0 ? "" : $" From Update status, {string.Join("; ", drops)}.";

    /// <summary>What a file to import holds: its preferences, the whole earlier settings file they came from, or why it is refused.</summary>
    private sealed record SettingsFileRead(PortableSettings? Preferences, UserSettings? EarlierFile, string? Refusal);

    private SettingsFileRead ReadSettingsFile(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, ImportDocument);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SettingsFileRead(null, null, NotSettingsFileMessage);
            }

            // What the file names, in any case; the serializer, too, takes the last of a name given twice.
            var named = root.EnumerateObject()
                .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value.ValueKind, StringComparer.OrdinalIgnoreCase);
            if (named.ContainsKey(nameof(PortableSettings.Format)))
            {
                return ReadExport(root.Deserialize<PortableSettings>(ImportFormat));
            }

            // The whole settings file always holds the connection; the other DevKit files never do at the top.
            return named.GetValueOrDefault(nameof(UserSettings.Tfs)) == JsonValueKind.Object
                ? ReadEarlierFile(root.Deserialize<UserSettings>(ImportFormat), named, NamedInside(root, nameof(UserSettings.StatusUpdate)))
                : new SettingsFileRead(null, null, NotSettingsFileMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "A settings file chosen for import could not be read as DevKit settings");
            return new SettingsFileRead(null, null, NotSettingsFileMessage);
        }
    }

    private static SettingsFileRead ReadExport(PortableSettings? portable)
    {
        if (portable is null || !string.Equals(portable.Format, PortableSettings.FormatName, StringComparison.Ordinal))
        {
            return new SettingsFileRead(null, null, NotSettingsFileMessage);
        }

        return portable.Version > PortableSettings.CurrentVersion
            ? new SettingsFileRead(null, null, "This file was made by a newer DevKit. Update DevKit, then import it again.")
            : new SettingsFileRead(portable, null, null);
    }

    /// <summary>
    /// The preferences of a whole settings file (usersettings.json): only the ones it names, so a file from before a
    /// setting existed leaves that setting as it is; inside the Update status settings, too, only what the file names.
    /// Its TFS connection and token are never read.
    /// </summary>
    private static SettingsFileRead ReadEarlierFile(
        UserSettings? earlier, Dictionary<string, JsonValueKind> named, HashSet<string> statusNamed)
    {
        if (earlier is null)
        {
            return new SettingsFileRead(null, null, NotSettingsFileMessage);
        }

        T? Named<T>(string name, T value) where T : class => named.ContainsKey(name) ? value : null;
        return new SettingsFileRead(new PortableSettings
        {
            TeamName = Named(nameof(UserSettings.TeamName), earlier.TeamName),
            DefaultAreaPath = Named(nameof(UserSettings.DefaultAreaPath), earlier.DefaultAreaPath),
            DefaultSprint = Named(nameof(UserSettings.DefaultSprint), earlier.DefaultSprint),
            DefaultSprintMode = earlier.DefaultSprintMode,
            DefaultRepoId = Named(nameof(UserSettings.DefaultRepoId), earlier.DefaultRepoId),
            MostUsedRepoCount = named.ContainsKey(nameof(UserSettings.MostUsedRepoCount)) ? earlier.MostUsedRepoCount : null,
            MostUsedRepoIds = Named(nameof(UserSettings.MostUsedRepoIds), earlier.MostUsedRepoIds),
            CapacityPlanning = earlier.CapacityPlanning,
            CodeMergingRepoIds = Named(nameof(UserSettings.CodeMergingRepoIds), earlier.CodeMergingRepoIds),
            RepoBranches = Named(nameof(UserSettings.RepoBranches), earlier.RepoBranches)
        }.WithStatusUpdate(Named(nameof(UserSettings.StatusUpdate), earlier.StatusUpdate)?.Normalized(), statusNamed.Contains), earlier, null);
    }

    /// <summary>The names inside the object <paramref name="root"/> names <paramref name="name"/>, in any case; the last one wins, as in the serializer.</summary>
    private static HashSet<string> NamedInside(JsonElement root, string name)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject().Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            names.Clear();
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                names.UnionWith(property.Value.EnumerateObject().Select(inner => inner.Name));
            }
        }

        return names;
    }

    /// <summary>
    /// The branch defaults a settings file from before repository ids kept by repository name. They are added where
    /// nothing is kept yet, and become id-keyed defaults once the repositories load (<see cref="MigrateRepoBranches"/>).
    /// </summary>
    private void CarryNameKeyedBranches(UserSettings earlier)
    {
        foreach (var (name, branch) in (earlier.RepoTargetBranches ?? new()).Take(MaxImportedRepositories))
        {
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(branch))
            {
                _settings.RepoTargetBranches.TryAdd(name.Trim(), branch.Trim());
            }
        }

        if (string.IsNullOrWhiteSpace(_settings.CodeMergingTargetBranch))
        {
            _settings.CodeMergingTargetBranch = (earlier.CodeMergingTargetBranch ?? "").Trim();
        }
    }

    /// <summary>Puts the preferences <paramref name="changes"/> names in use, cleaned as Settings keeps them.</summary>
    private void ApplyPreferences(PortableSettings changes)
    {
        _settings.TeamName = changes.TeamName?.Trim() ?? _settings.TeamName;
        _settings.DefaultAreaPath = changes.DefaultAreaPath ?? _settings.DefaultAreaPath;
        _settings.DefaultRepoId = changes.DefaultRepoId?.Trim() ?? _settings.DefaultRepoId;
        // A file from before the choice existed names the sprint alone; null then means "worked out from the sprint".
        if (changes.DefaultSprint is not null || changes.DefaultSprintMode is not null)
        {
            _settings.DefaultSprint = changes.DefaultSprint ?? _settings.DefaultSprint;
            _settings.DefaultSprintMode = changes.DefaultSprintMode;
        }

        if (changes.MostUsedRepoCount is { } count)
        {
            _settings.MostUsedRepoCount = Math.Clamp(count, 0, MaxMostUsedRepoCount);
        }

        // Also when only the count changed: a lower count trims the pinned list.
        _settings.MostUsedRepoIds = DistinctIds(changes.MostUsedRepoIds ?? _settings.MostUsedRepoIds, MostUsedRepoCount);
        _settings.CapacityPlanning = changes.CapacityPlanning?.Normalized() ?? _settings.CapacityPlanning;
        if (changes.CodeMergingRepoIds is { } merging)
        {
            _settings.CodeMergingRepoIds = DistinctIds(merging, MaxImportedRepositories);
        }

        MergeBranches(changes.RepoBranches);
        ApplyStatusUpdate(changes);
    }

    /// <summary>
    /// The Update status settings <paramref name="changes"/> names, laid over those kept as a new copy: fields a newer
    /// DevKit wrote (UnknownFields) stay, and a change made in another tab to a setting not named here is kept. The kept
    /// object is replaced, never changed, so whoever holds it still sees it whole.
    /// </summary>
    private void ApplyStatusUpdate(PortableSettings changes) =>
        _settings.StatusUpdate = changes.StatusUpdateOver(_settings.StatusUpdate, _logger);

    /// <summary>Each repository named takes its branches; one with both empty goes back to automatic. Others keep theirs.</summary>
    private void MergeBranches(Dictionary<string, RepoBranchDefaults>? changes)
    {
        foreach (var (id, branches) in CleanBranches(changes))
        {
            if (branches.IsEmpty)
            {
                _settings.RepoBranches.Remove(id);
            }
            else if (_settings.RepoBranches.TryGetValue(id, out var kept))
            {
                // The entry kept also holds what a later DevKit added to it.
                kept.QaBranch = branches.QaBranch;
                kept.MergingBranch = branches.MergingBranch;
            }
            else
            {
                _settings.RepoBranches[id] = branches;
            }
        }
    }

    /// <summary>Branch defaults with ids and names trimmed, one entry per repository, and at most as many as a collection has.</summary>
    private static Dictionary<string, RepoBranchDefaults> CleanBranches(Dictionary<string, RepoBranchDefaults>? branches) =>
        (branches ?? new())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            .Take(MaxImportedRepositories)
            .Select(pair => (Id: pair.Key.Trim(), Branches: new RepoBranchDefaults
            {
                QaBranch = (pair.Value.QaBranch ?? "").Trim(),
                MergingBranch = (pair.Value.MergingBranch ?? "").Trim()
            }))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Branches, StringComparer.OrdinalIgnoreCase);
}
