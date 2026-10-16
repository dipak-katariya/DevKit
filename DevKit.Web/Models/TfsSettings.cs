using System.Text.Json.Serialization;

namespace DevKit.Web.Models;

/// <summary>How DevKit signs in to TFS. Stored by name, so the settings file reads as what was chosen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TfsAuthMode>))]
public enum TfsAuthMode
{
    /// <summary>A personal access token, sent as HTTP Basic auth. A file without the setting means this.</summary>
    Pat,

    /// <summary>The Windows account DevKit runs as, through TFS's own Negotiate/NTLM sign-in. No token is stored.</summary>
    Windows
}

public class TfsSettings
{
    public string Url { get; set; } = "";
    public TfsAuthMode Auth { get; set; } = TfsAuthMode.Pat;
    public string Pat { get; set; } = "";
    public string ApiVersion { get; set; } = "5.0";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) && (Auth == TfsAuthMode.Windows || !string.IsNullOrWhiteSpace(Pat));
}
