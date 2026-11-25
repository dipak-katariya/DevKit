using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DevKit.Web.Components.Pages;

/// <summary>
/// The TFS connection form: checked with TFS and saved only when TFS accepts it. A saved PAT shows as a masked field;
/// it reaches the browser only when its owner clicks Show or Copy, over https or on the DevKit PC, and is hidden again
/// after <see cref="PatShownFor"/>.
/// </summary>
public sealed partial class Settings
{
    // A fixed length, so the mask says nothing about the token's.
    private const string StoredPatMask = "••••••••••••••••••••";

    private const string RevealRefused =
        "✕ The saved token is shown and copied only over https, or in a browser on the PC DevKit runs on.";

    private static readonly TimeSpan PatShownFor = TimeSpan.FromSeconds(30);

    private string tfsUrl = "";
    private TfsAuthMode tfsAuth;

    // What the connection form started from, to tell an edit from what is saved.
    private string savedUrl = "";
    private TfsAuthMode savedAuth;

    // A token the person typed; empty keeps the saved one. The saved token is never in it.
    private string tfsPat = "";
    private bool patEdited;
    private bool hasStoredPat;

    // The saved token while its owner has it shown, and the timer that hides it again.
    private string? revealedPat;
    private bool patVisible;
    private CancellationTokenSource? patHide;

    private bool saving, testing;
    private string statusMsg = "";
    private bool statusOk;

    /// <summary>What the field shows: the person's own typing, or the saved token while it is shown; otherwise empty, under the mask.</summary>
    private string PatFieldValue => patEdited ? tfsPat : revealedPat ?? "";

    /// <summary>
    /// Whether the form holds a connection other than the one saved: another server, another way to sign in, or a new
    /// token. A token left blank, or typed the same as the saved one, keeps the saved one.
    /// </summary>
    private bool ConnectionChanged =>
        !string.Equals(tfsUrl.Trim(), savedUrl.Trim(), StringComparison.Ordinal)
        || (SettingsSvc.IsConfigured && tfsAuth != savedAuth)
        || (tfsAuth == TfsAuthMode.Pat && tfsPat.Trim().Length > 0 && !SettingsSvc.IsStoredPat(tfsPat));

    /// <summary>Fills the connection form from Settings, at start and after a save.</summary>
    private void ReadConnection()
    {
        savedUrl = tfsUrl = SettingsSvc.FixedTfsUrl ?? SettingsSvc.Tfs.Url;
        savedAuth = tfsAuth = SettingsSvc.IsConfigured ? SettingsSvc.Tfs.Auth : FirstSignIn;
        hasStoredPat = SettingsSvc.HasStoredPat;
        tfsPat = "";
        patEdited = false;
        HidePat();
    }

    /// <summary>
    /// Takes a connection saved elsewhere into the form, unless the form has a connection being typed: a token shown
    /// here that is no longer the saved one is hidden too.
    /// </summary>
    private void FollowSavedConnection()
    {
        // The same rule as Save uses, so a form that reads "saved" always follows a connection saved elsewhere.
        var formEdited = ConnectionChanged;
        var savedElsewhere = !string.Equals(SettingsSvc.FixedTfsUrl ?? SettingsSvc.Tfs.Url, savedUrl, StringComparison.Ordinal)
            || hasStoredPat != SettingsSvc.HasStoredPat
            || (SettingsSvc.IsConfigured && SettingsSvc.Tfs.Auth != savedAuth)
            || (revealedPat is not null && !SettingsSvc.IsStoredPat(revealedPat));
        if (savedElsewhere && !formEdited)
        {
            ReadConnection();
        }
    }

    /// <summary>A connection not set up yet starts on Windows sign-in where it is allowed, since it needs no token.</summary>
    private TfsAuthMode FirstSignIn => SettingsSvc.AllowsWindowsSignIn ? TfsAuthMode.Windows : TfsAuthMode.Pat;

    /// <summary>The Windows account DevKit runs as, which is the one Windows sign-in presents to TFS.</summary>
    private static string WindowsAccount => $@"{Environment.UserDomainName}\{Environment.UserName}";

    /// <summary>Switches how the form signs in. A token typed for the other sign-in is not kept.</summary>
    private void SetSignIn(TfsAuthMode auth)
    {
        tfsAuth = auth;
        tfsPat = "";
        patEdited = false;
        HidePat();
        ShowStatus("", ok: false);
    }

    private void OnPatInput(ChangeEventArgs e)
    {
        tfsPat = e.Value?.ToString() ?? "";
        patEdited = true;
        // What is in the field is now the person's own typing; the saved token is not kept on the page for it.
        revealedPat = null;
    }

    /// <summary>What the form asks TFS with, for the sign-in chosen above it.</summary>
    private TfsConnectionForm.Candidate FormCandidate() => tfsAuth == TfsAuthMode.Windows
        ? TfsConnectionForm.ChooseWindowsSignIn(tfsUrl)
        : TfsConnectionForm.Choose(tfsUrl, tfsPat, SettingsSvc);

    /// <summary>Names the account TFS ran the check as, when TFS said, so the status shows who DevKit signed in as.</summary>
    private static string SignedInAs(TfsConnectionCheck check) =>
        check.Account.Length == 0 ? "" : $" as {check.Account}";

    /// <summary>What Save &amp; Connect came to.</summary>
    private enum ConnectionSave
    {
        Saved,

        /// <summary>The form was not filled in right, or TFS refused or could not be reached.</summary>
        Refused,

        /// <summary>TFS accepted it, but it could not be kept.</summary>
        NotKept,

        /// <summary>The page was left before TFS answered.</summary>
        Stopped
    }

    /// <summary>
    /// Checks the server and sign-in first and saves them only when TFS accepts them, so a mistyped token never
    /// replaces a working one. The status says what TFS answered, or why the settings were not saved.
    /// </summary>
    private async Task<ConnectionSave> SaveConnectionAsync()
    {
        var (url, pat, error, auth) = FormCandidate();
        if (error.Length > 0)
        {
            ShowStatus($"✕ {error}", ok: false);
            return ConnectionSave.Refused;
        }

        saving = true;
        ShowStatus("", ok: false);
        StateHasChanged();
        try
        {
            var outcome = await CheckAndSaveConnectionAsync(url, pat, auth);
            if (outcome.Check is { } check)
            {
                await ReloadListsAsync(check, url, auth);
            }

            return outcome.Result;
        }
        finally
        {
            saving = false;
            FollowMissedSave();
        }
    }

    /// <summary>Asks TFS, then saves; nothing is saved until TFS has answered, so leaving before then saves nothing.</summary>
    private async Task<(ConnectionSave Result, TfsConnectionCheck? Check)> CheckAndSaveConnectionAsync(string url, string pat, TfsAuthMode auth)
    {
        try
        {
            var check = await TfsApi.TestConnectionAsync(url, auth, pat, page.Token);
            if (!SettingsSvc.SaveTfs(url, pat, auth))
            {
                ShowStatus($"✕ TFS accepted the sign-in, but it was not saved. {SettingsSvc.LastSaveError}", ok: false);
                return (ConnectionSave.NotKept, null);
            }

            Logger.LogInformation("TFS connection saved; DevKit signs in with {SignIn}", auth);
            ReadConnection();
            return (ConnectionSave.Saved, check);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Save & Connect stopped because the page was left before TFS answered; nothing was saved");
            return (ConnectionSave.Stopped, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The TFS connection check with {SignIn} sign-in failed, so the connection was not saved", auth);
            ShowStatus($"✕ Not saved: {TfsErrors.Describe(ex)}", ok: false);
            return (ConnectionSave.Refused, null);
        }
    }

    /// <summary>
    /// The lists belonged to the server just replaced, so every page of the circuit now reads the new one's, Update
    /// status's tags and fields too. A list that fails to load is shown in the notice: the connection is saved either way.
    /// </summary>
    private async Task ReloadListsAsync(TfsConnectionCheck check, string url, TfsAuthMode auth)
    {
        var saved = $"✓ Saved and connected{SignedInAs(check)}. Found {check.ProjectCount} project(s)";
        PageState.TfsData.Invalidate();
        statusListsRead = false;
        statusProject = "";
        listsLoading = true;
        StateHasChanged();
        try
        {
            var load = await PageState.TfsData.EnsureLoadedAsync(page.Token);
            if (load.Cancelled)
            {
                return;
            }

            metadataError = load.ErrorMessage;
            ShowStatus($"{saved} and {repos.Count} repositories.{TfsConnectionForm.InsecureUrlWarning(url, auth)}", ok: true);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The page was left while the lists of the new server were loading");
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The connection was saved, but the lists of the new server could not be loaded");
            ShowStatus($"{saved}. The lists could not be loaded: {TfsErrors.Describe(ex)}", ok: true);
            return;
        }
        finally
        {
            listsLoading = false;
        }

        await EnsureStatusListsAsync();
    }

    /// <summary>Tries the server and sign-in in the form without saving anything.</summary>
    private async Task TestConnection()
    {
        var (url, pat, error, auth) = FormCandidate();
        if (error.Length > 0)
        {
            ShowStatus($"✕ {error}", ok: false);
            return;
        }

        testing = true;
        ShowStatus("", ok: false);
        StateHasChanged();
        try
        {
            var check = await TfsApi.TestConnectionAsync(url, auth, pat, page.Token);
            if (SettingsSvc.IsStoredServer(url))
            {
                // A refusal noted for the saved sign-in is over once TFS takes that same sign-in again.
                SettingsSvc.NoteSignInAccepted(auth, pat);
            }

            ShowStatus($"✓ Connected{SignedInAs(check)}. Found {check.ProjectCount} project(s). Nothing was saved.{TfsConnectionForm.InsecureUrlWarning(url, auth)}", ok: true);
        }
        catch (OperationCanceledException ex) when (page.Token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The connection test stopped because the page was left");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The TFS connection test with {SignIn} sign-in failed", auth);
            ShowStatus($"✕ Connection failed: {TfsErrors.Describe(ex)}", ok: false);
        }
        finally
        {
            testing = false;
        }
    }

    /// <summary>
    /// The saved token stays on the server unless the page itself is private: over https, or in a browser on the PC
    /// DevKit runs on. Over plain http to another PC, anyone on the network could read it on the way.
    /// </summary>
    private bool MayRevealPat => !TfsConnectionForm.IsPlainHttpToAnotherMachine(Nav.BaseUri);

    /// <summary>Shows or hides the token. The person's own typing only changes how it shows; the saved one comes from the server.</summary>
    private void TogglePat()
    {
        if (patVisible)
        {
            HidePat();
            return;
        }

        if (!patEdited)
        {
            if (!MayRevealPat)
            {
                ShowStatus(RevealRefused, ok: false);
                return;
            }

            var stored = SettingsSvc.RevealStoredPat(PatReveal.Shown);
            if (stored.Length == 0)
            {
                NoTokenStored();
                return;
            }

            revealedPat = stored;
        }

        patVisible = true;
        HidePatLater();
    }

    /// <summary>Puts the saved token on the clipboard, without showing it.</summary>
    private async Task CopyPatAsync()
    {
        if (!MayRevealPat)
        {
            ShowStatus(RevealRefused, ok: false);
            return;
        }

        var stored = SettingsSvc.RevealStoredPat(PatReveal.Copied);
        if (stored.Length == 0)
        {
            NoTokenStored();
            return;
        }

        try
        {
            if (await JS.InvokeAsync<bool>("devKit.copyToClipboard", stored))
            {
                ShowStatus("✓ Token copied. It stays on your clipboard until you copy something else.", ok: true);
            }
            else
            {
                Logger.LogWarning("The browser did not allow copying the saved token");
                ShowStatus("✕ The browser did not allow copying. Click Show and copy the token from the field.", ok: false);
            }
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The page closed before the saved token was copied");
        }
        catch (JSException ex)
        {
            Logger.LogError(ex, "The browser could not copy the saved token");
            ShowStatus("✕ The browser could not copy the token. Click Show and copy it from the field.", ok: false);
        }
    }

    /// <summary>The token was replaced elsewhere (Windows sign-in, say) before this page redrew: it follows what is saved.</summary>
    private void NoTokenStored()
    {
        ReadConnection();
        ShowStatus("✕ No token is saved any more.", ok: false);
    }

    private void HidePat()
    {
        CancellationSources.CancelAndDispose(ref patHide);
        patVisible = false;
        revealedPat = null;
    }

    /// <summary>Hides the token again after <see cref="PatShownFor"/>, so it is not left on screen.</summary>
    private void HidePatLater()
    {
        CancellationSources.CancelAndDispose(ref patHide);
        patHide = page.CreateLinked();
        _ = HidePatAfterAsync(patHide.Token);
    }

    private async Task HidePatAfterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(PatShownFor, token);
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "The token was hidden, or the page left, before it hid by itself");
            return;
        }

        await InvokeAsync(() =>
        {
            // Hide then Show while this waited its turn started a new timer; this one is over.
            if (token.IsCancellationRequested)
            {
                return;
            }

            HidePat();
            StateHasChanged();
        });
    }

    private void ShowStatus(string message, bool ok)
    {
        statusMsg = message;
        statusOk = ok;
    }
}
