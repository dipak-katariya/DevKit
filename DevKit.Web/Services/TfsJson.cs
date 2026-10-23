using System.Globalization;
using System.Text.Json;

namespace DevKit.Web.Services;

/// <summary>Null-safe readers for the TFS REST payload shapes.</summary>
public static class TfsJson
{
    public static string Str(JsonElement el, string prop, string fallback = "") =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static int Int(JsonElement el, string prop, int fallback = 0) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    // A plain DateTime.TryParse turns a "...Z" value into the server's local time, so what it read depended
    // on the zone the server runs in. These keep it in UTC on every server.
    private const DateTimeStyles UtcStyles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

    /// <summary>
    /// A moment TFS recorded, such as a commit or a pull request being raised, in UTC. A value without an
    /// offset is taken as UTC, as TFS writes them. Convert it with ToLocalTime only to show it.
    /// </summary>
    public static DateTime? Timestamp(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, UtcStyles, out var d) ? d : null;

    /// <summary>
    /// A calendar day TFS sent, such as a sprint's start or a day off, as midnight UTC of that day. TFS sends
    /// these as midnight UTC ("2026-09-23T00:00:00Z"), which on a server behind UTC is the evening before in
    /// local time. Read its date parts; ToLocalTime would move it back to that evening.
    /// </summary>
    public static DateTime? Day(JsonElement el, string prop) => Timestamp(el, prop)?.Date;

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

    /// <summary>
    /// The object items of a TFS list response. A batch read with errorPolicy=omit answers null in place of
    /// each item it could not return, and every property read throws on a null element, so those are skipped.
    /// </summary>
    public static IEnumerable<JsonElement> ObjectValues(JsonElement el) =>
        Values(el).Where(item => item.ValueKind == JsonValueKind.Object);

    /// <summary>The requested ids that did not come back, in the order they were asked for.</summary>
    public static List<int> MissingIds(IEnumerable<int> requested, IEnumerable<int> returned)
    {
        var found = returned.ToHashSet();
        return requested.Where(id => !found.Contains(id)).Distinct().ToList();
    }

    /// <summary>A nested commit identity: { name, email, date }.</summary>
    public static (string Name, DateTime? Date) CommitPerson(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
            ? (Str(p, "name"), Timestamp(p, "date"))
            : ("", null);

    /// <summary>The email of a nested commit identity, or empty when there is none.</summary>
    public static string CommitEmail(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "email") : "";
}
