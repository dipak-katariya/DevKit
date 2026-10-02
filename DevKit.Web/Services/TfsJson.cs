using System.Text.Json;

namespace DevKit.Web.Services;

/// <summary>Null-safe readers for the TFS REST payload shapes.</summary>
public static class TfsJson
{
    public static string Str(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static int Int(JsonElement el, string prop, int fallback = 0) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    public static DateTime? Date(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(v.GetString(), out var d) ? d : null;

    /// <summary>Display name from an identity field, which may be an object or a "Name &lt;email&gt;" string.</summary>
    public static string Person(JsonElement el, string prop, string fallback = "-")
    {
        if (!el.TryGetProperty(prop, out var p)) return fallback;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("displayName", out var dn))
            return dn.GetString() ?? fallback;
        if (p.ValueKind != JsonValueKind.String) return fallback;

        var s = p.GetString() ?? fallback;
        return s.Contains('<') ? s.Split('<')[0].Trim() : s;
    }

    /// <summary>Items of a TFS list response, or empty when the payload has no "value" array.</summary>
    public static IEnumerable<JsonElement> Values(JsonElement el) =>
        el.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    /// <summary>A nested commit identity: { name, email, date }.</summary>
    public static (string Name, DateTime? Date) CommitPerson(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
            ? (Str(p, "name"), Date(p, "date"))
            : ("", null);

    /// <summary>The email of a nested commit identity, or empty when there is none.</summary>
    public static string CommitEmail(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "email") : "";
}
