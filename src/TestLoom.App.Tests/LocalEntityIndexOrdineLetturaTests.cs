using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Parita' di <see cref="LocalEntityIndexStore.EnumerateOrdinatoPerLettura{T}"/> con <see cref="LocalEntityIndexStore.Enumerate{T}"/>:
/// stessi record, stesso ordine (modified_ticks DESC, item_key NOCASE, path) anche con pareggi di modifica, chiavi che differiscono
/// solo per maiuscole, e tutte le combinazioni di archived/deleted. L'ordine atteso e' calcolato qui in .NET, indipendentemente dalla query.
/// </summary>
public sealed class LocalEntityIndexOrdineLetturaTests : IDisposable
{
    private const string Kind = "change-request";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_EntOrd_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void EnumerateOrdinatoPerLettura_StessiRecordEStessoOrdineDiEnumerate_ConPareggiECombinazioniDiFiltro()
    {
        var store = new LocalEntityIndexStore(Path.Combine(_root, "shared"), Path.Combine(_root, "local"));
        var istanti = new[] { new DateTime(2026, 6, 1, 8, 0, 0), new DateTime(2026, 6, 1, 9, 0, 0), new DateTime(2026, 6, 2, 8, 0, 0) };
        var chiavi = new[] { "CR-a", "cr-A", "CR-B", "cr-b", "CR-10", "CR-9", "CR-z" };
        var attesi = new Dictionary<string, (DateTime Modifica, string Chiave, bool Archiviata, bool Eliminata)>(StringComparer.Ordinal);

        for (var n = 0; n < 240; n++)
        {
            var chiave = chiavi[n % chiavi.Length] + "-" + (n % 7);
            var modifica = istanti[n % istanti.Length];
            var archiviata = n % 5 == 0;
            var eliminata = n % 11 == 0;
            var path = $"cr_{n:D4}_{(n * 37) % 97:D2}.json";
            var cr = new ChangeRequest { Uid = Guid.NewGuid(), Id = chiave, Titolo = "titolo " + n, Archiviata = archiviata };
            store.Upsert(Kind, path, modifica.AddMinutes(n), 100 + n, cr, c => c.Id, c => c.Uid,
                archived: archiviata, deleted: eliminata, modified: modifica);
            attesi[path] = (modifica, chiave, archiviata, eliminata);
        }
        // Un altro kind non deve comparire nelle letture di change-request.
        var altra = new ChangeRequest { Uid = Guid.NewGuid(), Id = "CR-altro", Titolo = "altro kind" };
        store.Upsert("altro-kind", "altro.json", istanti[0], 1, altra, c => c.Id, c => c.Uid, modified: istanti[0]);

        foreach (var archiviataFiltro in new bool?[] { null, false, true })
        foreach (var eliminataFiltro in new bool?[] { null, false, true })
        {
            var attesiFiltrati = attesi
                .Where(kv => (archiviataFiltro == null || kv.Value.Archiviata == archiviataFiltro)
                          && (eliminataFiltro == null || kv.Value.Eliminata == eliminataFiltro))
                .OrderByDescending(kv => kv.Value.Modifica.Ticks)
                .ThenBy(kv => AsciiMinuscolo(kv.Value.Chiave), StringComparer.Ordinal)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key)
                .ToList();

            var perEnumerate = store.Enumerate<ChangeRequest>(Kind, archiviataFiltro, eliminataFiltro).ToList();
            var perLettura = store.EnumerateOrdinatoPerLettura<ChangeRequest>(Kind, archiviataFiltro, eliminataFiltro);

            var contesto = $"archived={archiviataFiltro?.ToString() ?? "tutti"}, deleted={eliminataFiltro?.ToString() ?? "tutti"}";
            Assert.True(attesiFiltrati.SequenceEqual(perEnumerate.Select(r => r.Path)), "Ordine di Enumerate diverso dall'atteso: " + contesto);
            Assert.Equal(perEnumerate.Count, perLettura.Count);
            for (var i = 0; i < perEnumerate.Count; i++)
            {
                var a = perEnumerate[i];
                var b = perLettura[i];
                Assert.True(a.Path == b.Path && a.Uid == b.Uid && a.Key == b.Key && a.MtimeUtc == b.MtimeUtc
                    && a.FileLength == b.FileLength && a.ContentHash == b.ContentHash && a.Entity.Id == b.Entity.Id,
                    $"Record {i} diverso ({contesto}): {a.Path} / {b.Path}");
            }
        }
    }

    /// <summary>Confronto NOCASE di SQLite: solo i caratteri ASCII A-Z vengono ripiegati in minuscolo.</summary>
    private static string AsciiMinuscolo(string testo) =>
        new(testo.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray());
}
