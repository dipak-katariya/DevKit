using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Reading the stored settings when DevKit or a page starts, and recovering from a damaged or unreadable copy:
/// it is set aside, never deleted, and the backup taken at the previous save restored when it can be read.
/// </summary>
public partial class SettingsService
{
    private const string MainFileSource = "settings file";
    private const string BackupFileSource = "settings backup";

    private const string LoadFailedMessage =
        "Settings could not be read when DevKit started, so nothing is being saved, to avoid overwriting them. " +
        "Restart DevKit once the settings file can be opened.";

    private const string RestoredMessage =
        "The settings file was damaged. It was kept beside the settings file and the previous version was restored, " +
        "so the latest changes may be missing.";

    private const string DiscardedMessage =
        "The settings file was damaged and there was no earlier version to restore. It was kept beside the settings file, " +
        "and DevKit started with default settings.";

    /// <param name="read">Reads the stored text, or null when there is none; may throw when it cannot be read.</param>
    private void Load(Func<string?> read)
    {
        string? text;
        try
        {
            text = read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Settings could not be read; nothing will be saved until DevKit restarts");
            BlockSaving();
            return;
        }
        catch (CryptographicException ex)
        {
            // Shared mode's file, encrypted with keys the server no longer has or for another account: damaged, as far
            // as this account goes, so it is set aside like a damaged file and never overwritten.
            _logger.LogError(ex, "The stored settings could not be decrypted for this account");
            RecoverFromBackup();
            return;
        }

        if (text is null)
        {
            return;
        }

        if (TryParse(text, MainFileSource, out var loaded))
        {
            if (Apply(loaded))
            {
                SaveDroppingOldPat();
            }

            return;
        }

        RecoverFromBackup();
    }

    /// <summary>
    /// Sets a damaged file aside, never deleting it, and restores the backup taken at the previous save
    /// when there is a readable one. With no backup DevKit starts on defaults, and the next save writes
    /// a new file.
    /// </summary>
    private void RecoverFromBackup()
    {
        try
        {
            _store.SetAsideCorrupt();
            var backup = ReadBackupText();
            if (backup is not null && TryParse(backup, BackupFileSource, out var restored))
            {
                Apply(restored);
                Save();
                LoadProblem = RestoredMessage;
                _logger.LogWarning("Settings were restored from the backup taken at the previous save");
                return;
            }

            LoadProblem = DiscardedMessage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The damaged settings file could not be set aside; nothing will be saved until DevKit restarts");
            BlockSaving();
        }
    }

    /// <summary>The backup's text, or null when there is none or it does not decrypt either.</summary>
    private string? ReadBackupText()
    {
        try
        {
            return _store.ReadBackupText();
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "The settings backup could not be decrypted for this account either");
            return null;
        }
    }

    private bool TryParse(string text, string source, [NotNullWhen(true)] out UserSettings? settings)
    {
        try
        {
            settings = JsonSerializer.Deserialize<UserSettings>(text) ?? new UserSettings();
            settings.Tfs ??= new TfsSettings();
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "The {Source} is not valid settings JSON", source);
            settings = null;
            return false;
        }
    }

    private void BlockSaving()
    {
        _saveBlockedReason = LoadFailedMessage;
        LoadProblem = LoadFailedMessage;
    }
}
