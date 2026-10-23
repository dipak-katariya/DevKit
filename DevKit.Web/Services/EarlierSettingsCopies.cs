using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace DevKit.Web.Services;

/// <summary>
/// The copies of a person's settings that earlier DevKits kept: their file on this server (<see cref="ServerSettingsFolder"/>),
/// or, before that, this browser's local storage, encrypted with this server's keys. When the person's browser keeps no
/// settings yet, the newest copy is read once to move it there (<see cref="ClientSettingsStore"/>), and every copy is
/// deleted once the browser has kept it, so the server keeps nothing of theirs. A browser keeps one local storage per
/// site address, so only a copy made under the address DevKit is opened at now can be found.
/// </summary>
internal sealed class EarlierSettingsCopies
{
    // How the browser store before the server's named and encrypted each account's settings.
    private const string PurposePrefix = "DevKit.UserSettings.v1|";
    private const string KeyPrefix = "devkit-settings-";
    private const int KeyHashBytes = 8;

    private readonly ServerSettingsFolder _server;
    private readonly ProtectedLocalStorage _browser;
    private readonly ILogger<EarlierSettingsCopies> _logger;

    public EarlierSettingsCopies(ServerSettingsFolder server, ProtectedLocalStorage browser, ILogger<EarlierSettingsCopies> logger)
    {
        _server = server;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>The settings text of the newest earlier copy, the server's before the browser's; null when none can be read.</summary>
    public async Task<string?> ReadAsync(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return ReadServerCopy(user) ?? await ReadBrowserCopyAsync(user);
    }

    /// <summary>Deletes every earlier copy of <paramref name="user"/>'s settings, once their browser keeps them.</summary>
    public async Task DeleteAsync(SignedInUser user)
    {
        DeleteServerCopy(user);
        await DeleteBrowserCopyAsync(user);
    }

    /// <summary>Deletes the server's copy, with its backup and any damaged copies set aside beside it.</summary>
    public void DeleteServerCopy(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        try
        {
            var deleted = _server.Delete(user);
            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} files of a person's settings from the server, now that their browser keeps them", deleted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "A person's settings could not be deleted from the server; DevKit tries again on their next visit");
        }
    }

    internal static string PurposeFor(SignedInUser user) => PurposePrefix + user.Id;

    internal static string KeyFor(SignedInUser user) => KeyPrefix + Convert.ToHexString(user.IdHash(), 0, KeyHashBytes);

    private string? ReadServerCopy(SignedInUser user)
    {
        try
        {
            return _server.ReadText(user);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "The settings an earlier DevKit kept on the server for a person do not decrypt with this server's keys, so they cannot be moved");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The settings an earlier DevKit kept on the server for a person could not be read; DevKit tries again on their next visit");
            return null;
        }
    }

    private async Task<string?> ReadBrowserCopyAsync(SignedInUser user)
    {
        try
        {
            var stored = await _browser.GetAsync<string>(PurposeFor(user), KeyFor(user));
            return stored is { Success: true, Value: { } text } ? text : null;
        }
        catch (CryptographicException ex)
        {
            // Encrypted with keys the server no longer has: it can never be read, so it is deleted.
            _logger.LogError(ex, "The settings an earlier DevKit kept in a browser could not be decrypted; that copy is deleted");
            await DeleteBrowserCopyAsync(user);
            return null;
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "The page closed before its browser was asked for settings an earlier DevKit kept there");
            return null;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "The browser could not be asked for settings an earlier DevKit kept there; they stay for the next visit");
            return null;
        }
    }

    private async Task DeleteBrowserCopyAsync(SignedInUser user)
    {
        try
        {
            await _browser.DeleteAsync(KeyFor(user));
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "The page closed before an earlier DevKit's settings copy was deleted from its browser");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            _logger.LogError(ex, "An earlier DevKit's settings copy could not be deleted from a browser");
        }
    }
}
