using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Banco A/B della matrice Report a 20.000 Test (6.6.1, feat(report) matrice multi-campo): costo del primo
/// ingresso della matrice (Prepara + Finestra 50x12, stesso percorso di ReportViewModel.GeneraMatrice) con UN
/// campo per asse in modalita' Percentuale, cioe' il caso che NON deve peggiorare. Le misure si fanno nello stesso
/// processo e sullo stesso archivio sintetico, prima e dopo la modifica (stesso banco, stessa macchina).
///
/// Attivo solo con TestLoom_PERF_MATRICE_AB=1 (Category Benchmark, come gli altri banchi di perf). Una misura di
/// riscaldamento non cronometrata + 5 misure con Stopwatch e GC stabilizzato prima di ciascuna. Output: riga
/// REPORT_MATRIX_AB sull'output xUnit e file .ai/artifacts/perf/matrice-ab-{etichetta}-20k.tsv (etichetta da
/// TestLoom_PERF_MEDIA_LABEL, default "corrente"). Nessuna asserzione sui tempi.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class ReportMatriceAbPerfTests
{
    private const int CampioniMisurati = 5;
    private readonly ITestOutputHelper _output;

    public ReportMatriceAbPerfTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MatriceAbVentimila_PrimoIngressoUnCampoPerAsse()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_MATRICE_AB"), "1", StringComparison.Ordinal)) return;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MEDIA_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var veicoli = Enumerable.Range(0, 40).Select(v => new Veicolo { Nome = $"VEH-{v:D2}", GammaNome = $"GAMMA-{v % 4}" }).ToList();
        var funzionalita = Enumerable.Range(0, 300).Select(f => new Funzionalita
        {
            Nome = $"FUN-{f:D3}",
            Categorie = new List<string> { $"CAT-{f % 5}" }
        }).ToList();
        var casi = CreaArchivio(20_000);
        var configurazioni = new (string Nome, ConfigurazioneMatriceReport Config)[]
        {
            ("ecu_x_categoria", new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.CategoriaFunzionalita }),
            ("sottoveicolo_x_funzionalita", new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.Sottoveicolo, AsseY = DimensioniMatrice.Funzionalita }),
        };

        var righe = new List<string>();
        foreach (var (nome, config) in configurazioni)
        {
            // Riscaldamento (JIT, inizializzazioni) fuori dalla misura.
            ReportMatriceService.GeneraFinestra(casi.Take(200), config, veicoli, funzionalita, [], 0, 50, 0, 12);

            var campioni = new double[CampioniMisurati];
            long allocazioneMib = 0;
            for (var i = 0; i < CampioniMisurati; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var allocazionePrima = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                var finestra = ReportMatriceService.GeneraFinestra(casi, config, veicoli, funzionalita, [], 0, 50, 0, 12);
                watch.Stop();
                GC.KeepAlive(finestra);
                campioni[i] = watch.Elapsed.TotalMilliseconds;
                allocazioneMib = (GC.GetAllocatedBytesForCurrentThread() - allocazionePrima) / 1_048_576;
            }
            Array.Sort(campioni);
            var p50 = campioni[CampioniMisurati / 2];
            var p95 = campioni[^1];
            var riga = $"REPORT_MATRIX_AB|etichetta={etichetta}|config={nome}|test=20k|campioni={CampioniMisurati}|p50_ms={p50:F1}|max_ms={p95:F1}|alloc_ultimo_mib={allocazioneMib}";
            _output.WriteLine(riga);
            righe.Add(riga);
        }

        var cartella = Path.Combine(FindRepositoryRoot(), ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartella);
        var file = Path.Combine(cartella, $"matrice-ab-{etichetta}-20k.tsv");
        File.AppendAllLines(file, righe.Select(r => r.Replace('|', '\t')));
    }

    /// <summary>Archivio sintetico: 200 componente, 300 funzionalita', 40 sottoveicoli; ogni Test porta una funzionalita',
    /// un componente e un veicolo, con esito eseguito su circa tre quarti dei Test (il resto da eseguire).</summary>
    private static List<TestCase> CreaArchivio(int n)
    {
        var base0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var casi = new List<TestCase>(n);
        for (var i = 0; i < n; i++)
        {
            var tc = new TestCase
            {
                Id = $"T-{i:D6}",
                Componente = $"Componente-{i % 200:D3}",
                Veicoli = new List<string> { $"VEH-{i % 40:D2}" },
                Funzionalita = new List<string> { $"FUN-{i % 300:D3}" },
            };
            var esito = (i % 4) switch { 0 => Esito.NonConforme, 1 => Esito.Conforme, 2 => Esito.ConformeConDeviazioni, _ => Esito.DaEseguire };
            tc.Esecuzioni.Add(new Esecuzione { Data = base0.AddMinutes(i), Esito = esito });
            casi.Add(tc);
        }
        return casi;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
