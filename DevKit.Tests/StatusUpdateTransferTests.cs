using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The Update status settings as the Settings page, export and import handle them: each of its six fields saves on
/// its own, a file that does not name them keeps them, and what a file brings is cleaned as Settings keeps it.
/// </summary>
public sealed class StatusUpdateTransferTests
{
    private const string FixedUrl = "https://tfs.example.test/tfs/Collection";
    private const string StatusReference = "Casepoint.TFS.CustomFields.Status";

    private static SettingsService NewSettings(MemorySettingsStore? store = null)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store ?? new MemorySettingsStore(), FixedUrl);
        settings.LoadFromStore(allowsWindowsSignIn: false);
        return settings;
    }

    private static StatusUpdateSettings PtMerged() => new()
    {
        AddTags = true,
        Tags = new List<string> { "PT Merged", "QA Done" },
        SetField = true,
        Field = StatusReference,
        FieldName = "Status",
        Value = "In PT"
    };

    private static SettingsService WithPtMerged()
    {
        var settings = NewSettings();
        Assert.True(settings.SavePreferences(new PortableSettings().WithStatusUpdate(PtMerged())));
        return settings;
    }

    [Fact]
    public void A_new_person_has_Update_status_off()
    {
        var status = NewSettings().StatusUpdate;

        Assert.False(status.HasWork());
        Assert.Empty(status.Tags);
        Assert.Equal("", status.Field);
    }

    [Fact]
    public void Update_status_travels_in_an_export()
    {
        var other = NewSettings();

        Assert.True(other.ImportSettings(WithPtMerged().ExportSettings()).Imported);

        var status = other.StatusUpdate;
        Assert.True(status.AddTags);
        Assert.Equal(new[] { "PT Merged", "QA Done" }, status.Tags);
        Assert.True(status.SetField);
        Assert.Equal(StatusReference, status.Field);
        Assert.Equal("Status", status.FieldName);
        Assert.Equal("In PT", status.Value);
    }

    [Fact]
    public void A_file_from_before_Update_status_keeps_the_one_set_up()
    {
        var settings = WithPtMerged();

        Assert.True(settings.ImportSettings($$"""{ "Format": "{{PortableSettings.FormatName}}", "Version": 1, "TeamName": "TEAM-B" }""").Imported);

        Assert.Equal("TEAM-B", settings.TeamName);
        Assert.True(settings.StatusUpdate.HasWork());
        Assert.Equal(new[] { "PT Merged", "QA Done" }, settings.StatusUpdate.Tags);
    }

    [Fact]
    public void An_import_says_which_Update_status_values_it_left_out()
    {
        var settings = NewSettings();

        var result = settings.ImportSettings($$"""{ "Format": "{{PortableSettings.FormatName}}", "Version": 1, "StatusField": "not a reference" }""");

        Assert.True(result.Imported);
        Assert.Contains("From Update status, the field was left out", result.Message, StringComparison.Ordinal);
        Assert.Equal("", settings.StatusUpdate.Field);
    }

    [Fact]
    public void An_import_with_clean_Update_status_values_says_nothing_about_them()
    {
        var result = NewSettings().ImportSettings(WithPtMerged().ExportSettings());

        Assert.DoesNotContain("Update status", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_one_of_its_fields_keeps_the_others()
    {
        var settings = WithPtMerged();

        Assert.True(settings.SavePreferences(new PortableSettings { StatusValue = "Done" }));

        var status = settings.StatusUpdate;
        Assert.Equal("Done", status.Value);
        Assert.Equal(new[] { "PT Merged", "QA Done" }, status.Tags);
        Assert.Equal(StatusReference, status.Field);
        Assert.True(status.AddTags);
    }

    [Fact]
    public void Only_the_fields_the_form_changed_are_named_as_changes()
    {
        var saved = WithPtMerged().CurrentPreferences();
        var edited = WithPtMerged().CurrentPreferences();
        edited.StatusTags = new List<string> { "PT Merged" };
        edited.StatusSetField = false;

        var changes = edited.ChangesFrom(saved);

        Assert.True(changes.HasChanges);
        Assert.Equal(new[] { "PT Merged" }, changes.StatusTags);
        Assert.False(changes.StatusSetField);
        Assert.Null(changes.StatusAddTags);
        Assert.Null(changes.StatusField);
        Assert.Null(changes.StatusFieldName);
        Assert.Null(changes.StatusValue);
    }

    [Fact]
    public void An_unchanged_form_names_no_change()
    {
        var saved = WithPtMerged().CurrentPreferences();

        Assert.False(WithPtMerged().CurrentPreferences().ChangesFrom(saved).HasChanges);
    }

    [Fact]
    public void What_a_file_brings_is_cleaned_as_Settings_keeps_it()
    {
        var settings = NewSettings();

        var result = settings.ImportSettings($$"""
            { "Format": "{{PortableSettings.FormatName}}", "Version": 1, "StatusAddTags": true,
              "StatusTags": [" PT Merged ", "pt merged", "a;b", "", null],
              "StatusSetField": true, "StatusField": "System.State/../../_apis/projects", "StatusFieldName": "State", "StatusValue": "Closed" }
            """);

        Assert.True(result.Imported, result.Message);
        var status = settings.StatusUpdate;
        Assert.Equal(new[] { "PT Merged" }, status.Tags);
        Assert.Equal("", status.Field);
        Assert.Equal("", status.FieldName);
        Assert.False(status.HasFieldToSet());
    }

    [Fact]
    public void An_earlier_whole_settings_file_brings_its_Update_status_and_one_without_it_keeps_the_one_set_up()
    {
        var settings = WithPtMerged();
        const string WithoutIt = """{ "Tfs": { "Url": "https://old.example.test/tfs/Old" }, "TeamName": "TEAM-OLD" }""";
        const string WithIt = """
            { "Tfs": { "Url": "https://old.example.test/tfs/Old" },
              "StatusUpdate": { "AddTags": true, "Tags": ["Merged to PT"], "SetField": false, "Field": "", "FieldName": "", "Value": "" } }
            """;

        Assert.True(settings.ImportSettings(WithoutIt).Imported);
        Assert.Equal(new[] { "PT Merged", "QA Done" }, settings.StatusUpdate.Tags);

        Assert.True(settings.ImportSettings(WithIt).Imported);
        Assert.Equal(new[] { "Merged to PT" }, settings.StatusUpdate.Tags);
        Assert.False(settings.StatusUpdate.SetField);
        Assert.Equal(FixedUrl, settings.Tfs.Url);
    }

    [Fact]
    public void Fields_a_later_DevKit_keeps_in_it_are_written_back_unchanged()
    {
        var store = new MemorySettingsStore();
        store.Write("""{ "StatusUpdate": { "AddTags": true, "Tags": ["PT Merged"], "FromLater": { "Nested": [1, 2] } } }""");
        var settings = NewSettings(store);

        Assert.True(settings.SavePreferences(new PortableSettings { StatusValue = "In PT" }));

        using var saved = JsonDocument.Parse(store.Text!);
        var status = saved.RootElement.GetProperty("StatusUpdate");
        Assert.Equal(2, status.GetProperty("FromLater").GetProperty("Nested").GetArrayLength());
        Assert.Equal("In PT", status.GetProperty("Value").GetString());
    }

    [Fact]
    public void A_settings_file_with_no_Update_status_or_a_null_one_reads_as_off()
    {
        var store = new MemorySettingsStore();
        store.Write("""{ "TeamName": "TEAMX", "StatusUpdate": null }""");

        var status = NewSettings(store).StatusUpdate;

        Assert.False(status.HasWork());
        Assert.Empty(status.Tags);
    }
}
