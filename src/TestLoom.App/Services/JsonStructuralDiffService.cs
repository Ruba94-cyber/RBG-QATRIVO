using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Diagnostic diff (read-only) between two JSON payloads. Never edits, merges, normalizes
/// or writes original data. Paths are JSON Pointer-like and contain only field names/indices:
/// never include actual values from sensitive operational records.
/// </summary>
public static class JsonStructuralDiffService
{
    public sealed record Difference(string Path, string Kind)
    {
        public override string ToString() => $"{Kind}: {Path}";
    }

    public sealed record Report(IReadOnlyList<Difference> Items, bool Truncated)
    {
        public bool HasDifferences => Items.Count != 0;
        public string Summary => Items.Count == 0 && !Truncated
            ? "Nessuna differenza strutturale."
            : $"{Items.Count} differenze visualizzate" +
              (Truncated ? " (limite raggiunto, potrebbero essercene altre)" : "") +
              ". Nessun dato è stato modificato.";
        public string Details => Items.Count == 0 && !Truncated
            ? "Nessuna differenza strutturale."
            : string.Join(Environment.NewLine, Items.Select(x => x.ToString())) +
              (Truncated ? Environment.NewLine + "[Elenco troncato, nessun dato modificato.]" : "");
    }

    /// <summary>
    /// Uses a fixed depth/entry ceiling to protect low-power laptops from deeply nested
    /// malicious or unexpectedly large datasets. Values are never copied to the report.
    /// </summary>
    public static Report Compare(JsonElement draft, JsonElement current,
        int maxDifferences = 100, int maxDepth = 32)
    {
        if (maxDifferences < 1 || maxDifferences > 1000) throw new ArgumentOutOfRangeException(nameof(maxDifferences));
        if (maxDepth < 1 || maxDepth > 128) throw new ArgumentOutOfRangeException(nameof(maxDepth));

        var items = new List<Difference>(Math.Min(maxDifferences, 100));
        var truncated = false;

        void Add(string path, string kind)
        {
            if (items.Count >= maxDifferences) { truncated = true; return; }
            // Dynamic JSON object keys might be extremely long. Bound the
            // diagnostic string, not the original field or document.
            const int maxPathChars = 240;
            var displayPath = path.Length == 0 ? "/" : path;
            if (displayPath.Length > maxPathChars)
                displayPath = displayPath[..maxPathChars] + "… [percorso troncato]";
            items.Add(new Difference(displayPath, kind));
        }

        void Walk(JsonElement left, JsonElement right, string path, int depth)
        {
            if (truncated) return;
            // Avoid repeatedly traversing entire subtrees: compare primitive leaves or
            // use the recursion below for composite nodes.
            if (left.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) &&
                JsonElement.DeepEquals(left, right)) return;
            if (depth >= maxDepth)
            {
                // Reaching the traversal limit is not a difference by itself.
                // Evaluate equality once at this boundary to avoid false positives
                // in unchanged deeply nested documents.
                if (!JsonElement.DeepEquals(left, right))
                    Add(path, "Profondità massima");
                return;
            }
            if (left.ValueKind != right.ValueKind)
            {
                Add(path, "Tipo modificato");
                return;
            }

            switch (left.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var lp = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    var rp = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    bool ambiguous = false;
                    foreach (var property in left.EnumerateObject())
                        if (!lp.TryAdd(property.Name, property.Value)) ambiguous = true;
                    foreach (var property in right.EnumerateObject())
                        if (!rp.TryAdd(property.Name, property.Value)) ambiguous = true;
                    // Duplicate JSON keys are ambiguous; never silently declare a
                    // comparison equal or pick an arbitrary value from a malformed record.
                    if (ambiguous)
                    {
                        Add(path, "Chiavi JSON duplicate");
                        return;
                    }
                    foreach (var key in lp.Keys.Concat(rp.Keys).Distinct(StringComparer.Ordinal)
                        .OrderBy(k => k, StringComparer.Ordinal))
                    {
                        if (truncated) return;
                        var child = path + "/" + Escape(key);
                        var hasLeft = lp.TryGetValue(key, out var l);
                        var hasRight = rp.TryGetValue(key, out var r);
                        if (!hasLeft) Add(child, "Solo documento locale");
                        else if (!hasRight) Add(child, "Solo bozza");
                        else Walk(l, r, child, depth + 1);
                    }
                    break;
                }
                case JsonValueKind.Array:
                {
                    // Index the JsonElement arrays directly: avoid materializing potentially
                    // hundreds of thousands of entries on low-RAM corporate laptops.
                    var leftLength = left.GetArrayLength();
                    var rightLength = right.GetArrayLength();
                    var count = Math.Max(leftLength, rightLength);
                    for (int i = 0; i < count && !truncated; i++)
                    {
                        var child = path + "/" + i;
                        if (i >= leftLength) Add(child, "Solo documento locale");
                        else if (i >= rightLength) Add(child, "Solo bozza");
                        else Walk(left[i], right[i], child, depth + 1);
                    }
                    break;
                }
                default:
                    Add(path, "Valore modificato");
                    break;
            }
        }

        Walk(draft, current, "", 0);
        return new Report(items, truncated);
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);
}
