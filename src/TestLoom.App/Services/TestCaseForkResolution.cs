using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Traccia DENTRO il Test JSON quali fork OneDrive sono stati esplicitamente risolti da un Master.
/// Nessun sidecar: i client precedenti preservano il campo grazie a TestCase.JsonExtensionData.
/// Il journal può quindi essere compattato liberamente senza far riapparire un vecchio conflitto.
/// </summary>
public static class TestCaseForkResolution
{
    public const string StampKey = "_ditmResolvedForks";

    /// <summary>Unione deterministica dei Changelog di più versioni dello stesso Test (rami di un fork, versione
    /// corrente, risultato): ogni voce compare una sola volta, in ordine di data. Changelog è un campo tecnico
    /// escluso dal merge a tre vie, quindi senza questa unione il risultato conserverebbe le voci di un solo ramo.
    /// Usata sia dalla risoluzione manuale del Master sia dal merge automatico.</summary>
    public static List<ChangelogEntry> UnisciChangelog(IEnumerable<TestCase> versioni)
    {
        var uniche = new Dictionary<string, ChangelogEntry>(StringComparer.Ordinal);
        foreach (var voce in versioni.SelectMany(v => v.Changelog ?? new List<ChangelogEntry>()))
            uniche.TryAdd(JsonSerializer.Serialize(voce, JsonIo.Options), voce);
        return uniche.Values.OrderBy(e => e.Data).ToList();
    }

    public static string Key(JournalForkConflict fork)
    {
        var canonical = $"{fork.EntityUid:N}|{fork.BaseRevision}|{fork.BaseHash?.ToUpperInvariant()}|" +
                        string.Join('|', fork.Branches
                            .Select(b => $"{b.NewRevision}:{b.NewHash?.ToUpperInvariant()}")
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static IReadOnlyList<string> GetResolvedKeys(TestCase testCase)
    {
        if (testCase.ExtensionData == null || !testCase.ExtensionData.TryGetValue(StampKey, out var raw) ||
            raw.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return raw.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsResolved(TestCase testCase, JournalForkConflict fork) =>
        GetResolvedKeys(testCase).Contains(Key(fork), StringComparer.OrdinalIgnoreCase);

    public static void MarkResolved(TestCase testCase, IEnumerable<JournalForkConflict> forks)
    {
        var keys = GetResolvedKeys(testCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fork in forks) keys.Add(Key(fork));
        testCase.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        testCase.ExtensionData[StampKey] = JsonSerializer.SerializeToElement(keys.OrderBy(x => x).ToArray(), JsonIo.Options);
    }

    public static void SetResolvedKeys(TestCase testCase, IEnumerable<string> keys)
    {
        var normalized = keys.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        if (normalized.Length == 0)
        {
            testCase.ExtensionData?.Remove(StampKey);
            return;
        }
        testCase.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        testCase.ExtensionData[StampKey] = JsonSerializer.SerializeToElement(normalized, JsonIo.Options);
    }
}
