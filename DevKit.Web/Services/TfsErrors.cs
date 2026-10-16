using System.Net;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// A successful TFS reply that cannot be taken at face value: not shaped the way the API documents it, or
/// refusing inside a 200 what was asked. The message is written to be shown as it is.
/// </summary>
public sealed class TfsResponseException : Exception
{
    public TfsResponseException(string message) : base(message)
    {
    }
}

/// <summary>
/// What a failed TFS call means for the person looking at the page: what to do about it, never a stack
/// trace or a parser's internals. The full exception still goes to the log.
/// </summary>
public static class TfsErrors
{
    private const int FirstServerError = 500;
    private const string LoggedKey = "DevKit.TfsErrors.Logged";
    private const string SignInKey = "DevKit.TfsErrors.SignIn";

    /// <summary>
    /// Notes on the exception that it has been logged at Error, so a handler further out that catches it
    /// again shows it without logging it twice. Returns the same exception, ready to throw.
    /// </summary>
    public static TException MarkLogged<TException>(TException ex) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(ex);
        ex.Data[LoggedKey] = true;
        return ex;
    }

    /// <summary>Whether a catch further in has already logged this exception at Error.</summary>
    public static bool IsLogged(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.Data[LoggedKey] is true;
    }

    /// <summary>
    /// Notes on a refused request how it signed in, so <see cref="Describe"/> points at the Windows account or at
    /// the token, whichever TFS refused. Returns the same exception, ready to throw.
    /// </summary>
    public static TException MarkSignIn<TException>(TException ex, TfsAuthMode auth) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(ex);
        ex.Data[SignInKey] = auth;
        return ex;
    }

    private static bool SignedInAsWindowsAccount(Exception ex) => ex.Data[SignInKey] is TfsAuthMode.Windows;

    /// <summary>
    /// Whether <paramref name="ex"/> is the cancellation the caller asked for through <paramref name="ct"/>, which
    /// is not a failure: it is let through rather than logged and described. A timeout is a cancellation the caller
    /// did not ask for, and so is any other exception thrown while the token happened to be cancelled.
    /// </summary>
    public static bool IsRequestedCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } when SignedInAsWindowsAccount(ex) =>
            "TFS refused your Windows sign-in (401). Check that your Windows account can open TFS, or sign in with a personal access token in Settings.",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "TFS refused the request (401). Check the personal access token in Settings.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } when SignedInAsWindowsAccount(ex) =>
            "TFS says your Windows account may not read it (403). Check your permissions in TFS.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "TFS says this token may not read it (403). Check the token's permissions.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "TFS could not find it (404). It may have been deleted or renamed.",
        HttpRequestException { StatusCode: { } code } when (int)code >= FirstServerError =>
            $"TFS failed on its side ({(int)code}). Try again in a moment.",
        // Any other status carries TFS's own explanation, which is worth showing as it is.
        HttpRequestException { StatusCode: not null } => ex.Message,
        HttpRequestException => "Couldn't reach TFS. Check the server address and the network.",
        // Callers only describe cancellations they did not ask for, so any left here are timeouts.
        OperationCanceledException or TimeoutException => "TFS took too long to answer. Try again.",
        TfsResponseException => ex.Message,
        JsonException => "TFS sent a reply DevKit couldn't read.",
        _ => "Something unexpected went wrong. The DevKit log has the details."
    };
}
