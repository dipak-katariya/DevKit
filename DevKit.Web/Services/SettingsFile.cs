using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DevKit.Web.Services;

/// <summary>
/// Where <see cref="SettingsService"/> keeps the settings text: the per-user file in local mode, or in shared mode
/// the signed-in person's own encrypted file on the server (<see cref="ServerSettingsStore"/>).
/// </summary>
internal interface ISettingsStore
{
    /// <summary>Where the settings are kept, as the Settings page names it.</summary>
    string Location { get; }

    /// <summary>The stored text, or null when there is none yet.</summary>
    string? ReadText();

    /// <summary>The previous version's text, or null when none is kept.</summary>
    string? ReadBackupText();

    /// <summary>Replaces the stored text; the version replaced becomes the backup where one is kept.</summary>
    void Write(string text);

    /// <summary>Removes the previous version kept by the last save; nothing happens when there is none.</summary>
    void DeleteBackup();

    /// <summary>Keeps unreadable text aside, where the store can, so nothing overwrites it.</summary>
    void SetAsideCorrupt();
}

/// <summary>
/// The settings file on disk: where it lives, reading it, and replacing it without ever leaving a
/// half-written file behind. On Windows only the current user, SYSTEM and Administrators can open it.
/// </summary>
internal sealed class SettingsFile : ISettingsStore
{
    public const string FileName = "usersettings.json";

    /// <summary>Overrides the location; tests and side-by-side instances point it at their own file.</summary>
    public const string PathVariable = "DEVKIT_SETTINGS_PATH";

    private const string AppFolderName = "DevKit";
    private const string BackupSuffix = ".bak";
    private const string TempSuffix = ".tmp";
    private const string CorruptSuffixFormat = ".corrupt-{0:yyyyMMdd-HHmmss}";
    private const int WriteBufferBytes = 4096;

    public SettingsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"The settings file location must be a full path; check {PathVariable}.", nameof(path));
        }

        FullPath = Path.GetFullPath(path);
    }

    public string FullPath { get; }

    public string Location => FullPath;

    public string BackupPath => FullPath + BackupSuffix;

    private string TempPath => FullPath + TempSuffix;

    public bool Exists => File.Exists(FullPath);

    /// <summary>True when <see cref="PathVariable"/> names the file to use.</summary>
    public static bool IsLocationOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable));

    /// <summary>
    /// <see cref="PathVariable"/> when it is set, otherwise usersettings.json in the user's local
    /// application data folder: private to the user, and untouched by a rebuild or a clean.
    /// </summary>
    public static string ResolvePath()
    {
        if (IsLocationOverridden)
        {
            return Environment.GetEnvironmentVariable(PathVariable)!.Trim();
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localData))
        {
            throw new InvalidOperationException($"There is no local application data folder for this account; set {PathVariable}.");
        }

        return Path.Combine(localData, AppFolderName, FileName);
    }

    /// <summary>
    /// Same file, compared the way Windows compares names. Deciding this wrongly would move the
    /// settings onto themselves and then delete them.
    /// </summary>
    public bool IsSameFileAs(string otherPath) =>
        string.Equals(FullPath, Path.GetFullPath(otherPath), StringComparison.OrdinalIgnoreCase);

    public string? ReadText() => ReadShared(FullPath);

    public string? ReadBackupText() => ReadShared(BackupPath);

    /// <summary>
    /// Replaces the file with <paramref name="text"/>. The text is flushed to a private temporary file
    /// first and then swapped in, so a crash leaves either the old file or the new one, never a torn
    /// one. The version replaced becomes the backup.
    /// </summary>
    public void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FullPath)!);

        // A temporary file left by a crash keeps the security it was created with, so start afresh.
        File.Delete(TempPath);
        using (var stream = CreatePrivateFile(TempPath))
        {
            stream.Write(Encoding.UTF8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }

        try
        {
            if (File.Exists(FullPath))
            {
                // Replace keeps the replaced file's permissions, so a file restricted once stays restricted.
                File.Replace(TempPath, FullPath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TempPath, FullPath);
            }
        }
        finally
        {
            // Gone after a successful swap; after a failed one it is a stray copy of the settings.
            File.Delete(TempPath);
        }
    }

    public void DeleteBackup() => File.Delete(BackupPath);

    /// <summary>Moves an unreadable file aside, beside the settings file, so nothing overwrites it.</summary>
    public void SetAsideCorrupt()
    {
        var stamped = FullPath + string.Format(CultureInfo.InvariantCulture, CorruptSuffixFormat, DateTime.UtcNow);
        var target = stamped;
        var copy = 1;
        while (File.Exists(target))
        {
            copy++;
            target = $"{stamped}-{copy}";
        }

        File.Move(FullPath, target);
    }

    /// <summary>
    /// Limits the file and its backup to the current user, SYSTEM and Administrators. Files written
    /// here already are; this catches one created some other way, such as by an older DevKit.
    /// </summary>
    public void RestrictExistingFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var path in new[] { FullPath, BackupPath }.Where(File.Exists))
        {
            new FileInfo(path).SetAccessControl(OwnerOnlySecurity());
        }
    }

    // Shares read, write and delete so a save in progress elsewhere never makes this read fail.
    private static string? ReadShared(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Created with its permissions in place, so there is no moment when others can open it.
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
                WriteBufferBytes, FileOptions.WriteThrough, OwnerOnlySecurity());
        }

        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, WriteBufferBytes, FileOptions.WriteThrough);
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity OwnerOnlySecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var account in TrustedAccounts())
        {
            security.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] TrustedAccounts()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new[]
        {
            identity.User ?? throw new InvalidOperationException("The current Windows account has no security identifier."),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
    }
}
