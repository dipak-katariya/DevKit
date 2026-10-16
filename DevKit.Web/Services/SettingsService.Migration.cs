namespace DevKit.Web.Services;

/// <summary>
/// Carrying settings forward when the app moves: a newer target framework folder, and the move from
/// the program folder to the per-user settings file.
/// </summary>
public partial class SettingsService
{
    private const string TargetFolderPrefix = "net";

    /// <summary>
    /// The settings file beside the program, which builds before the per-user file used; null when
    /// <see cref="SettingsFile.PathVariable"/> names the file, since that file is then used exactly as
    /// it is and nothing is moved into it.
    /// </summary>
    internal static string? LegacySettingsPathToMove() =>
        SettingsFile.IsLocationOverridden ? null : Path.Combine(AppContext.BaseDirectory, SettingsFile.FileName);

    /// <summary>
    /// Builds before the per-user settings file kept usersettings.json beside the program, where a clean
    /// or a rebuild deleted it and other accounts on the machine could read it. On the first start with
    /// no per-user file, that file (or one from an earlier target framework) is copied in, read back
    /// to check it, and only then removed. Returns the settings text moved, or null when nothing was.
    /// </summary>
    private string? MoveLegacySettings(SettingsFile file, string? legacySettingsPath)
    {
        if (legacySettingsPath is null || file.IsSameFileAs(legacySettingsPath))
        {
            return null;
        }

        if (file.Exists)
        {
            if (File.Exists(legacySettingsPath))
            {
                _logger.LogWarning("An older settings file beside the program was ignored because the per-user settings file exists; delete it once it is no longer needed");
            }

            return null;
        }

        AdoptFromPreviousTarget(legacySettingsPath, _logger);
        if (!File.Exists(legacySettingsPath))
        {
            return null;
        }

        var text = File.ReadAllText(legacySettingsPath);
        file.Write(text);
        if (!string.Equals(file.ReadText(), text, StringComparison.Ordinal))
        {
            throw new IOException("The settings copied to the per-user settings file did not read back the same.");
        }

        DeleteMovedLegacyFile(legacySettingsPath);
        return text;
    }

    private void DeleteMovedLegacyFile(string legacySettingsPath)
    {
        try
        {
            File.Delete(legacySettingsPath);
            _logger.LogInformation("Settings moved from the program folder to the per-user settings file");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Settings were moved to the per-user settings file, but the old copy beside the program could not be deleted; delete it by hand");
        }
    }

    /// <summary>
    /// A source build keeps its files in bin/&lt;Configuration&gt;/&lt;target framework&gt;/, so moving to a
    /// newer target framework moves the binary away from the settings it wrote. When the settings file
    /// is missing from such a folder, it is copied from the newest older target framework folder beside
    /// it (bin/Debug/net9.0 for bin/Debug/net10.0) instead of the app opening unconfigured. The old
    /// file is left in place. Returns the file adopted from, or null when nothing was.
    /// </summary>
    public static string? AdoptFromPreviousTarget(string settingsPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(logger);
        if (File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            var previous = FindPreviousTargetSettings(Path.GetFullPath(settingsPath));
            if (previous is null)
            {
                return null;
            }

            File.Copy(previous, settingsPath, overwrite: false);
            logger.LogInformation("Settings carried over from {Folder} after a target framework upgrade",
                Path.GetFileName(Path.GetDirectoryName(previous)));
            return previous;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Settings from an earlier target framework could not be carried over; starting unconfigured");
            return null;
        }
    }

    private static string? FindPreviousTargetSettings(string settingsPath)
    {
        var outputDir = Path.GetDirectoryName(settingsPath);
        var configurationDir = Path.GetDirectoryName(outputDir);
        if (outputDir is null || configurationDir is null || TargetVersion(Path.GetFileName(outputDir)) is not { } current)
        {
            return null;
        }

        return Directory.EnumerateDirectories(configurationDir)
            .Select(dir => (Dir: dir, Version: TargetVersion(Path.GetFileName(dir))))
            .Where(candidate => candidate.Version is not null && candidate.Version < current)
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => Path.Combine(candidate.Dir, Path.GetFileName(settingsPath)))
            .FirstOrDefault(file => IsConfinedRegularFile(file, configurationDir));
    }

    /// <summary>The version a build output folder is named for (net10.0 is 10.0), or null for any other folder.</summary>
    private static Version? TargetVersion(string folderName) =>
        folderName.StartsWith(TargetFolderPrefix, StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(folderName[TargetFolderPrefix.Length..], out var version)
            ? version
            : null;

    /// <summary>Exists inside <paramref name="root"/>, and neither the file nor its folder is a link.</summary>
    private static bool IsConfinedRegularFile(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(full))
        {
            return false;
        }

        var file = new FileInfo(full);
        return !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
               && file.Directory is { } folder
               && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}
