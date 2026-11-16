using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DevKit.Web.Services;

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
        "DevKit could not open your settings just now, so nothing is saved, to keep them from being overwritten. Reload the " +
        "page in a minute; if it keeps happening, tell whoever runs DevKit.";

    private const string UnparsableText =
        "Your saved settings were saved by a later version of DevKit, which this one cannot read, so nothing is saved here, " +
        "to keep them. Ask whoever runs DevKit to run the later version again.";

    private readonly PersonVault _vault;
    private readonly ClientVault _sealer;
    private readonly ILogger<PersonSettingsStore> _logger;
    private SignedInUser? _user;
    private PersonKey? _key;
    private bool _unreadable;

    // What this page last read from the server or saved there, sealed and as text. A save applies its changes to that
    // text, unless the server keeps something else by then: what another page saved.
    private string? _sealedKept;
    private string? _textKept;

    public PersonSettingsStore(PersonVault vault, ClientVault sealer, ILogger<PersonSettingsStore> logger)
    {
        _vault = vault;
        _sealer = sealer;
        _logger = logger;
    }

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

    /// <summary>The person's settings text, or null when the server keeps none; their key is kept for the saves that follow.</summary>
    public string? ReadText()
    {
        if (_unreadable)
        {
            throw new IOException("The settings could not be read when the page opened.");
        }

        if (_sealedKept is null)
        {
            return null;
        }

        var opened = _sealer.Open(Opened(), _sealedKept);
        _key = opened.Key;
        _textKept = opened.Text;
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
        _key = opened.Key;
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
        _vault.Update(user, kept =>
        {
            var basis = kept is null || changes is null ? null : BasisFrom(user, kept);
            merged = basis is null ? text : SettingsChanges.Apply(basis, changes!);
            _key ??= _sealer.NewKey();
            sealedText = _sealer.Seal(user, _key, merged);
            return sealedText;
        });
        _sealedKept = sealedText;
        _textKept = merged;
    }

    /// <summary>The text the kept copy holds: this page's own last copy, or another page's newer save opened.</summary>
    private string? BasisFrom(SignedInUser user, string kept)
    {
        if (string.Equals(kept, _sealedKept, StringComparison.Ordinal))
        {
            return _textKept;
        }

        try
        {
            var opened = _sealer.Open(user, kept);
            // Another page may have made the person's first key; this one seals with it from now on.
            _key = opened.Key;
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
