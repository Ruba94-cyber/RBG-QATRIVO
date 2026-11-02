using System.Diagnostics;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Benchmark del controllo di riferimenti prima di eliminare una Change Request (Category=Benchmark, fuori dal
/// ciclo rapido). PRIMA: il ciclo attuale, paginato su tutte le CR attive e archiviate (QueryActiveSlice/QueryArchivedSlice
/// a 500). DOPO: ReferenziataDaAltraCr, candidati dall'indice confermati sul JSON. Dataset: CR realistiche, circa il 14%
/// archiviate, circa l'1% citate da un'altra CR in CrCorrelate. Scale 5k e 20k CR.
/// </summary>
[Collection("Scale performance")]
public sealed class ChangeRequestRiferimentiBenchmarkTests
{
    private const int Ripetizioni = 5;

    /// <summary>Indici interrogati: due citati da un'altra CR (risultato true), uno no (false).</summary>
    private static readonly int[] IndiciInterrogati = { 7, 107, 1 };

    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ChangeRequestRiferimentiBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Scenari() =>
        from n in new[] { 5_000, 20_000 } select new object[] { n };

    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(Scenari))]
    public void EliminaCr_PrimaEDopoControlloRiferimenti(int n)
    {
        var radice = Path.Combine(Path.GetTempPath(), "testloom-bench-cr-" + Guid.NewGuid().ToString("N"));
        var indiceLocale = Path.Combine(radice, "_indice");
        try
        {
            var paths = new DataPaths(Path.Combine(radice, "condivisa"));
            ChangeRequestRiferimentiTests.CreaChangeRequest(paths, n);
            var repo = new ChangeRequestRepository(paths, null, indiceLocale);
            var sw0 = Stopwatch.StartNew();
            repo.RefreshIndex();
            _output.WriteLine($"[CR n={n:N0}] riconciliazione indice (una tantum, non misurata): {sw0.Elapsed.TotalSeconds:F1} s");

            var ids = IndiciInterrogati.Select(i => ChangeRequestRiferimentiTests.IdCr(i, n)).ToArray();
            var attesi = ids.Select(id => id == ChangeRequestRiferimentiTests.IdCr(7, n) || id == ChangeRequestRiferimentiTests.IdCr(107, n)).ToArray();

            // PRIMA: percorso attuale. Riscaldamento non misurato, poi Ripetizioni giri.
            foreach (var id in ids) _ = PrimaReferenziata(repo, id);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var prima = new List<double>();
            for (var r = 0; r < Ripetizioni; r++)
                for (var k = 0; k < ids.Length; k++)
                {
                    var sw = Stopwatch.StartNew();
                    var esito = PrimaReferenziata(repo, ids[k]);
                    prima.Add(sw.Elapsed.TotalMilliseconds);
                    Assert.Equal(attesi[k], esito);
                }

            // DOPO: percorso veloce. Stessa sequenza, stessa risposta attesa.
            foreach (var id in ids) _ = repo.ReferenziataDaAltraCr(id);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var dopo = new List<double>();
            for (var r = 0; r < Ripetizioni; r++)
                for (var k = 0; k < ids.Length; k++)
                {
                    var sw = Stopwatch.StartNew();
                    var esito = repo.ReferenziataDaAltraCr(ids[k]);
                    dopo.Add(sw.Elapsed.TotalMilliseconds);
                    Assert.Equal(attesi[k], esito);
                }

            // Costo della sola verifica di allineamento (stamp su disco contro indice, senza leggere i JSON).
            var allineamento = new List<double>();
            for (var r = 0; r < Ripetizioni; r++)
            {
                var sw = Stopwatch.StartNew();
                Assert.True(repo.IndiceAllineatoAiFile());
                allineamento.Add(sw.Elapsed.TotalMilliseconds);
            }

            _output.WriteLine($"[CR n={n:N0}] PRIMA paginazione completa: p50={Percentile(prima, 0.50):F1} ms p95={Percentile(prima, 0.95):F1} ms max={prima.Max():F1} ms ({prima.Count} controlli)");
            _output.WriteLine($"[CR n={n:N0}] DOPO indice + conferma JSON: p50={Percentile(dopo, 0.50):F1} ms p95={Percentile(dopo, 0.95):F1} ms max={dopo.Max():F1} ms ({dopo.Count} controlli)");
            _output.WriteLine($"[CR n={n:N0}] DOPO di cui allineamento indice (stamp): p50={Percentile(allineamento, 0.50):F1} ms p95={Percentile(allineamento, 0.95):F1} ms");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(radice, recursive: true); } catch (IOException) { /* residuo di un handle SQLite: nessun effetto sul test */ }
        }
    }

    /// <summary>Copia fedele di ArchivioViewModel.CrReferenziataDaAltraCr prima della modifica: misura il percorso attuale.</summary>
    private static bool PrimaReferenziata(ChangeRequestRepository repo, string crId)
    {
        foreach (var archived in new[] { false, true })
        {
            long offset = 0;
            while (true)
            {
                var slice = archived
                    ? repo.QueryArchivedSlice(offset, 500)
                    : repo.QueryActiveSlice(offset, 500);
                if (slice.Items.Any(c => c.Id != crId && c.CrCorrelate.Contains(crId, StringComparer.OrdinalIgnoreCase)))
                    return true;
                if (slice.Items.Count == 0 || checked(offset + slice.Items.Count) >= slice.TotalCount) break;
                offset = checked(offset + slice.Items.Count);
            }
        }
        return false;
    }

    private static double Percentile(List<double> campioni, double p)
    {
        var ordinati = campioni.OrderBy(x => x).ToList();
        var indice = (int)Math.Ceiling(p * ordinati.Count) - 1;
        return ordinati[Math.Clamp(indice, 0, ordinati.Count - 1)];
    }
}
