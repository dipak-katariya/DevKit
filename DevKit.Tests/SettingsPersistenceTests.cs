using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Characterises how SettingsService writes and reads usersettings.json, the encrypted PAT included.
/// The facts that pinned B112, B114 and B116 now pin their fixes; each says which.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class SettingsPersistenceTests : IDisposable
{
    private const string EncryptedPrefix = "enc:v2:";
    private const string TfsUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Pat = "contoso-test-pat-7f3a9c21";
    private const string WebRepoId = "11111111-aaaa-4000-8000-000000000001";
    private const string ApiRepoId = "22222222-bbbb-4000-8000-000000000002";

    private static readonly string SettingsPath = TestSettingsLocation.SettingsPath;

    public SettingsPersistenceTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        foreach (var file in SettingsFiles())
        {
            // One test leaves the file read-only, and File.Delete refuses a read-only file on Windows.
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
    }

    /// <summary>The settings file and everything saved beside it: the backup and any damaged file set aside.</summary>
    private static string[] SettingsFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, Path.GetFileName(SettingsPath) + "*");

    private static string[] SetAsideFiles() =>
        SettingsFiles().Where(f => Path.GetFileName(f).Contains(".corrupt-", StringComparison.Ordinal)).ToArray();

    private static SettingsService NewService() => new(NullLogger<SettingsService>.Instance);

    private static JsonElement ReadFile()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        return document.RootElement.Clone();
    }

    private static string? StoredPat() => ReadFile().GetProperty("Tfs").GetProperty("Pat").GetString();

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

    private static SettingsService SeedEveryPersistedKey()
    {
        // DeliverableTags has no setter on the service; it only ever arrives in a file from an older build.
        File.WriteAllText(SettingsPath, """{ "DeliverableTags": { "ALM": true, "Regression": false } }""");
        var settings = NewService();
        settings.SaveTfs(TfsUrl, Pat);
        // ApiVersion has no saving setter; the saves that follow carry it to disk.
        settings.Tfs.ApiVersion = "6.0";
        settings.DefaultAreaPath = @"Contoso\TEAMX";
        settings.DefaultSprint = @"Contoso\Sprint 7";
        settings.DefaultRepoId = WebRepoId;
        settings.TeamName = "TEAMX";
        settings.MostUsedRepoCount = 3;
        settings.SetMostUsedRepoIds(new[] { WebRepoId, ApiRepoId });
        settings.CodeMergingTargetBranch = "release/1.0";
        settings.SetCodeMergingRepoIds(new[] { WebRepoId, ApiRepoId, WebRepoId });
        settings.SetRepoTargetBranch("Contoso.Web", "qa");
        settings.SetRepoQaBranch(ApiRepoId, "qa");
        settings.SetRepoMergingBranch(ApiRepoId, "release/1.0");
        var planning = settings.CapacityPlanning;
        planning.Mode = PlanningMode.Regular;
        planning.HoursPerDay = 7.5;
        planning.SprintWeeks = 3;
        settings.SaveCapacityPlanning(planning);
        return settings;
    }

    [Fact]
    public void The_file_holds_every_key_and_the_pat_only_in_encrypted_form()
    {
        var settings = SeedEveryPersistedKey();

        var root = ReadFile();

        Assert.StartsWith(EncryptedPrefix, StoredPat());
        Assert.DoesNotContain(Pat, File.ReadAllText(SettingsPath));
        Assert.Equal(Pat, settings.Tfs.Pat);
        Assert.Equal(
            new[]
            {
                "CapacityPlanning", "CodeMergingRepoIds", "CodeMergingTargetBranch", "DefaultAreaPath",
                "DefaultRepoId", "DefaultSprint", "DefaultSprintMode", "DeliverableTags", "MostUsedRepoCount",
                "MostUsedRepoIds", "RepoBranches", "RepoTargetBranches", "TeamName", "Tfs"
            },
            PropertyNames(root));
        // IsConfigured is computed, yet it is written to the file and ignored when read back.
        Assert.Equal(new[] { "ApiVersion", "Auth", "IsConfigured", "Pat", "Url" }, PropertyNames(root.GetProperty("Tfs")));
        Assert.Equal("Pat", root.GetProperty("Tfs").GetProperty("Auth").GetString());
        Assert.True(root.GetProperty("DeliverableTags").GetProperty("ALM").GetBoolean());
        Assert.False(root.GetProperty("DeliverableTags").GetProperty("Regression").GetBoolean());
    }

    [Fact]
    public void A_new_service_reads_every_persisted_value_back()
    {
        SeedEveryPersistedKey();

        var reloaded = NewService();

        Assert.Equal(TfsUrl, reloaded.Tfs.Url);
        Assert.Equal(Pat, reloaded.Tfs.Pat);
        Assert.Equal("6.0", reloaded.Tfs.ApiVersion);
        Assert.True(reloaded.IsConfigured);
        Assert.Equal(@"Contoso\TEAMX", reloaded.DefaultAreaPath);
        Assert.Equal(@"Contoso\Sprint 7", reloaded.DefaultSprint);
        Assert.Equal(WebRepoId, reloaded.DefaultRepoId);
        Assert.Equal("TEAMX", reloaded.TeamName);
        Assert.Equal(3, reloaded.MostUsedRepoCount);
        Assert.Equal(new[] { WebRepoId, ApiRepoId }, reloaded.MostUsedRepoIds);
        Assert.Equal("release/1.0", reloaded.CodeMergingTargetBranch);
        Assert.Equal(new[] { WebRepoId, ApiRepoId }, reloaded.CodeMergingRepoIds);
        Assert.Equal("qa", Assert.Single(reloaded.RepoTargetBranches, b => b.Key == "Contoso.Web").Value);
        var branches = Assert.Single(reloaded.RepoBranches, b => b.Key == ApiRepoId).Value;
        Assert.Equal("qa", branches.QaBranch);
        Assert.Equal("release/1.0", branches.MergingBranch);
        var planning = reloaded.CapacityPlanning;
        Assert.Equal(PlanningMode.Regular, planning.Mode);
        Assert.Equal(7.5, planning.HoursPerDay);
        Assert.Equal(3, planning.SprintWeeks);
        Assert.True(planning.TagRules.Single(r => r.Tag == "ALM").Deliverable);

        // DeliverableTags is only observable on disk: re-saving from the reloaded service proves it was read.
        reloaded.Save();
        Assert.True(ReadFile().GetProperty("DeliverableTags").GetProperty("ALM").GetBoolean());
    }

    [Fact]
    public void A_plaintext_pat_in_the_file_is_re_saved_encrypted_on_load()
    {
        File.WriteAllText(SettingsPath, $$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{Pat}}" }, "TeamName": "TEAMX" }""");

        var settings = NewService();

        Assert.Equal(Pat, settings.Tfs.Pat);
        Assert.StartsWith(EncryptedPrefix, StoredPat());
        Assert.DoesNotContain(Pat, File.ReadAllText(SettingsPath));
        Assert.Equal("TEAMX", ReadFile().GetProperty("TeamName").GetString());
        Assert.Equal(Pat, NewService().Tfs.Pat);
    }

    [Fact]
    public void A_file_from_before_the_sign_in_choice_signs_in_with_its_pat()
    {
        File.WriteAllText(SettingsPath, $$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{Pat}}" } }""");

        var settings = NewService();

        Assert.Equal(TfsAuthMode.Pat, settings.Tfs.Auth);
        Assert.True(settings.IsConfigured);
    }

    [Fact]
    public void Windows_sign_in_is_saved_by_name_with_no_pat_and_read_back()
    {
        var settings = NewService();

        var saved = settings.SaveTfs(TfsUrl, Pat, TfsAuthMode.Windows);

        Assert.True(saved);
        Assert.True(settings.IsConfigured);
        Assert.False(settings.HasStoredPat);
        var tfs = ReadFile().GetProperty("Tfs");
        Assert.Equal("Windows", tfs.GetProperty("Auth").GetString());
        Assert.Equal("", tfs.GetProperty("Pat").GetString());
        var reloaded = NewService();
        Assert.Equal(TfsAuthMode.Windows, reloaded.Tfs.Auth);
        Assert.Equal(TfsUrl, reloaded.Tfs.Url);
        Assert.True(reloaded.IsConfigured);
    }

    [Fact]
    public void Switching_to_Windows_sign_in_leaves_no_pat_in_the_file_or_its_backup()
    {
        var settings = NewService();
        settings.SaveTfs(TfsUrl, Pat);
        settings.TeamName = "TEAMX";
        Assert.Equal(2, SettingsFiles().Count(f => File.ReadAllText(f).Contains(EncryptedPrefix, StringComparison.Ordinal)));

        settings.SaveTfs(TfsUrl, "", TfsAuthMode.Windows);

        Assert.Equal("", settings.Tfs.Pat);
        Assert.All(SettingsFiles(), f => Assert.DoesNotContain(EncryptedPrefix, File.ReadAllText(f), StringComparison.Ordinal));
        Assert.Equal("TEAMX", NewService().TeamName);
    }

    [Fact]
    public void Switching_to_Windows_sign_in_drops_a_pat_this_account_cannot_decrypt()
    {
        const string Blob = "enc:v1:AAECAwQFBgcICQoLDA0ODw==";
        File.WriteAllText(SettingsPath, $$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{Blob}}" } }""");
        var settings = new SettingsService(new RecordingLogger<SettingsService>());

        settings.SaveTfs(TfsUrl, "", TfsAuthMode.Windows);

        Assert.Equal("", StoredPat());
        Assert.All(SettingsFiles(), f => Assert.DoesNotContain(Blob, File.ReadAllText(f), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TfsUrl, TfsAuthMode.Windows, "", true)]
    [InlineData(TfsUrl, TfsAuthMode.Pat, "", false)]
    [InlineData(TfsUrl, TfsAuthMode.Pat, Pat, true)]
    [InlineData("", TfsAuthMode.Windows, "", false)]
    public void Only_a_pat_sign_in_needs_a_pat_to_be_configured(string url, TfsAuthMode auth, string pat, bool configured)
    {
        Assert.Equal(configured, new TfsSettings { Url = url, Auth = auth, Pat = pat }.IsConfigured);
    }

    [Fact]
    public void A_file_with_nothing_to_migrate_is_not_rewritten_on_load()
    {
        const string Original = """{ "TeamName": "TEAMX" }""";
        File.WriteAllText(SettingsPath, Original);

        var settings = NewService();

        Assert.Equal("TEAMX", settings.TeamName);
        Assert.Equal(Original, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("""{ "TeamName": "TEAMX", "Tfs": { "Url": """)]
    [InlineData("""{ "TeamName": "TEAMX", "MostUsedRepoCount": "three" }""")]
    public void An_unreadable_file_with_no_backup_is_set_aside_and_never_overwritten(string unreadable)
    {
        // B114 fixed: the damaged file is kept beside the settings file, not replaced by the next save.
        File.WriteAllText(SettingsPath, unreadable);
        var logger = new RecordingLogger<SettingsService>();

        var settings = new SettingsService(logger);

        Assert.Equal("", settings.TeamName);
        Assert.Equal("", settings.Tfs.Url);
        Assert.False(settings.IsConfigured);
        Assert.NotNull(settings.LoadProblem);
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(unreadable, File.ReadAllText(Assert.Single(SetAsideFiles())));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsAssignableFrom<JsonException>(entry.Exception);

        settings.DefaultSprint = @"Contoso\Sprint 7";

        var rewritten = ReadFile();
        Assert.Equal(@"Contoso\Sprint 7", rewritten.GetProperty("DefaultSprint").GetString());
        Assert.Equal("", rewritten.GetProperty("TeamName").GetString());
        Assert.Equal("", rewritten.GetProperty("Tfs").GetProperty("Url").GetString());
        Assert.Equal(unreadable, File.ReadAllText(Assert.Single(SetAsideFiles())));
    }

    [Theory]
    [InlineData("enc:v1:AAECAwQFBgcICQoLDA0ODw==")]
    [InlineData("enc:v1:not-base64!")]
    public void A_pat_that_cannot_be_decrypted_loads_empty_and_is_kept_through_unrelated_saves(string blob)
    {
        // B116 fixed: an undecryptable PAT is blank in memory, but saves write the stored blob back unchanged.
        File.WriteAllText(SettingsPath, $$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{blob}}" }, "TeamName": "TEAMX" }""");
        var logger = new RecordingLogger<SettingsService>();

        var settings = new SettingsService(logger);

        Assert.Equal("", settings.Tfs.Pat);
        Assert.Equal(TfsUrl, settings.Tfs.Url);
        Assert.Equal("TEAMX", settings.TeamName);
        Assert.False(settings.IsConfigured);
        Assert.Equal(blob, StoredPat());
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);

        settings.DefaultRepoId = WebRepoId;

        Assert.Equal(blob, StoredPat());
        Assert.Equal(TfsUrl, ReadFile().GetProperty("Tfs").GetProperty("Url").GetString());
        Assert.Equal(WebRepoId, ReadFile().GetProperty("DefaultRepoId").GetString());
    }

    [Fact]
    public void A_save_to_a_read_only_file_is_reported_to_the_caller()
    {
        // B112 fixed: the failed write is logged and returned, so the page can say the PAT was not stored.
        NewService().TeamName = "TEAMX";
        var logger = new RecordingLogger<SettingsService>();
        var settings = new SettingsService(logger);
        var before = File.ReadAllText(SettingsPath);
        File.SetAttributes(SettingsPath, FileAttributes.ReadOnly);

        var saved = settings.SaveTfs(TfsUrl, Pat);

        Assert.False(saved);
        Assert.NotNull(settings.LastSaveError);
        Assert.True(settings.IsConfigured);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<UnauthorizedAccessException>(entry.Exception);
        var restarted = NewService();
        Assert.Equal("TEAMX", restarted.TeamName);
        Assert.False(restarted.IsConfigured);
    }

    [Fact]
    public void Every_save_tells_the_page_frame_whether_it_worked_or_not()
    {
        var settings = NewService();
        var seen = new List<(bool Configured, string? Error)>();
        settings.Changed += () => seen.Add((settings.IsConfigured, settings.LastSaveError));

        settings.SaveTfs(TfsUrl, Pat);
        File.SetAttributes(SettingsPath, FileAttributes.ReadOnly);
        settings.TeamName = "TEAMX";

        Assert.Equal(2, seen.Count);
        Assert.Equal((true, (string?)null), seen[0]);
        Assert.True(seen[1].Configured);
        Assert.NotNull(seen[1].Error);
    }

    [Fact]
    public void A_refused_change_tells_the_page_frame_too()
    {
        var store = new MemorySettingsStore();
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, store, TfsUrl);
        settings.LoadFromStore(allowsWindowsSignIn: false);
        var changes = 0;
        settings.Changed += () => changes++;

        Assert.False(settings.SaveTfs(TfsUrl, "", TfsAuthMode.Windows));

        Assert.Equal(1, changes);
        Assert.NotNull(settings.LastSaveError);
        Assert.Null(store.Text);
    }
}
