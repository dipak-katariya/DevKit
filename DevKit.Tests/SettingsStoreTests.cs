using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The per-user settings file: moving settings out of the program folder, keeping a backup, recovering
/// from a damaged file, never overwriting one it could not read, binding the PAT to its server, and
/// keeping the file private. Every test works in its own folder, so these run alongside the rest.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private const string TfsUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string OtherServerUrl = "https://elsewhere.example.test/tfs/DefaultCollection";
    private const string Pat = "contoso-test-pat-5d2e8b10";
    private const string CurrentFormat = "enc:v2:";

    private readonly string _folder =
        Path.Combine(AppContext.BaseDirectory, "settings-store-tests", Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);

    private string StorePath => Path.Combine(_folder, "store", SettingsFile.FileName);

    private string LegacyPath => Path.Combine(_folder, "bin", SettingsFile.FileName);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    private SettingsService Open(ILogger<SettingsService>? logger = null) =>
        new(logger ?? NullLogger<SettingsService>.Instance, StorePath, LegacyPath);

    private void WriteStore(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, json);
    }

    private JsonElement ReadStore() => ReadJson(StorePath);

    private static JsonElement ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private string StoredPat() => ReadStore().GetProperty("Tfs").GetProperty("Pat").GetString()!;

    private string[] SetAsideFiles() =>
        Directory.GetFiles(Path.GetDirectoryName(StorePath)!, SettingsFile.FileName + ".corrupt-*");

    [Fact]
    public void Settings_beside_the_program_move_to_the_per_user_file_on_first_start()
    {
        File.WriteAllText(LegacyPath, $$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{Pat}}" }, "TeamName": "TEAMX" }""");

        var settings = Open();

        Assert.Equal("TEAMX", settings.TeamName);
        Assert.Equal(Pat, settings.Tfs.Pat);
        Assert.Equal(Path.GetFullPath(StorePath), settings.SettingsFilePath);
        Assert.False(File.Exists(LegacyPath));
        Assert.Equal("TEAMX", ReadStore().GetProperty("TeamName").GetString());
        Assert.StartsWith(CurrentFormat, StoredPat());
        Assert.DoesNotContain(Pat, File.ReadAllText(StorePath));
    }

    [Fact]
    public void A_pat_saved_again_in_the_current_form_leaves_no_older_copy_beside_the_file()
    {
        WriteStore($$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{Pat}}" } }""");

        Open();

        Assert.StartsWith(CurrentFormat, StoredPat());
        Assert.All(Directory.GetFiles(Path.GetDirectoryName(StorePath)!),
            file => Assert.DoesNotContain(Pat, File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_per_user_file_wins_and_the_old_file_is_left_alone()
    {
        WriteStore("""{ "TeamName": "STORE" }""");
        File.WriteAllText(LegacyPath, """{ "TeamName": "LEGACY" }""");
        var logger = new RecordingLogger<SettingsService>();

        var settings = Open(logger);

        Assert.Equal("STORE", settings.TeamName);
        Assert.Equal("""{ "TeamName": "LEGACY" }""", File.ReadAllText(LegacyPath));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void The_same_file_named_twice_is_read_in_place_and_never_deleted()
    {
        File.WriteAllText(LegacyPath, """{ "TeamName": "TEAMX" }""");

        var settings = new SettingsService(NullLogger<SettingsService>.Instance, LegacyPath.ToUpperInvariant(), LegacyPath);

        Assert.Equal("TEAMX", settings.TeamName);
        Assert.Equal("""{ "TeamName": "TEAMX" }""", File.ReadAllText(LegacyPath));
    }

    [Fact]
    public void A_relative_settings_path_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new SettingsService(NullLogger<SettingsService>.Instance, SettingsFile.FileName, LegacyPath));
    }

    [Fact]
    public void The_test_run_never_resolves_to_the_users_own_settings_file()
    {
        Assert.Equal(TestSettingsLocation.SettingsPath, SettingsFile.ResolvePath());
    }

    [Fact]
    public void A_file_named_by_the_environment_is_used_as_it_is_and_nothing_is_moved_into_it()
    {
        Assert.True(SettingsFile.IsLocationOverridden);
        Assert.Null(SettingsService.LegacySettingsPathToMove());
    }

    [Fact]
    public void Without_a_legacy_location_an_old_file_beside_the_program_stays_where_it_is()
    {
        File.WriteAllText(LegacyPath, """{ "TeamName": "LEGACY" }""");

        var settings = new SettingsService(NullLogger<SettingsService>.Instance, StorePath, legacySettingsPath: null);

        Assert.Equal("", settings.TeamName);
        Assert.True(File.Exists(LegacyPath));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void Each_save_keeps_the_previous_version_as_the_backup()
    {
        var settings = Open();

        settings.TeamName = "ONE";
        settings.TeamName = "TWO";

        Assert.Null(settings.LastSaveError);
        Assert.Equal("TWO", ReadStore().GetProperty("TeamName").GetString());
        Assert.Equal("ONE", ReadJson(StorePath + ".bak").GetProperty("TeamName").GetString());
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    [Fact]
    public void A_failed_save_leaves_no_temporary_copy_behind()
    {
        var settings = Open();
        settings.TeamName = "ONE";
        File.SetAttributes(StorePath, FileAttributes.ReadOnly);

        settings.TeamName = "TWO";

        Assert.NotNull(settings.LastSaveError);
        Assert.False(File.Exists(StorePath + ".tmp"));
        Assert.Equal("ONE", ReadStore().GetProperty("TeamName").GetString());
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_the_backup_restored()
    {
        var first = Open();
        first.TeamName = "ONE";
        first.TeamName = "TWO";
        File.WriteAllText(StorePath, """{ "TeamName": "TW""");
        var logger = new RecordingLogger<SettingsService>();

        var settings = Open(logger);

        Assert.Equal("ONE", settings.TeamName);
        Assert.NotNull(settings.LoadProblem);
        Assert.Equal("ONE", ReadStore().GetProperty("TeamName").GetString());
        Assert.Equal("""{ "TeamName": "TW""", File.ReadAllText(Assert.Single(SetAsideFiles())));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is JsonException);
    }

    [Fact]
    public void Settings_that_could_not_be_read_are_never_overwritten()
    {
        const string Original = """{ "TeamName": "KEEP" }""";
        WriteStore(Original);
        SettingsService settings;
        using (new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            settings = Open();
        }

        settings.TeamName = "OVERWRITE";

        Assert.NotNull(settings.LoadProblem);
        Assert.NotNull(settings.LastSaveError);
        Assert.False(settings.Save());
        Assert.Equal(Original, File.ReadAllText(StorePath));
    }

    [Fact]
    public void The_pat_only_decrypts_for_the_server_it_was_saved_for()
    {
        Open().SaveTfs(TfsUrl, Pat);
        var blob = StoredPat();
        File.WriteAllText(StorePath, File.ReadAllText(StorePath).Replace(TfsUrl, OtherServerUrl, StringComparison.Ordinal));
        var logger = new RecordingLogger<SettingsService>();

        var settings = Open(logger);
        settings.TeamName = "TEAMX";

        Assert.Equal("", settings.Tfs.Pat);
        Assert.False(settings.IsConfigured);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
        Assert.Equal(blob, StoredPat());
    }

    [Fact]
    public void The_same_server_typed_differently_still_decrypts_the_pat()
    {
        Open().SaveTfs(TfsUrl, Pat);
        File.WriteAllText(StorePath, File.ReadAllText(StorePath).Replace(TfsUrl, TfsUrl.ToUpperInvariant() + "/", StringComparison.Ordinal));

        Assert.Equal(Pat, Open().Tfs.Pat);
    }

    [Fact]
    public void A_pat_in_the_previous_format_is_read_and_saved_again_bound_to_its_server()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WriteStore($$"""{ "Tfs": { "Url": "{{TfsUrl}}", "Pat": "{{LegacyPatBlob.Protect(Pat)}}" } }""");

        var settings = Open();

        Assert.Equal(Pat, settings.Tfs.Pat);
        Assert.StartsWith(CurrentFormat, StoredPat());
        Assert.Equal(Pat, Open().Tfs.Pat);
    }

    [Fact]
    public void The_file_and_its_backup_are_private_to_the_current_account()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(LegacyPath, """{ "TeamName": "ONE" }""");
        var settings = Open();
        settings.TeamName = "TWO";

        AssertPrivate(StorePath);
        AssertPrivate(StorePath + ".bak");
    }

    [Fact]
    public void A_file_created_some_other_way_is_made_private_at_startup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        WriteStore("""{ "TeamName": "TEAMX" }""");
        Assert.False(new FileInfo(StorePath).GetAccessControl().AreAccessRulesProtected);

        Open();

        AssertPrivate(StorePath);
    }

    [Fact]
    public void Saves_from_many_callers_at_once_all_land_in_one_valid_file()
    {
        const int Callers = 40;
        var settings = Open();

        Parallel.For(0, Callers, i => settings.SetRepoQaBranch($"repo-{i}", $"qa-{i}"));

        Assert.Null(settings.LastSaveError);
        var reloaded = Open();
        Assert.Equal(Callers, reloaded.RepoBranches.Count);
        Assert.Equal("qa-7", reloaded.GetRepoBranches("repo-7").QaBranch);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivate(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var granted = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToHashSet();
        using var current = WindowsIdentity.GetCurrent();
        var trusted = new HashSet<SecurityIdentifier>
        {
            current.User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

        Assert.True(security.AreAccessRulesProtected, "inherited permissions are cut off");
        Assert.Contains(current.User!, granted);
        Assert.Subset(trusted, granted);
    }
}
