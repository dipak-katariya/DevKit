using Microsoft.JSInterop;

namespace DevKit.Web.Services;

/// <summary>
/// Shared mode: one person's settings, PAT included, kept only in their own browser on their own PC, sealed by
/// <see cref="ClientVault"/>; the server keeps nothing of theirs. The browser is asked once, when the page starts
/// (<see cref="OpenAsync"/>). Each save is sealed at once and sent to the browser after the one before it, and a save
/// the browser did not keep is reported through <see cref="WriteFailed"/>.
/// </summary>
internal sealed class ClientSettingsStore : ISettingsStore
{
    internal const string LoadFunction = "devKit.loadVault";
    internal const string SaveFunction = "devKit.saveVault";

    private const string SaveFailedText =
        "Settings could not be saved in this browser. Reload the page and try again; if it keeps happening, check that this " +
        "browser keeps cookies for DevKit, or tell whoever runs DevKit.";

    private const string UnreadableText =
        "The settings this browser keeps for you were saved by DevKit running under another Windows account, or are damaged, so " +
        "DevKit started with default settings. Saving anything here replaces them. To keep them instead, whoever runs DevKit " +
        "starts it under the account that saved them, before you save here.";

    private const string ReadFailedText =
        "DevKit could not open your settings just now, so nothing is saved, to keep them from being overwritten. Reload the " +
        "page in a minute; if it keeps happening, tell whoever runs DevKit.";

    private readonly IJSRuntime _js;
    private readonly ClientVault _vault;
    private readonly ILogger<ClientSettingsStore> _logger;
    private SignedInUser? _user;
    private string? _sealed;
    private PersonKey? _key;
    private Task<bool> _lastWrite = Task.FromResult(true);

    public ClientSettingsStore(IJSRuntime js, ClientVault vault, ILogger<ClientSettingsStore> logger)
    {
        _js = js;
        _vault = vault;
        _logger = logger;
    }

    public string Location { get; private set; } = "this browser";

    public PatAtRest Pat => PatAtRest.InSealedText;

    public string SaveFailedMessage => SaveFailedText;

    public string UnreadableMessage => UnreadableText;

    public string ReadFailedMessage => ReadFailedText;

    /// <summary>Whether the browser keeps settings for the person, whether or not this server can read them.</summary>
    public bool HasSettings => _sealed is not null;

    /// <summary>Raised, with what to tell the person, when the browser did not keep a save.</summary>
    public event Action<string>? WriteFailed;

    /// <summary>Asks the browser for <paramref name="user"/>'s sealed settings; who that is comes from their sign-in, never from the page.</summary>
    public async Task OpenAsync(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        _user = user;
        Location = $"this browser, on this PC only, for {user.Name}";
        _sealed = await _js.InvokeAsync<string?>(LoadFunction);
    }

    /// <summary>The person's settings text, or null when the browser keeps none; their key is kept for the saves that follow.</summary>
    public string? ReadText()
    {
        if (_sealed is null)
        {
            return null;
        }

        var opened = _vault.Open(Opened(), _sealed);
        _key = opened.Key;
        return opened.Text;
    }

    public string? ReadBackupText() => null;

    public void DeleteBackup()
    {
        // The browser keeps no earlier version.
    }

    public void SetAsideCorrupt()
    {
        // Settings this server cannot read stay in the browser until a save replaces them, in case their key comes back.
    }

    /// <summary>
    /// Seals the text now, under the person's key (a new one, on their first save or when theirs could not be opened),
    /// and sends it to the browser once any save still on its way has arrived.
    /// </summary>
    public void Write(string text)
    {
        var user = Opened();
        _key ??= _vault.NewKey();
        _sealed = _vault.Seal(user, _key, text);
        _lastWrite = SendAfterAsync(_lastWrite, _sealed);
    }

    /// <summary>Whether the browser kept the latest save, once it has answered.</summary>
    public Task<bool> LastWriteAsync() => _lastWrite;

    private async Task<bool> SendAfterAsync(Task<bool> previous, string sealedText)
    {
        await previous;
        try
        {
            await _js.InvokeVoidAsync(SaveFunction, sealedText);
            return true;
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "The page closed before its browser kept the settings");
            return false;
        }
        catch (JSException ex)
        {
            _logger.LogError(ex, "The browser did not keep the settings");
            WriteFailed?.Invoke(SaveFailedText);
            return false;
        }
    }

    private SignedInUser Opened() => _user ?? throw new IOException("Settings cannot be used before DevKit knows who is signed in.");
}
