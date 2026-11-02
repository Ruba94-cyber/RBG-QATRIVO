using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Xunit.Abstractions;

namespace TestLoom.App.Tests.Perf;

/// <summary>
/// Micro-banco ciclo prestazioni 6.6.1, pulizie a basso rischio:
/// (1) FiltroMultiSelect.Popola con 1000 opzioni: tempo p50/p95 e numero di notifiche CollectionChanged per chiamata
///     (prima: Clear + una Add per opzione; dopo: un solo Reset);
/// (2) letture ripetute di GammeDisponibili e TestVincoloDisponibili nell'editor Test (prima: ricalcolate a ogni
///     accesso; dopo: memorizzate e invalidate solo quando cambiano gli input).
/// Attivo solo con TestLoom_PERF_P661_PULIZIE=1. Output: .ai/artifacts/perf/p661-pulizie-{etichetta}.tsv nella radice del
/// repository (cartella non versionata).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfP661PulizieTests
{
    private const int CampioniMisurati = 7;
    private const int LetturePerCampione = 300;
    private readonly ITestOutputHelper _output;

    public PerfP661PulizieTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MisuraPulizieFiltriEProprietaMemorizzate()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TestLoom_PERF_P661_PULIZIE"), "1", StringComparison.Ordinal)) return;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_P661_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";
        var righe = new List<string>();

        // (1) Popola con 1000 opzioni: notifiche per chiamata e tempo.
        var valori = Enumerable.Range(0, 1000).Select(i => ($"V{i:D4}", $"Voce {i}")).ToList();
        var filtro = new FiltroMultiSelect();
        var notifiche = 0;
        filtro.Opzioni.CollectionChanged += (_, _) => notifiche++;
        var tempiPopola = new List<double>();
        for (var k = 0; k <= CampioniMisurati; k++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var sw = Stopwatch.StartNew();
            filtro.Popola(valori);
            sw.Stop();
            if (k > 0) tempiPopola.Add(sw.Elapsed.TotalMilliseconds);
        }
        var notifichePerChiamata = notifiche / (double)(CampioniMisurati + 1);
        righe.Add(RigaTsv("Popola 1000 opzioni (ms per chiamata)", tempiPopola, $"notifiche per chiamata {notifichePerChiamata:F0}"));
        _output.WriteLine($"Popola 1000 opzioni: p50 {Percentile(tempiPopola, 0.5):F2} ms, notifiche per chiamata {notifichePerChiamata:F0}, opzioni {filtro.Opzioni.Count}");

        // (2) Proprieta' memorizzate dell'editor Test: dataset con un vincolo di collegamento attivo.
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_PerfP661Pulizie_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var state = TestStateFactory.Create();
            state.InizializzaCartella(dir);
            var t2 = Guid.Parse("44444444-4444-4444-8444-444444444444");
            var t1 = Guid.Parse("55555555-5555-4555-8555-555555555555");
            state.Master.TestTemplate.Save(new List<TestTemplate>
            {
                new() { Uid = t2, Nome = "Modello ammesso" },
                new() { Uid = t1, Nome = "Modello con vincolo", VincoloCollegamento = new VincoloCollegamentoTemplate { TemplateAmmessiUid = new List<Guid> { t2 } } },
            });
            state.Master.Veicoli.Save(Enumerable.Range(0, 1200).Select(i => new Veicolo { Nome = $"VEI-{i:D4}", GammaNome = $"Gamma {i % 60:D2}" }).ToList());
            for (var i = 0; i < 2000; i++)
                state.TestCases.Save(new TestCase { Id = $"TC-{i:D4}", TestTemplateUid = i % 2 == 0 ? t1 : t2, Descrizione = $"Test {i}" }, isNew: true);
            state.RicaricaTutto();

            var editor = new TestCaseEditorViewModel(state, "TC-0000", () => { }, _ => { });
            var tempiGamme = new List<double>();
            var tempiVincolo = new List<double>();
            var conteggioGamme = 0;
            var conteggioVincolo = 0;
            for (var k = 0; k <= CampioniMisurati; k++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var sw = Stopwatch.StartNew();
                for (var r = 0; r < LetturePerCampione; r++) conteggioGamme = editor.GammeDisponibili.Count;
                sw.Stop();
                if (k > 0) tempiGamme.Add(sw.Elapsed.TotalMilliseconds);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                sw.Restart();
                for (var r = 0; r < LetturePerCampione; r++) conteggioVincolo = editor.TestVincoloDisponibili.Count;
                sw.Stop();
                if (k > 0) tempiVincolo.Add(sw.Elapsed.TotalMilliseconds);
            }
            righe.Add(RigaTsv($"GammeDisponibili ({LetturePerCampione} letture, ms per campione)", tempiGamme, $"gamme {conteggioGamme}"));
            righe.Add(RigaTsv($"TestVincoloDisponibili ({LetturePerCampione} letture, ms per campione)", tempiVincolo, $"test ammessi {conteggioVincolo}"));
            _output.WriteLine($"GammeDisponibili: p50 {Percentile(tempiGamme, 0.5):F2} ms per {LetturePerCampione} letture (gamme {conteggioGamme})");
            _output.WriteLine($"TestVincoloDisponibili: p50 {Percentile(tempiVincolo, 0.5):F2} ms per {LetturePerCampione} letture (test ammessi {conteggioVincolo})");
        }
        finally
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }

        var tsv = new StringBuilder();
        tsv.AppendLine("scenario\tp50_ms\tp95_ms\tmin_ms\tmax_ms\tnota");
        foreach (var r in righe) tsv.AppendLine(r);
        var radice = TrovaRadiceRepo() ?? Path.GetTempPath();
        var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartella);
        File.WriteAllText(Path.Combine(cartella, $"p661-pulizie-{etichetta}.tsv"), tsv.ToString());
    }

    private static string RigaTsv(string scenario, List<double> campioni, string nota)
    {
        var ordinati = campioni.OrderBy(x => x).ToArray();
        return string.Join('\t', scenario, Percentile(campioni, 0.5).ToString("F3", CultureInfo.InvariantCulture),
            Percentile(campioni, 0.95).ToString("F3", CultureInfo.InvariantCulture), ordinati[0].ToString("F3", CultureInfo.InvariantCulture),
            ordinati[^1].ToString("F3", CultureInfo.InvariantCulture), nota);
    }

    private static double Percentile(List<double> campioni, double p)
    {
        var ordinati = campioni.OrderBy(x => x).ToArray();
        var idx = Math.Min(ordinati.Length - 1, (int)Math.Ceiling(p * ordinati.Length) - 1);
        return ordinati[Math.Max(0, idx)];
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
