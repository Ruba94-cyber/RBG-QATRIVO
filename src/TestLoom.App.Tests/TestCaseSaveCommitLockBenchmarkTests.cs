using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Benchmark riproducibile richiesto dalla review del fix 46ee5b2 (lock di commit cross-processo per il
/// salvataggio concorrente, TestCaseCommitLock): misura l'overhead per singola chiamata a
/// <see cref="TestCaseRepository.Save(TestCase, bool, long?)"/> introdotto dall'acquisizione/rilascio del file
/// sentinella `.commitlock` attorno alla finestra rilettura -> confronto -> scrittura. Nessuna concorrenza reale
/// qui: 100 Save SEQUENZIALI dello stesso Test da un'unica postazione, così il numero misurato è isolato al
/// costo del lock (che nel caso non conteso è solo apertura/chiusura di un FileStream FileShare.None) e non
/// contaminato da tempi di attesa/retry. Scrive i numeri in .ai/artifacts/benchmark_save_commitlock.txt.
/// </summary>
public sealed class TestCaseSaveCommitLockBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TestCaseSaveBench_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_100SalvataggiSequenzialiDelloStessoTest_OverheadDelLockDiCommit()
    {
        var dataRoot = Path.Combine(_root, "data");
        var configPath = Path.Combine(_root, "local", "config.json");
        Directory.CreateDirectory(dataRoot);
        var configService = new AppConfigService(configPath);
        configService.Save(new AppConfig { CartellaCondivisa = dataRoot, Lingua = "it", OperatoreAttivo = "Master Bench" });
        using var stato = new AppState(configService, runBackgroundMaintenance: false);
        stato.InizializzaCartella(dataRoot);
        var ops = stato.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Sigilla(new Operatore { Nome = "Master Bench", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" }));
        stato.Master.Operatori.Save(ops);
        stato.ImpostaOperatore("Master Bench", "1234");

        stato.TestCases.Save(new TestCase
        {
            Id = "TC-BENCH-COMMITLOCK-0001",
            Componente = "BCM",
            Descrizione = "base",
            ModificatoDa = "Setup",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        stato.TestCases.RefreshIndex();

        const int iterazioni = 100;
        var tempiMs = new List<double>(iterazioni);
        var totale = Stopwatch.StartNew();
        for (var i = 0; i < iterazioni; i++)
        {
            var tc = stato.TestCases.GetAll().Single(t => t.Id == "TC-BENCH-COMMITLOCK-0001");
            tc.Descrizione = $"iterazione {i}";
            var sw = Stopwatch.StartNew();
            stato.TestCases.Save(tc, isNew: false, tc.RevisioneDati);
            sw.Stop();
            tempiMs.Add(sw.Elapsed.TotalMilliseconds);
        }
        totale.Stop();

        var ordinati = tempiMs.OrderBy(t => t).ToList();
        var p50 = Percentile(ordinati, 0.50);
        var p95 = Percentile(ordinati, 0.95);
        var mediaMs = tempiMs.Average();

        var report =
            $"[{DateTime.UtcNow:O}] Iterazioni={iterazioni} " +
            $"Totale={totale.ElapsedMilliseconds}ms Media={mediaMs:F3}ms p50={p50:F3}ms p95={p95:F3}ms" +
            Environment.NewLine;

        var artifactsDir = TrovaCartellaArtifacts();
        if (artifactsDir != null)
            File.AppendAllText(Path.Combine(artifactsDir, "benchmark_save_commitlock.txt"), report);

        // Nessuna asserzione di soglia rigida qui (il numero va confrontato manualmente prima/dopo il fix,
        // vedi report del task): il test serve a produrre il numero riproducibile, non a fallire in CI per
        // varianza di macchina. Verifica solo che il giro sia completato correttamente.
        Assert.Equal(iterazioni, tempiMs.Count);
    }

    private static double Percentile(List<double> ordinati, double percentile)
    {
        if (ordinati.Count == 0) return 0;
        var rank = percentile * (ordinati.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return ordinati[lower];
        var frazione = rank - lower;
        return ordinati[lower] + (ordinati[upper] - ordinati[lower]) * frazione;
    }

    private static string? TrovaCartellaArtifacts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, ".ai", "artifacts");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
