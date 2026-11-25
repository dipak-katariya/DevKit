using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DevKit.Web.Services;

/// <summary>
/// Shared mode: tells every open page of a person that one of their pages saved their settings, so the others show and
/// use them too instead of what they read when they opened. Pages hear the tag that names nobody, and act only on their own.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PersonSettingsSaves
{
    /// <summary>
    /// Raised on the saving page's thread with the person's tag and the id of the store that saved, never the store
    /// itself, which holds that person's key; handlers must not wait on anything.
    /// </summary>
    public event Action<string, Guid>? Saved;

    public void Announce(string person, Guid savedBy) => Saved?.Invoke(person, savedBy);
}

/// <summary>
/// Shared mode: one person's settings, PAT included, kept on the DevKit server in <see cref="PersonVault"/>, sealed
/// by <see cref="ClientVault"/> for them. Who they are comes from their sign-in, never from the page. Every page of
/// theirs reads the same copy, so neither a browser that deletes its data, nor a redeploy, nor another port or PC, ever
/// loses them. A save carries only what changed (<see cref="SettingsChanges"/>), applied to the copy kept at that moment,
/// so the person's other tabs, saving to the same copy, never undo each other.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PersonSettingsStore : ISettingsStore
{
    private const string SaveFailedText =
        "Settings could not be saved just now. Reload the page and try again; if it keeps happening, tell whoever runs DevKit.";

    private const string UnreadableText =
        "Your saved settings were sealed by DevKit running under another Windows account, or are damaged, so DevKit started " +
        "with default settings. Saving anything here replaces them. To keep them instead, whoever runs DevKit starts it " +
        "under the account that saved them, before you save here.";

    private const string ReadFailedText =
        "DevKit could not open your settings just now, so nothing is saved, to keep them from being overwritten. Click Try " +
        "again in a minute; if it keeps happening, tell whoever runs DevKit.";

    private const string UnparsableText =
        "Your saved settings were saved by a later version of DevKit, which this one cannot read, so nothing is saved here, " +
        "to keep them. Ask whoever runs DevKit to run the later version again.";

    private readonly PersonVault _vault;
    private readonly ClientVault _sealer;
    private readonly PersonSettingsSaves _saves;
    private readonly ILogger<PersonSettingsStore> _logger;

    // A reload reads and opens the stored copy on another thread than the page's saves; the kept copy changes under it.
    private readonly object _gate = new();

    // The vault replaces the settings file in place, which fails while a read of it is open, and a reload reads it
    // without holding the settings: this page's reads and saves of the file take turns, as they did under the settings.
    private readonly object _fileTurn = new();
    private SignedInUser? _user;
    private PersonKey? _key;
    private bool _unreadable;

    // What this page last read from the server or saved there, sealed and as text, always changed together. A save
    // applies its changes to that text, unless the server keeps something else by then: what another page saved.
    // _textKept is null until the sealed copy has been opened.
    private string? _sealedKept;
    private string? _textKept;

    public PersonSettingsStore(PersonVault vault, ClientVault sealer, PersonSettingsSaves saves, ILogger<PersonSettingsStore> logger)
    {
        _vault = vault;
        _sealer = sealer;
        _saves = saves;
        _logger = logger;
    }

    /// <summary>Tells this page's saves from another page's in <see cref="PersonSettingsSaves"/>, without handing the store out.</summary>
    public Guid InstanceId { get; } = Guid.NewGuid();

    /// <summary>Tests only: runs inside <see cref="Refresh"/> as it starts reading the stored copy, in this page's turn on the file.</summary>
    internal Action? ReadingStored { get; set; }

    public string Location { get; private set; } = "the DevKit server";

    public PatAtRest Pat => PatAtRest.InSealedText;

    public string? Person => _user is null ? null : ClientVault.PersonTag(_user);

    public string SaveFailedMessage => SaveFailedText;

    public string UnreadableMessage => UnreadableText;

    public string ReadFailedMessage => ReadFailedText;

    public string UnparsableMessage => UnparsableText;

    /// <summary>
    /// Whether the server keeps settings for the person, whether or not they open now; also when they could not be
    /// read, so that nothing is moved or saved over them.
    /// </summary>
    public bool HasSettings => _sealedKept is not null || _unreadable;

    /// <summary>Reads <paramref name="user"/>'s sealed settings from the server.</summary>
    public void Open(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _user = user;
        Location = $"the DevKit server, encrypted for {user.Name}";
        try
        {
            _sealedKept = _vault.Load(user);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "A person's settings could not be read from the settings folder; nothing is saved for them on this page");
            _unreadable = true;
        }
    }

    /// <summary>
    /// Reads the person's sealed settings from the server again, and opens them: what another of their pages saved
    /// since, or what could not be read before. Throws when they cannot be read or opened now, which the caller logs;
    /// what this page kept then stays as it was, so its next save still merges onto the copy it holds the text of.
    /// A save by this page while the read ran is newer than what was read, so the read is then not kept.
    /// </summary>
    public void Refresh()
    {
        var user = Opened();
        string? keptWhenStarted;
        PersonKey? key;
        lock (_gate)
        {
            keptWhenStarted = _sealedKept;
            key = _key;
        }

        string? loaded;
        lock (_fileTurn)
        {
            ReadingStored?.Invoke();
            loaded = _vault.Load(user);
        }

        var opened = loaded is null ? null : _sealer.Open(user, loaded, key);
        lock (_gate)
        {
            if (!string.Equals(_sealedKept, keptWhenStarted, StringComparison.Ordinal))
            {
                return;
            }

            _sealedKept = loaded;
            _textKept = opened?.Text;
            _key = opened?.Key ?? _key;
            _unreadable = false;
        }
    }

    /// <summary>The person's settings text, or null when the server keeps none; their key is kept for the saves that follow.</summary>
    public string? ReadText()
    {
        string? sealedKept;
        PersonKey? key;
        lock (_gate)
        {
            if (_unreadable)
            {
                throw new IOException("The settings could not be read when the page opened.");
            }

            if (_sealedKept is null)
            {
                return null;
            }

            if (_textKept is not null)
            {
                return _textKept;
            }

            sealedKept = _sealedKept;
            key = _key;
        }

        var opened = _sealer.Open(Opened(), sealedKept, key);
        lock (_gate)
        {
            if (string.Equals(_sealedKept, sealedKept, StringComparison.Ordinal))
            {
                _key = opened.Key;
                _textKept = opened.Text;
            }
        }

        return opened.Text;
    }

    /// <summary>The settings kept before the last save, which <see cref="SettingsService"/> falls back on when the latest do not open.</summary>
    public string? ReadBackupText()
    {
        var user = Opened();
        if (_vault.LoadBackup(user) is not { } sealedBackup)
        {
            return null;
        }

        var opened = _sealer.Open(user, sealedBackup);
        lock (_gate)
        {
            _key = opened.Key;
        }

        return opened.Text;
    }

    public void DeleteBackup() => _vault.DeleteBackup(Opened());

    public void SetAsideCorrupt()
    {
        // Settings that do not open stay in place until a save replaces them, in case DevKit runs again under the account
        // that sealed them; the save that replaces them keeps them as its backup.
    }

    /// <summary>Saves <paramref name="text"/> whole, replacing whatever the server keeps.</summary>
    public void Write(string text) => Write(text, previousText: null);

    /// <summary>
    /// Seals and keeps the settings, under the person's key (a new one on their first save, or when theirs did not open).
    /// What changed since <paramref name="previousText"/> is applied to the copy kept at that moment; without
    /// <paramref name="previousText"/>, or when the server keeps none, the text replaces it whole. Throws when it was not
    /// kept, which <see cref="SettingsService"/> reports.
    /// </summary>
    public void Write(string text, string? previousText)
    {
        var user = Opened();
        var changes = previousText is null ? null : SettingsChanges.Between(previousText, text);
        string? sealedText = null;
        string? merged = null;
        lock (_fileTurn)
        {
            _vault.Update(user, kept =>
            {
                var basis = kept is null || changes is null ? null : BasisFrom(user, kept);
                merged = basis is null ? text : SettingsChanges.Apply(basis, changes!);
                var key = KeyForSealing();
                sealedText = _sealer.Seal(user, key, merged);
                return sealedText;
            });
        }

        lock (_gate)
        {
            _sealedKept = sealedText;
            _textKept = merged;
        }

        _saves.Announce(ClientVault.PersonTag(user), InstanceId);
    }

    // Making a key locks it with DPAPI-NG, which can wait on the domain, so it is made without holding the gate: a reload
    // or another read of this page never waits for it. When two saves race to make one, the first kept wins.
    private PersonKey KeyForSealing()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                return _key;
            }
        }

        var made = _sealer.NewKey();
        lock (_gate)
        {
            return _key ??= made;
        }
    }

    /// <summary>The text the kept copy holds: this page's own last copy, or another page's newer save opened.</summary>
    private string? BasisFrom(SignedInUser user, string kept)
    {
        PersonKey? key;
        lock (_gate)
        {
            if (_textKept is not null && string.Equals(kept, _sealedKept, StringComparison.Ordinal))
            {
                return _textKept;
            }

            key = _key;
        }

        try
        {
            var opened = _sealer.Open(user, kept, key);
            lock (_gate)
            {
                // Another page may have made the person's first key; this one seals with it from now on.
                _key = opened.Key;
            }

            return opened.Text;
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "The settings kept for this person do not open, so this save replaces them");
            return null;
        }
    }

    private SignedInUser Opened() => _user ?? throw new IOException("Settings cannot be used before DevKit knows who is signed in.");
}
