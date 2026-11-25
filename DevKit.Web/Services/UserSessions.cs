using System.Runtime.Versioning;
using Microsoft.AspNetCore.Components.Authorization;

namespace DevKit.Web.Services;

/// <summary>
/// The person behind one open page, and whether their settings are ready for the pages to use. The layout shows no
/// page until <see cref="StartAsync"/> has made them ready.
/// </summary>
public abstract class UserSession
{
    /// <summary>Whether people sign in (shared mode) rather than the starter using DevKit directly (local mode).</summary>
    public abstract bool IsShared { get; }

    /// <summary>Whether the settings are loaded for this page.</summary>
    public bool IsReady { get; protected set; }

    /// <summary>Who is signed in; null in local mode, and in shared mode before <see cref="StartAsync"/>.</summary>
    public SignedInUser? User { get; protected set; }

    /// <summary>Makes the settings ready. False when nobody is signed in any more, and the page must sign in again.</summary>
    public abstract Task<bool> StartAsync();
}

/// <summary>Local mode: the settings file was read at startup, and the person is whoever started DevKit.</summary>
internal sealed class LocalUserSession : UserSession
{
    public LocalUserSession()
    {
        IsReady = true;
    }

    public override bool IsShared => false;

    public override Task<bool> StartAsync() => Task.FromResult(true);
}

/// <summary>
/// Shared mode: reads who is signed in to this page, then loads their own settings from the server's settings folder
/// (<see cref="PersonSettingsStore"/>). On their first visit since the folder came in, the newest copy an earlier DevKit
/// kept (in their browser's local storage, or in an older server folder) moves into it, and is deleted once it is there.
/// While the page is open, a save on another of the person's pages is read here too (<see cref="PersonSettingsSaves"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SharedUserSession : UserSession, IDisposable
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly PersonSettingsStore _store;
    private readonly EarlierSettingsCopies _earlier;
    private readonly SettingsService _settings;
    private readonly OwnerAccount _owner;
    private readonly PersonSettingsSaves _saves;
    private readonly ILogger<SharedUserSession> _logger;

    // Dispose can run while StartAsync waits on the browser; the subscription is only made while the page is open.
    private readonly object _subscription = new();
    private volatile bool _disposed;

    public SharedUserSession(
        AuthenticationStateProvider authentication,
        PersonSettingsStore store,
        EarlierSettingsCopies earlier,
        SettingsService settings,
        OwnerAccount owner,
        PersonSettingsSaves saves,
        ILogger<SharedUserSession> logger)
    {
        _authentication = authentication;
        _store = store;
        _earlier = earlier;
        _settings = settings;
        _owner = owner;
        _saves = saves;
        _logger = logger;
    }

    /// <summary>The page has closed: it stops hearing about the person's saves.</summary>
    public void Dispose()
    {
        lock (_subscription)
        {
            _disposed = true;
            _saves.Saved -= OnSavedElsewhere;
        }
    }

    // Raised on the saving page's thread, maybe while it holds its settings: this page reads them on its own thread,
    // so two pages saving at once never wait on each other.
    private void OnSavedElsewhere(string person, Guid savedBy)
    {
        if (savedBy != _store.InstanceId && string.Equals(person, _store.Person, StringComparison.Ordinal))
        {
            _ = Task.Run(ReloadAfterSaveElsewhere);
        }
    }

    /// <summary>Reads what another page saved. Nobody waits on it, so every failure is logged here.</summary>
    private void ReloadAfterSaveElsewhere()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _settings.ReloadFromStore();
        }
        catch (ObjectDisposedException ex) when (_disposed)
        {
            _logger.LogDebug(ex, "The page closed while it read the settings another page saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The settings another page of {Person} saved could not be read on this page", _store.Person);
        }
    }

    public override bool IsShared => true;

    public override async Task<bool> StartAsync()
    {
        if (IsReady)
        {
            return true;
        }

        User = SignedInUser.From((await _authentication.GetAuthenticationStateAsync()).User, _owner.Id);
        if (User is null)
        {
            return false;
        }

        _store.Open(User);
        if (_store.HasSettings)
        {
            _settings.LoadFromStore(User.IsOwner);
            // An older server folder's copy left by a move whose delete failed; one is kept while these do not open.
            if (_settings.LoadProblem is null)
            {
                _earlier.DeleteServerCopy(User);
            }
        }
        else
        {
            await MoveEarlierCopyAsync(User);
        }

        lock (_subscription)
        {
            // Closed while the earlier copy moved: the page is gone, and hearing saves would keep it alive for nothing.
            if (_disposed)
            {
                return false;
            }

            _saves.Saved += OnSavedElsewhere;
        }

        IsReady = true;
        return true;
    }

    /// <summary>
    /// Keeps the newest earlier copy in the settings folder, and deletes the copies once it is there. The browser drops
    /// the copy its cookies may keep, which is never moved.
    /// </summary>
    private async Task MoveEarlierCopyAsync(SignedInUser user)
    {
        var earlier = await _earlier.ReadAsync(user);
        _settings.LoadFromStore(user.IsOwner, earlier);
        if (earlier is not null && _settings.LastSaveError is null)
        {
            await _earlier.DeleteAsync(user);
        }

        await _earlier.ClearCookieCopyAsync();
    }
}
