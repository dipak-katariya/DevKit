using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace DevKit.Web.Services;

/// <summary>
/// The copies of a person's settings that earlier DevKits kept outside the server's settings folder: in the server's
/// older folder (<see cref="ServerSettingsFolder"/>), or encrypted in their browser's local storage. Both were encrypted
/// with keys only this server has, so a copy that opens is one DevKit made. When the server keeps no settings for the
/// person yet, the newest copy found is read once to move it there (<see cref="PersonSettingsStore"/>), and the copies
/// DevKit can reach are deleted once the server keeps it. A browser keeps its copies per site address, so only those
/// made under the address DevKit is opened at now are found.
/// <para>
/// The copy a later DevKit kept sealed in the browser's cookies is never moved: anyone can seal settings that open as
/// someone else's, and plant them in a browser from any site of the domain, so a cookie cannot show that DevKit made it.
/// The browser is only told to drop those cookies.
/// </para>
/// </summary>
internal sealed class EarlierSettingsCopies
{
    private const string ModulePath = "./earlier-settings.js?v=3";

    // How the browser store before the server's named and encrypted each account's settings.
    private const string PurposePrefix = "DevKit.UserSettings.v1|";
    private const string KeyPrefix = "devkit-settings-";
    private const int KeyHashBytes = 8;

    private readonly ServerSettingsFolder _server;
    private readonly ProtectedLocalStorage _browser;
    private readonly IJSRuntime _js;
    private readonly ILogger<EarlierSettingsCopies> _logger;

    public EarlierSettingsCopies(
        ServerSettingsFolder server,
        ProtectedLocalStorage browser,
        IJSRuntime js,
        ILogger<EarlierSettingsCopies> logger)
    {
        _server = server;
        _browser = browser;
        _js = js;
        _logger = logger;
    }

    /// <summary>The settings text of the newest earlier copy: the older server folder's, then the browser's local storage; null when none can be read.</summary>
    public async Task<string?> ReadAsync(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return ReadServerCopy(user) ?? await ReadBrowserCopyAsync(user);
    }

    /// <summary>Deletes every earlier copy, once the server keeps the settings.</summary>
    public async Task DeleteAsync(SignedInUser user)
    {
        DeleteServerCopy(user);
        await DeleteBrowserCopyAsync(user);
    }

    /// <summary>Deletes the older server folder's copy, with its backup and any damaged copies set aside beside it.</summary>
    public void DeleteServerCopy(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        try
        {
            var deleted = _server.Delete(user);
            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} files of a person's settings from the older server folder, now that the settings folder keeps them", deleted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "A person's settings could not be deleted from the older server folder; DevKit tries again on their next visit");
        }
    }

    /// <summary>Has the browser drop the settings an earlier DevKit kept sealed in its cookies, which are never moved.</summary>
    public async Task ClearCookieCopyAsync()
    {
        try
        {
            var module = await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            await using (module)
            {
                await module.InvokeVoidAsync("clearBrowser");
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "The page closed before its browser's earlier settings copy was cleared");
        }
        catch (JSException ex)
        {
            _logger.LogError(ex, "A browser's earlier settings copy could not be cleared; it stays until the browser deletes it");
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
