using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>What reading the stored settings again (<see cref="SettingsService.ReloadFromStore"/>) came to.</summary>
public enum SettingsReload
{
    /// <summary>The stored settings are in use; saving goes on.</summary>
    Reloaded,

    /// <summary>They could not be read now; the page keeps what it has, and saving stays stopped when it was.</summary>
    StillUnreadable,

    /// <summary>The store keeps none; the page keeps what it has.</summary>
    NothingStored,

    /// <summary>They opened, but are not settings this DevKit reads; the page keeps what it has.</summary>
    NotReadable,

    /// <summary>They were saved again on every read; the page keeps what it has.</summary>
    Busy
}

/// <summary>
/// Reading the stored settings when DevKit or a page starts, and recovering from a damaged or unreadable copy:
/// it is set aside, never deleted, and the backup taken at the previous save restored when it can be read.
/// </summary>
public partial class SettingsService
{
    private const string MainFileSource = "settings file";
    private const string BackupFileSource = "settings backup";

    // Each try is one read of the store; saves that keep landing during them are rare, and then a later reload follows.
    private const int MaxReloadAttempts = 3;

    private const string LoadFailedMessage =
        "Settings could not be read when DevKit started, so nothing is being saved, to avoid overwriting them. " +
        "Click Try again once the settings file can be opened, or restart DevKit.";

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
            // Shared mode's settings, sealed with another server's key or for another account: damaged, as far as this
            // account goes, so they are set aside like a damaged file where the store can, and never read as theirs.
            _logger.LogError(ex, "The stored settings could not be decrypted for this account");
            RecoverFromBackup();
            return;
        }

        UseStoredText(text);
    }

    /// <summary>Puts the stored text in use, as a page that opens does: none, settings, or text it cannot use.</summary>
    private void UseStoredText(string? text)
    {
        if (text is null)
        {
            StartedEmpty = true;
            return;
        }

        if (TryParse(text, MainFileSource, out var loaded))
        {
            if (Apply(loaded))
            {
                ResaveInCurrentForm();
            }

            return;
        }

        if (_store.UnparsableMessage is { } keepThem)
        {
            // Settings that opened but are not in a form this build reads (a later DevKit's) are the person's own:
            // saving defaults over them would lose them.
            _saveBlockedReason = keepThem;
            LoadProblem = keepThem;
            return;
        }

        RecoverFromBackup();
    }

    /// <summary>Whether saving is stopped because the stored settings could not be read: saving would replace them.</summary>
    public bool SavingBlocked => _saveBlockedReason is not null;

    /// <summary>
    /// Reads the stored settings again and puts them in use: after another page of the same person saved them, or to try
    /// again once they could not be read. A try again takes the same paths as a page that opens, so it lets saving go on
    /// whenever opening the page would. After another page's save, settings that cannot be read, or none, leave this
    /// page with what it has, and a change this page could not save is kept on top of what was read. The slow part,
    /// reading and opening the stored copy, runs without holding the settings, so the page never waits on the domain.
    /// Raises <see cref="Changed"/> once done. Throws only on a fault the log has no reason for, which the caller logs.
    /// </summary>
    public SettingsReload ReloadFromStore()
    {
        for (var attempt = 1; attempt <= MaxReloadAttempts; attempt++)
        {
            var changesBefore = Interlocked.Read(ref _changes);
            var read = ReadStoredAgain();
            SettingsReload? outcome = null;
            lock (_sync)
            {
                // A save made while this read ran has changed what this page holds; what was read may predate it.
                if (changesBefore == Interlocked.Read(ref _changes))
                {
                    outcome = SavingBlocked ? OpenAgain(read) : FollowStored(read);
                }
            }

            if (outcome is { } done)
            {
                Changed?.Invoke();
                return done;
            }

            _logger.LogDebug("The settings were saved while they were read again; reading them once more (attempt {Attempt})", attempt);
        }

        _logger.LogWarning("The settings kept changing while they were read again; this page keeps the settings it has");
        return SettingsReload.Busy;
    }

    /// <summary>What the store holds now, or why it could not be read (logged).</summary>
    private sealed record StoredRead(string? Text, Exception? Failure);

    private StoredRead ReadStoredAgain()
    {
        try
        {
            _store.Refresh();
            return new StoredRead(_store.ReadText(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError(ex, "The stored settings could not be read again");
            return new StoredRead(null, ex);
        }
    }

    /// <summary>Try again after the settings did not open: the paths a page that opens takes, from the same read.</summary>
    private SettingsReload OpenAgain(StoredRead read)
    {
        var blockedReason = _saveBlockedReason;
        _saveBlockedReason = null;
        if (string.Equals(LoadProblem, blockedReason, StringComparison.Ordinal))
        {
            LoadProblem = null;
        }

        switch (read.Failure)
        {
            case CryptographicException:
                RecoverFromBackup();
                break;
            case not null:
                BlockSaving();
                break;
            default:
                UseStoredText(read.Text);
                break;
        }

        if (!SavingBlocked)
        {
            return SettingsReload.Reloaded;
        }

        return read.Failure is null ? SettingsReload.NotReadable : SettingsReload.StillUnreadable;
    }

    /// <summary>
    /// After another page saved: what it saved is put in use. Anything else leaves this page as it is, notices it shows
    /// included, since saving here is not stopped.
    /// </summary>
    private SettingsReload FollowStored(StoredRead read)
    {
        if (read.Failure is not null)
        {
            return SettingsReload.StillUnreadable;
        }

        if (read.Text is null)
        {
            _logger.LogInformation("The store keeps no settings now; this page keeps the settings it has");
            return SettingsReload.NothingStored;
        }

        if (!TryParse(read.Text, MainFileSource, out var loaded))
        {
            return SettingsReload.NotReadable;
        }

        if (ApplyKeepingUnsaved(loaded, read.Text))
        {
            ResaveInCurrentForm();
        }

        return SettingsReload.Reloaded;
    }

    /// <summary>
    /// Puts <paramref name="loaded"/> in use with any change this page could not save on top, so a reload never drops it
    /// without a word: the save problem still shows, and the next save sends it. True when the stored PAT needs saving
    /// again in the current form.
    /// </summary>
    private bool ApplyKeepingUnsaved(UserSettings loaded, string storedText)
    {
        var unsaved = UnsavedChanges();
        var stored = Prepare(loaded);
        if (unsaved is null || !TryParse(SettingsChanges.Apply(storedText, unsaved), MainFileSource, out var withUnsaved))
        {
            return Publish(stored);
        }

        Publish(Prepare(withUnsaved) with { SavedText = stored.SavedText });
        _logger.LogInformation("A change this page could not save is kept on top of the settings another page saved");
        return stored.NeedsResave;
    }

    /// <summary>What the settings in use changed since they were last stored, when a save of them failed; null otherwise.</summary>
    private JsonObject? UnsavedChanges()
    {
        if (LastSaveError is null || _savedText is null)
        {
            return null;
        }

        var changes = SettingsChanges.Between(_savedText, PersistableText());
        return changes.Count > 0 ? changes : null;
    }

    /// <summary>Saves the settings again with the stored PAT in its current form; a failure is logged and shown as a save problem.</summary>
    private void ResaveInCurrentForm()
    {
        if (!SaveDroppingOldPat())
        {
            _logger.LogError("The settings could not be saved again with the stored PAT in its current form: {Reason}", LastSaveError);
        }
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

            LoadProblem = _store.UnreadableMessage ?? DiscardedMessage;
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
            settings.StatusUpdate ??= new StatusUpdateSettings();
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
        _saveBlockedReason = _store.ReadFailedMessage ?? LoadFailedMessage;
        LoadProblem = _saveBlockedReason;
    }
}
