using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace DevKit.Web.Services;

/// <summary>
/// Moves the settings an earlier DevKit kept in a person's browser to their file on the server, once, then deletes the
/// browser's copy. A browser keeps one store per site address, so only a copy made under the address DevKit is opened
/// at now can be found; one left under an older address stays out of reach.
/// </summary>
internal sealed class BrowserSettingsMigration
{
    // How the earlier browser store named and encrypted each account's settings.
    private const string PurposePrefix = "DevKit.UserSettings.v1|";
    private const string KeyPrefix = "devkit-settings-";
    private const int KeyHashBytes = 8;

    private readonly ProtectedLocalStorage _storage;
    private readonly ILogger<BrowserSettingsMigration> _logger;

    public BrowserSettingsMigration(ProtectedLocalStorage storage, ILogger<BrowserSettingsMigration> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    /// <summary>
    /// Moves this browser's copy of <paramref name="user"/>'s settings to <paramref name="server"/> when the server has
    /// none yet, and deletes the copy once the server has settings. A copy the server cannot take yet stays where it is,
    /// for the next visit.
    /// </summary>
    public async Task MoveToServerAsync(SignedInUser user, ServerSettingsStore server)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(server);
        var key = KeyFor(user);
        if (!server.HasSettings)
        {
            var settled = await ReadAsync(PurposeFor(user), key) switch
            {
                BrowserCopy.Found found => TryKeep(found.Text, server),
                BrowserCopy.Nothing => true,
                _ => false
            };
            if (!settled)
            {
                return;
            }
        }

        await DeleteAsync(key);
    }

    internal static string PurposeFor(SignedInUser user) => PurposePrefix + user.Id;

    internal static string KeyFor(SignedInUser user) =>
        KeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.Id)), 0, KeyHashBytes);

    /// <summary>What the browser holds: a copy, nothing worth keeping, or no answer to go on (the page stays as it is).</summary>
    private async Task<BrowserCopy> ReadAsync(string purpose, string key)
    {
        try
        {
            var stored = await _storage.GetAsync<string>(purpose, key);
            return stored is { Success: true, Value: { } text } ? new BrowserCopy.Found(text) : new BrowserCopy.Nothing();
        }
        catch (CryptographicException ex)
        {
            // Encrypted with keys the server no longer has: it can never be read, so it is deleted.
            _logger.LogError(ex, "The settings an earlier DevKit kept in a browser could not be decrypted; that copy is deleted");
            return new BrowserCopy.Nothing();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "The page closed before its browser was asked for settings an earlier DevKit kept there");
            return new BrowserCopy.Unavailable();
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "The browser could not be asked for settings an earlier DevKit kept there; they stay for the next visit");
            return new BrowserCopy.Unavailable();
        }
    }

    private bool TryKeep(string text, ServerSettingsStore server)
    {
        try
        {
            server.Write(text);
            _logger.LogInformation("Settings an earlier DevKit kept in a browser were moved to the server");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError(ex, "Settings kept in a browser could not be moved to the server; the browser keeps them for the next visit");
            return false;
        }
    }

    private async Task DeleteAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key);
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

    private abstract record BrowserCopy
    {
        public sealed record Found(string Text) : BrowserCopy;

        public sealed record Nothing : BrowserCopy;

        public sealed record Unavailable : BrowserCopy;
    }
}
