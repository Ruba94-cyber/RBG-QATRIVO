using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Dataset sintetico deterministico per l'indice locale dei Test (benchmark ed equivalenza). Nessun I/O sui JSON:
/// i path sono solo chiavi, l'indice non li legge.
/// </summary>
internal static class DatasetIndiceBatch
{
    /// <summary>Forma "fixture": come ArchivioSopraTettoFixture (nessuna esecuzione, nessun veicolo).</summary>
    public const string FormaLeggera = "leggera";

    /// <summary>Forma "ricca": veicoli, requisiti, funzionalita', una esecuzione con 3 operazioni (proiezioni piu' popolate).</summary>
    public const string FormaRicca = "ricca";

    public static TestCase Crea(int i, string forma)
    {
        var uidBytes = new byte[16];
        BitConverter.TryWriteBytes(uidBytes, i + 1);
        var baseData = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var tc = new TestCase
        {
            Uid = new Guid(uidBytes),
            Id = $"TC-{i:D6}",
            DataCreazione = baseData.AddDays(-1),
            UltimaModifica = baseData.AddMinutes(i % 997),
            RevisioneDati = 1,
            CreatoDa = "Paolo",
            DescrizioneBreve = "Test " + i,
            Descrizione = "Descrizione del test numero " + i,
            StatoLavoro = StatoLavoroTest.Pianificato,
            Componente = new EcuSelection(new[] { "Componente-A", i % 2 == 0 ? "Componente-B" : "Componente-C" }),
        };
        if (forma == FormaRicca)
        {
            tc.StatoLavoro = StatoLavoroTest.InCorso;
            tc.Assegnatario = "Luigi" + (i % 7);
            tc.Veicoli = new List<string> { "V" + (i % 40), "V" + ((i + 1) % 40) };
            tc.RequisitoId = new List<string> { "REQ-" + (i % 500), "REQ-" + ((i + 3) % 500) };
            tc.Funzionalita = new List<string> { "Funz-" + (i % 120) };
            tc.StrumentiUsati = new List<string> { "Strumento-" + (i % 9) };
            var esecuzione = new Esecuzione
            {
                Iterazione = 1,
                Data = baseData.AddMinutes(i % 500),
                Operatore = "Mario" + (i % 5),
            };
            for (var c = 0; c < 3; c++)
            {
                esecuzione.Condizioni.Add(new Condizione
                {
                    Uid = GuidDeterministico(i, c),
                    Funzionalita = "Funz-" + ((i + c) % 120),
                    Esito = c == 2 && i % 3 == 0 ? Esito.NonConforme : Esito.Conforme,
                    Componente = new EcuSelection(new[] { "Componente-A" }),
                });
            }
            esecuzione.SincronizzaEsito();
            tc.Esecuzioni.Add(esecuzione);
        }
        return tc;
    }

    /// <summary>Guid riproducibile: due costruzioni dello stesso dataset danno JSON e proiezioni byte per byte identici.</summary>
    private static Guid GuidDeterministico(int i, int parte)
    {
        var b = new byte[16];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), i + 1);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), parte + 7);
        b[15] = 0x5A;
        return new Guid(b);
    }

    public static string PathDi(string root, int i) => Path.Combine(root, $"bench_{i:D7}.json");

    public static List<LocalIndexMutation> Mutazioni(string root, int da, int quanti, string forma)
    {
        var lista = new List<LocalIndexMutation>(quanti);
        for (var i = da; i < da + quanti; i++)
        {
            var tc = Crea(i, forma);
            lista.Add(LocalIndexMutation.Upsert(PathDi(root, i), tc.UltimaModifica, 100, tc));
        }
        return lista;
    }
}

/// <summary>
/// Benchmark dell'inserimento nell'indice locale (LocalTestIndexStore.ApplyBatch). Attivo solo con
/// TestLoom_PERF_INDEX_BATCH=1 (fuori dal giro standard). TestLoom_PERF_INDEX_BATCH_SIZES = elenco di dimensioni separate da
/// virgola (default 20500,100000); TestLoom_PERF_INDEX_BATCH_FORMA = leggera|ricca|entrambe (default entrambe).
/// Scrive una riga per misura su console e su %TEMP%/ditm_perf_index_batch.txt.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class PerfIndiceBatchTests
{
    private readonly ITestOutputHelper _out;

    public PerfIndiceBatchTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void InserimentoBatch_MisuraMsPerRiga()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_INDEX_BATCH"), "1", StringComparison.Ordinal)) return;

        var dimensioni = (Environment.GetEnvironmentVariable("TestLoom_PERF_INDEX_BATCH_SIZES") ?? "20500,100000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        var formeEnv = Environment.GetEnvironmentVariable("TestLoom_PERF_INDEX_BATCH_FORMA") ?? "entrambe";
        var forme = formeEnv == "entrambe"
            ? new[] { DatasetIndiceBatch.FormaLeggera, DatasetIndiceBatch.FormaRicca }
            : new[] { formeEnv };
        var logPath = Path.Combine(Path.GetTempPath(), "ditm_perf_index_batch.txt");

        foreach (var forma in forme)
        foreach (var n in dimensioni)
        {
            var root = Path.Combine(Path.GetTempPath(), "TestLoom_PerfIdx_" + Guid.NewGuid().ToString("N"));
            var local = Path.Combine(Path.GetTempPath(), "TestLoom_PerfIdxLocal_" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new LocalTestIndexStore(root, local);
                var mutazioni = DatasetIndiceBatch.Mutazioni(root, 0, n, forma);
                var progressi = 0;
                var sw = Stopwatch.StartNew();
                store.ApplyBatch(mutazioni);
                sw.Stop();
                var riga = $"forma={forma} righe={n} totale_ms={sw.ElapsedMilliseconds} ms_per_riga={(double)sw.ElapsedMilliseconds / n:F3} " +
                           $"cpu_logiche={Environment.ProcessorCount} progress_eventi={progressi}";
                _out.WriteLine(riga);
                Console.WriteLine("[PERF-INDEX] " + riga);
                File.AppendAllText(logPath, DateTime.Now.ToString("s") + " " + riga + Environment.NewLine);
                Assert.Equal(n, store.LoadAll().Count);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
                try { if (Directory.Exists(local)) Directory.Delete(local, true); } catch { }
            }
        }
    }
}
