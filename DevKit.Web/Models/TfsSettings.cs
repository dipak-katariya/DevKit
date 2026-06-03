namespace DevKit.Web.Models;

public class TfsSettings
{
    public string Url { get; set; } = "";
    public string Pat { get; set; } = "";
    public string ApiVersion { get; set; } = "5.0";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Pat);
}
