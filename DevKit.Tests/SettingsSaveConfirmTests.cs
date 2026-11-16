using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The Settings page's Save button: it says the settings are saved only once the store kept them, and a store that kept
/// none yet (a person's first visit) is told apart until something is saved.
/// </summary>
public sealed class SettingsSaveConfirmTests
{
    private const string TfsUrl = "https://tfs.example.test/tfs/Collection";

    [Fact]
    public async Task Save_confirms_once_the_store_kept_the_settings()
    {
        var store = new AnsweringStore(kept: true);
        var settings = Shared(store);
        settings.TeamName = "FXCPMGR";

        Assert.True(await settings.SaveAndConfirmAsync());
        Assert.Contains("FXCPMGR", store.Text, StringComparison.Ordinal);
        Assert.Null(settings.LastSaveError);
    }

    [Fact]
    public async Task Save_is_not_confirmed_when_the_store_did_not_keep_them()
    {
        var store = new AnsweringStore(kept: false);
        var settings = Shared(store);

        Assert.False(await settings.SaveAndConfirmAsync());
        Assert.NotNull(settings.LastSaveError);
        Assert.Null(store.Text);
    }

    [Fact]
    public async Task Save_is_not_confirmed_when_saving_is_blocked()
    {
        var store = new AnsweringStore(kept: true) { ReadFails = true };
        var settings = Shared(store);

        Assert.False(await settings.SaveAndConfirmAsync());
        Assert.NotNull(settings.LastSaveError);
        Assert.Null(store.Text);
    }

    [Fact]
    public void A_store_that_keeps_nothing_starts_empty_until_a_save()
    {
        var settings = Shared(new AnsweringStore(kept: true));

        Assert.True(settings.StartedEmpty);
        settings.TeamName = "FXCPMGR";
        Assert.False(settings.StartedEmpty);
    }

    [Fact]
    public void A_store_that_keeps_settings_does_not_start_empty()
    {
        var store = new AnsweringStore(kept: true);
        store.Write("""{ "TeamName": "FXCPMGR" }""");
        var settings = Shared(store);

        Assert.False(settings.StartedEmpty);
        Assert.Equal("FXCPMGR", settings.TeamName);
    }

    private static SettingsService Shared(AnsweringStore store)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, TfsUrl);
        settings.LoadFromStore(allowsWindowsSignIn: false);
        return settings;
    }

    /// <summary>Settings kept in memory; a save throws, as a store that did not keep it does, unless <c>kept</c>.</summary>
    private sealed class AnsweringStore(bool kept) : ISettingsStore
    {
        public string? Text { get; private set; }

        public bool ReadFails { get; init; }

        public string Location => "memory";

        public string? ReadText() => ReadFails ? throw new IOException("The store did not answer.") : Text;

        public string? ReadBackupText() => null;

        public void Write(string text) => Text = kept ? text : throw new IOException("The store did not keep the settings.");

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
