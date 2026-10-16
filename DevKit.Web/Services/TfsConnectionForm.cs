using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// What the Settings connection form asks TFS with: the server URL as typed, once it is a valid http(s)
/// address, and how to sign in. Windows sign-in needs nothing more. A PAT sign-in uses the PAT typed in, or the
/// stored one when the field is left blank; the stored PAT is encrypted for its own server, so it is only offered
/// for that server.
/// </summary>
public static class TfsConnectionForm
{
    /// <summary>The shape of a collection address, shown where one is asked for.</summary>
    public const string ExampleHint = "e.g. https://tfs.example.com/tfs/Collection";

    /// <summary>A server and sign-in to try, or the reason there is none.</summary>
    public readonly record struct Candidate(string Url, string Pat, string Error, TfsAuthMode Auth = TfsAuthMode.Pat)
    {
        public bool IsValid => Error.Length == 0;

        public static Candidate Refused(string url, string error) => new(url, "", error);
    }

    public static Candidate Choose(string? urlInput, string? typedPat, SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryValidateUrl(urlInput, out var url, out var error))
        {
            return Candidate.Refused(url, error);
        }

        var pat = (typedPat ?? "").Trim();
        if (pat.Length > 0)
        {
            return new Candidate(url, pat, "");
        }

        if (!settings.HasStoredPat)
        {
            return Candidate.Refused(url, "Personal Access Token is required.");
        }

        return settings.IsStoredServer(url)
            ? new Candidate(url, settings.Tfs.Pat, "")
            : Candidate.Refused(url, "Enter the Personal Access Token again: the stored one belongs to a different server URL.");
    }

    /// <summary>The server to try with the Windows account DevKit runs as. No token is involved.</summary>
    public static Candidate ChooseWindowsSignIn(string? urlInput) =>
        TryValidateUrl(urlInput, out var url, out var error)
            ? new Candidate(url, "", "", TfsAuthMode.Windows)
            : Candidate.Refused(url, error);

    public static bool TryValidateUrl(string? raw, out string normalized, out string error)
    {
        normalized = (raw ?? "").Trim();
        error = "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "Server URL is required.";
            return false;
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"Enter a valid http(s) URL, {ExampleHint}.";
            return false;
        }
        return true;
    }

    // Over plain http to a remote host a PAT travels as readable HTTP Basic auth. A Windows sign-in sends no
    // password, but its exchange can be relayed, and everything read from TFS is readable too.
    public static string InsecureUrlWarning(string url, TfsAuthMode auth = TfsAuthMode.Pat)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || uri.IsLoopback)
        {
            return "";
        }

        return auth == TfsAuthMode.Windows
            ? "  ⚠ http detected: your Windows sign-in and TFS data travel unencrypted; prefer https."
            : "  ⚠ http detected: your PAT is sent unencrypted; prefer https.";
    }
}
