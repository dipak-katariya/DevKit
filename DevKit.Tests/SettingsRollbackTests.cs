using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What a save that failed leaves in use, what reading the settings again does with it, and which sign-in a refusal
/// from TFS is put down to.
/// </summary>
public sealed class SettingsRollbackTests
{
    private const string TfsUrl = "https://tfs.example.test/tfs/Collection";
    private const string OldPat = "old-test-pat-1";
    private const string NewPat = "new-test-pat-2";

    private readonly FlakyStore _store = new();

    [Fact]
    public void A_failed_save_of_the_form_leaves_the_stored_values_in_use()
    {
        var settings = Open();
        settings.TeamName = "TEAM-A";
        _store.FailWrites = true;

        Assert.False(settings.SavePreferences(new PortableSettings { TeamName = "TEAM-X" }));

        Assert.Equal("TEAM-A", settings.TeamName);
        Assert.Equal("TEAM-A", settings.CurrentPreferences().TeamName);
        Assert.NotNull(settings.LastSaveError);
    }

    [Fact]
    public void A_failed_import_leaves_nothing_of_the_file_in_use()
    {
        var settings = Open();
        _store.FailWrites = true;
        var file = $$"""{ "Format": "{{PortableSettings.FormatName}}", "Version": 1, "TeamName": "TEAM-X" }""";

        Assert.False(settings.ImportSettings(file).Imported);

        Assert.Equal("", settings.TeamName);
    }

    [Fact]
    public void Reading_again_after_another_page_saved_keeps_a_change_this_page_could_not_save()
    {
        var settings = Open();
        _store.FailWrites = true;
        settings.DefaultRepoId = "repo-1";
        _store.Put("TeamName", "TEAM-B");

        Assert.Equal(SettingsReload.Reloaded, settings.ReloadFromStore());

        Assert.Equal("TEAM-B", settings.TeamName);
        Assert.Equal("repo-1", settings.DefaultRepoId);
        _store.FailWrites = false;
        Assert.True(settings.Save());
        Assert.Contains("repo-1", _store.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Try_again_opens_saving_when_the_store_now_keeps_nothing()
    {
        _store.FailReads = true;
        var settings = Open();
        Assert.True(settings.SavingBlocked);

        _store.FailReads = false;
        Assert.Equal(SettingsReload.Reloaded, settings.ReloadFromStore());

        Assert.False(settings.SavingBlocked);
        Assert.Null(settings.LoadProblem);
    }

    [Fact]
    public void A_refusal_of_a_token_replaced_meanwhile_does_not_mark_the_new_one()
    {
        var settings = Open();
        Assert.True(settings.SaveTfs(TfsUrl, NewPat));

        settings.NoteSignInRefused(TfsAuthMode.Pat, OldPat);

        Assert.False(settings.SignInRefused);
    }

    [Fact]
    public void A_request_that_works_with_the_refused_sign_in_clears_the_mark()
    {
        var settings = Open();
        Assert.True(settings.SaveTfs(TfsUrl, NewPat));
        settings.NoteSignInRefused(TfsAuthMode.Pat, NewPat);
        Assert.True(settings.SignInRefused);

        settings.NoteSignInAccepted(TfsAuthMode.Pat, OldPat);
        Assert.True(settings.SignInRefused);
        settings.NoteSignInAccepted(TfsAuthMode.Pat, NewPat);

        Assert.False(settings.SignInRefused);
    }

    [Fact]
    public void Saving_the_same_sign_in_again_clears_the_mark()
    {
        var settings = Open();
        Assert.True(settings.SaveTfs(TfsUrl, NewPat));
        settings.NoteSignInRefused(TfsAuthMode.Pat, NewPat);

        Assert.True(settings.SaveTfs(TfsUrl, NewPat));

        Assert.False(settings.SignInRefused);
    }

    [Fact]
    public void The_tour_does_not_open_when_its_mark_cannot_be_saved()
    {
        var settings = Open();
        _store.FailWrites = true;

        Assert.False(settings.TryStartGuideTour());

        Assert.False(settings.GuideTourSeen);
        Assert.Null(settings.LastSaveError);
    }

    [Fact]
    public void The_status_field_its_name_and_its_value_change_together()
    {
        var saved = new PortableSettings().WithStatusUpdate(new StatusUpdateSettings
        {
            SetField = true, Field = "Custom.Status", FieldName = "Status", Value = "Done"
        });
        var edited = new PortableSettings().WithStatusUpdate(new StatusUpdateSettings
        {
            SetField = true, Field = "Custom.Status", FieldName = "Status", Value = "In PT"
        });

        var changes = edited.ChangesFrom(saved);

        Assert.Equal("Custom.Status", changes.StatusField);
        Assert.Equal("Status", changes.StatusFieldName);
        Assert.Equal("In PT", changes.StatusValue);
        Assert.Null(changes.StatusSetField);
    }

    [Fact]
    public void An_earlier_settings_file_changes_only_the_status_settings_it_names()
    {
        var settings = Open();
        Assert.True(settings.SavePreferences(new PortableSettings().WithStatusUpdate(new StatusUpdateSettings
        {
            SetField = true, Field = "Custom.Status", FieldName = "Status", Value = "Done"
        })));
        const string Earlier = """{ "Tfs": {}, "StatusUpdate": { "AddTags": true, "Tags": ["PT Merged"] } }""";

        Assert.True(settings.ImportSettings(Earlier).Imported);

        var status = settings.StatusUpdate;
        Assert.True(status.AddTags);
        Assert.True(status.SetField);
        Assert.Equal("Custom.Status", status.Field);
        Assert.Equal("Done", status.Value);
    }

    [Fact]
    public void Status_settings_a_later_DevKit_added_go_through_export_and_import()
    {
        var settings = Open();
        _store.Put("StatusUpdate", new Dictionary<string, object> { ["AddTags"] = true, ["Later"] = "kept" });
        settings.ReloadFromStore();

        var exported = settings.ExportSettings();
        var other = new SettingsService(NullLogger<SettingsService>.Instance, new FlakyStore(), TfsUrl);
        other.LoadFromStore(allowsWindowsSignIn: false);
        Assert.True(other.ImportSettings(exported).Imported);

        Assert.Contains("kept", other.ExportSettings(), StringComparison.Ordinal);
    }

    private SettingsService Open()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, _store, TfsUrl);
        settings.LoadFromStore(allowsWindowsSignIn: false);
        return settings;
    }

    /// <summary>Settings kept in memory, whose reads and writes can be made to fail, and which another page can change.</summary>
    private sealed class FlakyStore : ISettingsStore
    {
        public string? Text { get; private set; }

        public bool FailWrites { get; set; }

        public bool FailReads { get; set; }

        public string Location => "memory";

        public string? ReadText() => FailReads ? throw new IOException("The test store does not open.") : Text;

        public string? ReadBackupText() => null;

        public void Write(string text)
        {
            if (FailWrites)
            {
                throw new IOException("The test store does not save.");
            }

            Text = text;
        }

        /// <summary>What another page saved: one setting changed in what is kept.</summary>
        public void Put(string name, object value)
        {
            var kept = System.Text.Json.Nodes.JsonNode.Parse(Text ?? "{}")!.AsObject();
            kept[name] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value));
            Text = kept.ToJsonString();
        }

        public void DeleteBackup()
        {
            // Memory keeps no earlier version.
        }

        public void SetAsideCorrupt()
        {
            // Nothing to set aside in memory.
        }
    }
}
