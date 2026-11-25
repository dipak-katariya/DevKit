using System.Security.Cryptography;
using System.Text;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>Why the stored PAT left the server: shown in the Settings field, or copied to the clipboard.</summary>
public enum PatReveal
{
    Shown,
    Copied
}

/// <summary>
/// The TFS connection: the server, how DevKit signs in to it, and the PAT, which is held in memory as typed (the API
/// client needs it) and kept encrypted at rest for its own server.
/// </summary>
public partial class SettingsService
{
    private const string PatContextPrefix = "devkit-pat|";

    private const string WindowsSignInRefusedMessage =
        "Windows sign-in to TFS is only for the Windows account DevKit runs as. Use a personal access token.";

    /// <summary>
    /// The saved sign-in TFS last refused (401): how it signed in, and a fingerprint of its token, so no second copy of
    /// the token stays in memory once it is replaced.
    /// </summary>
    private sealed record RefusedSignIn(TfsAuthMode Auth, byte[] PatFingerprint);

    // Replaced whole, never changed, so a read outside the lock sees one sign-in or the other.
    private RefusedSignIn? _refusedSignIn;

    /// <summary>True when a PAT is stored, without handing it out.</summary>
    public bool HasStoredPat => !string.IsNullOrEmpty(_settings.Tfs.Pat);

    /// <summary>
    /// Whether TFS refused a request made with the saved sign-in (401), and since then no request made with it worked
    /// and nothing was saved in its place. The token may have expired or been revoked, or lack a permission one tool
    /// needs while others still work.
    /// </summary>
    public bool SignInRefused => _refusedSignIn is { } refused
        && refused.Auth == _settings.Tfs.Auth
        && CryptographicOperations.FixedTimeEquals(refused.PatFingerprint, Fingerprint(_settings.Tfs.Pat));

    /// <summary>What to do about a refused sign-in, for the top bar and Settings.</summary>
    public string SignInRefusedReason => _settings.Tfs.Auth == TfsAuthMode.Windows
        ? "TFS refused a request made as the Windows account DevKit runs as. Check that account's access in TFS, or use a personal access token."
        : "TFS refused a request made with your saved token. It may have expired or been revoked, or lack a permission a tool needs " +
          "(creating tasks needs Work Items: Read & write). Make a new token in TFS and save it in Settings.";

    /// <summary>
    /// TFS refused a request sent with <paramref name="auth"/> and <paramref name="pat"/>, the sign-in saved when it was
    /// sent. The top bar and Settings say so until a request with it works or it is replaced. A refusal of a sign-in
    /// replaced while the request was on its way is ignored: it says nothing about the one saved now.
    /// </summary>
    internal void NoteSignInRefused(TfsAuthMode auth, string pat)
    {
        lock (_sync)
        {
            if (SignInRefused || !IsSavedSignIn(auth, pat))
            {
                return;
            }

            _refusedSignIn = new RefusedSignIn(auth, Fingerprint(pat));
        }

        _logger.LogWarning("TFS refused a request made with the saved {SignIn} sign-in for {Person}", auth, Person ?? "the local user");
        Changed?.Invoke();
    }

    /// <summary>
    /// A request with <paramref name="auth"/> and <paramref name="pat"/> worked, or was checked with TFS: when that is the
    /// saved sign-in marked as refused, the mark goes, since TFS takes it again.
    /// </summary>
    internal void NoteSignInAccepted(TfsAuthMode auth, string pat)
    {
        if (_refusedSignIn is null)
        {
            return;
        }

        lock (_sync)
        {
            if (!SignInRefused || !IsSavedSignIn(auth, pat))
            {
                return;
            }

            _refusedSignIn = null;
        }

        _logger.LogInformation("TFS took the saved {SignIn} sign-in again for {Person}", auth, Person ?? "the local user");
        Changed?.Invoke();
    }

    private bool IsSavedSignIn(TfsAuthMode auth, string? pat) =>
        auth == _settings.Tfs.Auth
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pat ?? ""), Encoding.UTF8.GetBytes(_settings.Tfs.Pat ?? ""));

    // Only to tell one token from another; never stored where the token could be read back from it.
    private static byte[] Fingerprint(string? pat) => SHA256.HashData(Encoding.UTF8.GetBytes(pat ?? ""));

    /// <summary>Whether <paramref name="pat"/> is the stored PAT, compared in constant time so the answer's timing says nothing about it.</summary>
    public bool IsStoredPat(string? pat)
    {
        var typed = Encoding.UTF8.GetBytes((pat ?? "").Trim());
        var stored = Encoding.UTF8.GetBytes(_settings.Tfs.Pat ?? "");
        return HasStoredPat && CryptographicOperations.FixedTimeEquals(typed, stored);
    }

    /// <summary>
    /// The stored PAT, for its owner who asked on the Settings page to see or copy it; empty when none is stored. It is
    /// kept encrypted at rest and only ever leaves the server on this request. Each use is logged, never the token.
    /// </summary>
    public string RevealStoredPat(PatReveal use)
    {
        lock (_sync)
        {
            if (!HasStoredPat)
            {
                return "";
            }

            _logger.LogInformation("The stored PAT was {Use} on the Settings page for {Person}", use, Person ?? "the local user");
            return _settings.Tfs.Pat;
        }
    }

    /// <summary>
    /// True when <paramref name="url"/> is the server the stored PAT belongs to, however it was typed: the
    /// same comparison that decides whether the stored PAT decrypts.
    /// </summary>
    public bool IsStoredServer(string url) => SameServer(url, _settings.Tfs.Url);

    /// <summary>
    /// Sets the server, how DevKit signs in to it, and the PAT together, since the PAT is encrypted for that
    /// server. Windows sign-in keeps no PAT: one passed with it is ignored, and one stored before is dropped,
    /// from the backup too. In shared mode the server is always <see cref="FixedTfsUrl"/>, and Windows sign-in is
    /// refused unless <see cref="AllowsWindowsSignIn"/>. Clears <see cref="SignInRefused"/>: the caller checked the
    /// sign-in with TFS before saving it. Returns false when the save failed or was refused;
    /// <see cref="LastSaveError"/> says why.
    /// </summary>
    public bool SaveTfs(string url, string pat, TfsAuthMode auth = TfsAuthMode.Pat)
    {
        lock (_sync)
        {
            if (auth == TfsAuthMode.Windows && !AllowsWindowsSignIn)
            {
                LastSaveError = WindowsSignInRefusedMessage;
                Changed?.Invoke();
                return false;
            }

            var dropsStoredPat = auth == TfsAuthMode.Windows && (HasStoredPat || _undecryptablePat is not null);
            _settings.Tfs.Url = FixedTfsUrl ?? url;
            _settings.Tfs.Auth = auth;
            _settings.Tfs.Pat = auth == TfsAuthMode.Windows ? "" : pat;
            _undecryptablePat = null;
            // Save & Connect checked this sign-in with TFS first, so a refusal noted before is over, also for the same token.
            _refusedSignIn = null;
            return dropsStoredPat ? SaveDroppingOldPat() : Save();
        }
    }

    /// <summary>
    /// Saves a change that replaces the stored PAT: one read in an older form (plaintext, or encrypted without its
    /// server) saved in the current one, or one Windows sign-in no longer keeps. The backup that save keeps is the
    /// old file, PAT and all, so it is deleted rather than left behind. Returns false when the save failed.
    /// </summary>
    private bool SaveDroppingOldPat()
    {
        if (!Save())
        {
            return false;
        }

        try
        {
            _store.DeleteBackup();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The settings backup still holding the replaced PAT could not be deleted");
        }

        return true;
    }

    private string DecryptPat(string stored, string? url)
    {
        _undecryptablePat = null;
        try
        {
            return SecretProtector.Unprotect(stored, PatContext(url));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _logger.LogError(ex,
                "The stored PAT could not be decrypted: it was saved by another Windows account or machine, or for another server. " +
                "It is kept unchanged; enter the PAT again in Settings to replace it.");
            _undecryptablePat = stored;
            return "";
        }
    }

    /// <summary>What the PAT is encrypted for: the server, written the same way however it was typed.</summary>
    private static string PatContext(string? url) =>
        PatContextPrefix + (url ?? "").Trim().TrimEnd('/').ToUpperInvariant();

    private static bool SameServer(string? url, string? other) =>
        string.Equals(PatContext(url), PatContext(other), StringComparison.Ordinal);

    private string PersistedPat(TfsSettings tfs) =>
        string.IsNullOrEmpty(tfs.Pat) && _undecryptablePat is not null
            ? _undecryptablePat
            : _store.Pat.Protect(tfs.Pat, PatContext(tfs.Url));
}
