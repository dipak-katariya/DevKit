using System.Text.Json;
using System.Text.Json.Nodes;

namespace DevKit.Web.Services;

/// <summary>
/// What one save changed in a person's settings, as a JSON merge patch (RFC 7386): the fields that differ, objects
/// compared field by field, everything else (a list included) whole, and a removed field as null. Applying it to the
/// settings kept now changes only those fields, so a save from one browser tab never undoes what another tab saved
/// meanwhile, and never replaces a value this build does not know with its own default.
/// </summary>
internal static class SettingsChanges
{
    /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>, both settings JSON objects.</summary>
    public static JsonObject Between(string before, string after) =>
        Between(ParseObject(before), ParseObject(after));

    /// <summary><paramref name="basis"/>, a settings JSON object, with <paramref name="changes"/> applied.</summary>
    public static string Apply(string basis, JsonObject changes)
    {
        var merged = ParseObject(basis);
        Apply(merged, changes);
        return merged.ToJsonString(SettingsService.FileFormat);
    }

    private static JsonObject Between(JsonObject before, JsonObject after)
    {
        var changes = new JsonObject();
        foreach (var (name, value) in after)
        {
            if (!before.TryGetPropertyValue(name, out var old))
            {
                changes[name] = value?.DeepClone();
            }
            else if (old is JsonObject oldObject && value is JsonObject newObject)
            {
                var inner = Between(oldObject, newObject);
                if (inner.Count > 0)
                {
                    changes[name] = inner;
                }
            }
            else if (!JsonNode.DeepEquals(old, value))
            {
                changes[name] = value?.DeepClone();
            }
        }

        foreach (var (name, _) in before)
        {
            if (!after.ContainsKey(name))
            {
                changes[name] = null;
            }
        }

        return changes;
    }

    private static void Apply(JsonObject target, JsonObject changes)
    {
        foreach (var (name, value) in changes)
        {
            if (value is null)
            {
                target.Remove(name);
            }
            else if (value is JsonObject inner && target[name] is JsonObject kept)
            {
                Apply(kept, inner);
            }
            else
            {
                target[name] = value.DeepClone();
            }
        }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Settings must be a JSON object.");
}
