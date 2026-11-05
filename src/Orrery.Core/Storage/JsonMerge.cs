using System.Text.Json.Nodes;

namespace Orrery.Core.Storage;

/// <summary>
/// A three-way merge of JSON objects, for a data folder a sync app shares between computers: what this computer last
/// read or wrote (the base), what it has now (mine) and what's in the file now (theirs). A property only one side
/// changed takes that side's value; objects both changed are merged property by property; a value both changed to
/// different things keeps mine, and its path is listed as a conflict. Lists are values: one side's list or the other's.
/// A missing property and a null one are the same, as Orrery writes neither.
/// </summary>
public static class JsonMerge
{
    public static JsonObject Merge(JsonObject? baseline, JsonObject mine, JsonObject theirs, List<string> conflicts, string path = "")
    {
        var merged = new JsonObject();
        var keys = mine.Select(p => p.Key)
            .Concat(theirs.Select(p => p.Key).Where(k => !mine.ContainsKey(k)))
            .Concat((baseline ?? []).Select(p => p.Key).Where(k => !mine.ContainsKey(k) && !theirs.ContainsKey(k)))
            .ToList();
        foreach (var key in keys)
        {
            var b = baseline?[key];
            var m = mine[key];
            var t = theirs[key];
            JsonNode? value;
            if (Same(m, t) || Same(t, b))
            {
                value = m;
            }
            else if (Same(m, b))
            {
                value = t;
            }
            else if (m is JsonObject mo && t is JsonObject to && b is null or JsonObject)
            {
                value = Merge(b as JsonObject, mo, to, conflicts, path.Length == 0 ? key : path + "." + key);
            }
            else
            {
                conflicts.Add(path.Length == 0 ? key : path + "." + key);
                value = m;
            }

            if (value is not null)
            {
                merged[key] = value.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>Equal as JSON, a missing value and null counting as the same.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);
}
