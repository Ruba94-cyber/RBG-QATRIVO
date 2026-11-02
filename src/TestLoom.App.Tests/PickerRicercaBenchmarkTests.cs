using System.Diagnostics;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Benchmark di latenza di digitazione nei selettori con elenchi lunghi (Category=Benchmark, fuori dal ciclo
/// rapido). PRIMA: filtro sincrono ordinale sul thread UI, come SelezionaDialog.Filtra prima di questa modifica.
/// DOPO: PickerMotore con chiavi normalizzate preparate in anticipo e PickerRicercaController. Per ogni tasto
/// (prefisso crescente della query) misura: lavoro sul thread UI (Digita), calcolo fuori dal thread UI, tempo fino
/// al risultato pronto (fine-a-fine, senza il debounce di 200 ms, che si aggiunge solo all'ultimo tasto).
/// Dataset deterministici WU / componente / Requisiti a 20k e 100k voci. Eseguire in Debug, come il resto della suite.
/// </summary>
[Collection("Scale performance")]
public sealed class PickerRicercaBenchmarkTests
{
    private const int Ripetizioni = 5;
    private const double TargetP95Ms = 200;
    private const double TargetUiMs = 50;

    private static readonly string[] Parole =
    {
        "frenatura", "motore", "centralina", "sensore", "attuatore", "tensione", "perché", "città",
        "errore", "attivazione", "dispositivo", "gamma", "è", "più", "massa", "temperatura",
    };

    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public PickerRicercaBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Scenari() =>
        from n in new[] { 20_000, 100_000 }
        from tipo in new[] { "WU", "Componente", "Requisiti" }
        select new object[] { tipo, n };

    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(Scenari))]
    public async Task Digitazione_LatenzaFineAFineSottoTargetEUiSottoSoglia(string tipo, int n)
    {
        var voci = Dataset(tipo, n);
        var query = QueryDi(tipo);
        var prefissi = Enumerable.Range(1, query.Length).Select(k => query[..k]).ToArray();

        // Riscaldamento non misurato (JIT dei percorsi, primo contatto con il thread pool) e GC prima di ogni misura:
        // una pausa GC nel mezzo di una suite parallela non e' lavoro del selettore.
        foreach (var prefisso in prefissi) _ = LegacyFiltra(voci, prefisso);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // PRIMA: filtro sincrono sul thread UI (tutto il costo e' sul thread UI, a ogni tasto).
        var prima = new List<double>();
        for (var r = 0; r < Ripetizioni; r++)
            foreach (var prefisso in prefissi)
            {
                var sw = Stopwatch.StartNew();
                _ = LegacyFiltra(voci, prefisso);
                prima.Add(sw.Elapsed.TotalMilliseconds);
            }

        // DOPO: chiavi normalizzate preparate in anticipo (fuori dal thread UI), poi ricerca per tasto.
        var motore = new PickerMotore<VoceSelezionabile>(voci, PickerTesto.ChiaveVoce);
        var swPreparazione = Stopwatch.StartNew();
        await motore.PreparaAsync();
        var preparazioneMs = swPreparazione.Elapsed.TotalMilliseconds;

        var calcoloFuoriUi = new List<double>();
        for (var r = 0; r < Ripetizioni; r++)
            foreach (var prefisso in prefissi)
            {
                var sw = Stopwatch.StartNew();
                _ = motore.Cerca(prefisso);
                calcoloFuoriUi.Add(sw.Elapsed.TotalMilliseconds);
            }

        var uiPerTasto = new List<double>();
        var fineAFine = new List<double>();
        using var controller = new PickerRicercaController<VoceSelezionabile>(motore, _ => { }, 0, null, azione => azione());
        foreach (var prefisso in prefissi)     // riscaldamento del percorso asincrono, non misurato
        {
            controller.Digita(prefisso);
            await controller.UltimaRicerca;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (var r = 0; r < Ripetizioni; r++)
            foreach (var prefisso in prefissi)
            {
                var sw = Stopwatch.StartNew();
                controller.Digita(prefisso);          // solo lavoro sul thread UI: incremento generazione e avvio
                uiPerTasto.Add(sw.Elapsed.TotalMilliseconds);
                await controller.UltimaRicerca;       // risultato pronto (calcolo fuori UI incluso)
                fineAFine.Add(sw.Elapsed.TotalMilliseconds);
            }

        _output.WriteLine($"[{tipo} n={n:N0}] PRIMA sincrono su UI: p95={Percentile(prima, 0.95):F1} ms max={prima.Max():F1} ms ({prima.Count} tasti)");
        _output.WriteLine($"[{tipo} n={n:N0}] DOPO prep chiavi (una tantum, fuori UI): {preparazioneMs:F0} ms");
        _output.WriteLine($"[{tipo} n={n:N0}] DOPO calcolo fuori UI: p95={Percentile(calcoloFuoriUi, 0.95):F1} ms max={calcoloFuoriUi.Max():F1} ms");
        _output.WriteLine($"[{tipo} n={n:N0}] DOPO lavoro su UI per tasto: p95={Percentile(uiPerTasto, 0.95):F3} ms max={uiPerTasto.Max():F3} ms");
        _output.WriteLine($"[{tipo} n={n:N0}] DOPO fine-a-fine (senza debounce): p95={Percentile(fineAFine, 0.95):F1} ms max={fineAFine.Max():F1} ms ({fineAFine.Count} tasti)");

        Assert.True(Percentile(fineAFine, 0.95) < TargetP95Ms,
            $"p95 fine-a-fine {Percentile(fineAFine, 0.95):F1} ms oltre il target di {TargetP95Ms} ms ({tipo}, {n} voci)");
        Assert.True(Percentile(uiPerTasto, 0.95) < TargetUiMs,
            $"lavoro sul thread UI p95 {Percentile(uiPerTasto, 0.95):F3} ms per tasto oltre {TargetUiMs} ms ({tipo}, {n} voci)");
    }

    /// <summary>Filtro di SelezionaDialog.Filtra PRIMA della modifica (replica fedele: ordinale, senza accenti).</summary>
    private static List<VoceSelezionabile> LegacyFiltra(List<VoceSelezionabile> tutte, string filtro) =>
        string.IsNullOrWhiteSpace(filtro)
            ? tutte
            : tutte.Where(v => v.TestoRicerca.Contains(filtro, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string QueryDi(string tipo) => tipo switch
    {
        "WU" => "WU-004",
        "Componente" => "centralina",
        _ => "frenatura",
    };

    private static double Percentile(List<double> campioni, double p)
    {
        var ordinati = campioni.OrderBy(x => x).ToList();
        var indice = (int)Math.Ceiling(p * ordinati.Count) - 1;
        return ordinati[Math.Clamp(indice, 0, ordinati.Count - 1)];
    }

    private static List<VoceSelezionabile> Dataset(string tipo, int n)
    {
        var rnd = new Random(7 + n);
        var voci = new List<VoceSelezionabile>(n);
        for (var i = 0; i < n; i++)
        {
            string principale;
            string secondario;
            switch (tipo)
            {
                case "WU":
                    principale = $"WU-{i:D6}";
                    secondario = $"Veicolo V{rnd.Next(1, 400):D3} Rev {(char)('A' + rnd.Next(0, 6))} {Frase(rnd, 3)}";
                    break;
                case "Componente":
                    principale = $"Componente-{i:D6}";
                    secondario = $"{Frase(rnd, 2)} gamma {rnd.Next(1, 60)}";
                    break;
                default:
                    principale = $"REQ-{i:D6}";
                    secondario = Frase(rnd, 10);
                    break;
            }
            voci.Add(new VoceSelezionabile
            {
                Valore = i,
                TestoPrincipale = principale,
                TestoSecondario = secondario,
                TestoRicerca = $"{principale} {secondario}",
            });
        }
        return voci;
    }

    private static string Frase(Random rnd, int parole) =>
        string.Join(' ', Enumerable.Range(0, parole).Select(_ => Parole[rnd.Next(Parole.Length)]));
}
