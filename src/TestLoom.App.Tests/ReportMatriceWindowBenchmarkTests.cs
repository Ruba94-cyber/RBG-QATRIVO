using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>Misura isolata della proiezione completa che la UI usava prima contro la finestra corrente.
/// Non include il layout Avalonia: l'app reale resta un cancello distinto.</summary>
public sealed class ReportMatriceWindowBenchmarkTests
{
    private readonly ITestOutputHelper _output;
    public ReportMatriceWindowBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void MatriceMillePerMille_ConfrontaProiezioneCompletaEFinestra()
    {
        var casi = Enumerable.Range(0, 1000).Select(i => new TestCase
        {
            Id = $"T-{i:D4}", Componente = $"Componente-{i:D4}", Assegnatario = $"Operatore-{i:D4}"
        }).ToArray();
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente,
            AsseY = DimensioniMatrice.Assegnatario
        };

        // JIT e inizializzazione fuori dalla misura; i due percorsi condividono accumulazione e ordinamento.
        ReportMatriceService.GeneraFinestra(casi.Take(10), config, [], [], [], 0, 10, 0, 10);
        ReportMatriceService.Genera(casi.Take(10), config, [], [], []);

        (double Ms, long Bytes) Misura(Func<RisultatoMatriceReport> calcolo)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var prima = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var risultato = calcolo();
            watch.Stop();
            GC.KeepAlive(risultato);
            return (watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - prima);
        }

        var completa = Misura(() => ReportMatriceService.Genera(casi, config, [], [], []));
        var finestra = Misura(() => ReportMatriceService.GeneraFinestra(casi, config, [], [], [], 0, 50, 0, 12).Risultato);
        _output.WriteLine($"REPORT_MATRIX|assi=1000x1000|completa_ms={completa.Ms:F1}|completa_alloc_mib={completa.Bytes / 1048576.0:F1}|finestra_ms={finestra.Ms:F1}|finestra_alloc_mib={finestra.Bytes / 1048576.0:F1}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void ArchivioCentomila_ConfrontaValoriESecondaEspansione()
    {
        var casi = Enumerable.Range(0, 104_000).Select(i => new TestCase
        {
            Id = $"T-{i:D6}", Componente = $"Componente-{i % 1000:D4}", Assegnatario = $"Operatore-{i % 1000:D4}"
        }).ToArray();
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.Componente, AsseY = DimensioniMatrice.Assegnatario
        };
        var dimensioni = new[] { DimensioniMatrice.Componente, DimensioniMatrice.Assegnatario,
            DimensioniMatrice.Gamma, DimensioniMatrice.Funzionalita, DimensioniMatrice.Template };
        ReportMatriceService.GeneraFinestra(casi.Take(100), config, [], [], [], 0, 50, 0, 12);
        ReportMatriceService.ValoriDisponibiliPerDimensioni(dimensioni, casi.Take(100), [], [], []);

        double Misura(Action azione)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var watch = Stopwatch.StartNew();
            azione();
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }
        var valoriMs = Misura(() => ReportMatriceService.ValoriDisponibiliPerDimensioni(dimensioni, casi, [], [], []));
        var matriceMs = Misura(() => ReportMatriceService.GeneraFinestra(casi, config, [], [], [], 0, 50, 0, 12));
        _output.WriteLine($"REPORT_104K|valori_ms={valoriMs:F1}|matrice_ms={matriceMs:F1}");
    }
}
