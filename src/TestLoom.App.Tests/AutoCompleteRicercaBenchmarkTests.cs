using System.Diagnostics;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Benchmark del costo per tasto degli AutoCompleteBox con elenchi lunghi (ReportView: componente e veicoli), solo
/// logica, senza rendering (Category=Benchmark, fuori dal ciclo rapido). PRIMA: filtro interno di AutoCompleteBox
/// (FilterMode Custom con AutoCompleteFilters.Stringa), eseguito sul thread UI a ogni tasto su tutte le voci.
/// DOPO: AutoCompleteRicerca (motore e controller come nel collegamento), con lavoro sul thread UI per tasto = solo
/// Digita; il calcolo avviene fuori dal thread UI. Invio con ricerca ancora in attesa = Allinea, misurato a parte.
/// </summary>
[Collection("Scale performance")]
public sealed class AutoCompleteRicercaBenchmarkTests
{
    private const int Ripetizioni = 5;
    private const string QueryDiProva = "centralina";

    private static readonly string[] Parole =
    {
        "frenatura", "motore", "centralina", "sensore", "attuatore", "tensione", "perché", "città",
        "errore", "attivazione", "dispositivo", "gamma", "è", "più", "massa", "temperatura",
    };

    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AutoCompleteRicercaBenchmarkTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Scenari() =>
        from n in new[] { 5_000, 10_000, 20_000 } select new object[] { n };

    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(Scenari))]
    public async Task Digitazione_PrimaEDopoSulThreadUi(int n)
    {
        var elementi = Dataset(n);
        var prefissi = Enumerable.Range(1, QueryDiProva.Length).Select(k => QueryDiProva[..k]).ToArray();

        // PRIMA: filtro interno sul thread UI, a ogni tasto. Riscaldamento non misurato e GC prima della misura.
        foreach (var prefisso in prefissi) _ = FiltroInterno(elementi, prefisso);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var prima = new List<double>();
        for (var r = 0; r < Ripetizioni; r++)
            foreach (var prefisso in prefissi)
            {
                var sw = Stopwatch.StartNew();
                _ = FiltroInterno(elementi, prefisso);
                prima.Add(sw.Elapsed.TotalMilliseconds);
            }

        // DOPO: motore con chiavi preparate in anticipo; il post sul thread UI e' in linea (nessun Dispatcher in test).
        using var ricerca = new AutoCompleteRicerca<string>(elementi, voce => voce, _ => { }, postaSulThreadUi: azione => azione());
        foreach (var prefisso in prefissi)
        {
            ricerca.Digita(prefisso);
            await ricerca.UltimaRicerca;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var uiPerTasto = new List<double>();
        var fineAFine = new List<double>();
        for (var r = 0; r < Ripetizioni; r++)
            foreach (var prefisso in prefissi)
            {
                var sw = Stopwatch.StartNew();
                ricerca.Digita(prefisso);
                uiPerTasto.Add(sw.Elapsed.TotalMilliseconds);     // solo lavoro sul thread UI
                await ricerca.UltimaRicerca;
                fineAFine.Add(sw.Elapsed.TotalMilliseconds);      // risultato applicato
            }

        // Invio con ricerca ancora in attesa: il caso peggiore del percorso nuovo, calcolo sincrono sul thread UI.
        // Istanza con debounce di 60 s: la ricerca resta pendente (non si applica da sola) finche' Allinea non la annulla.
        using var ricercaLenta = new AutoCompleteRicerca<string>(elementi, voce => voce, _ => { },
            debounceMs: 60_000, postaSulThreadUi: azione => azione());
        var invio = new List<double>();
        for (var r = 0; r < Ripetizioni; r++)
            foreach (var prefisso in prefissi)
            {
                ricercaLenta.Digita(prefisso);
                var sw = Stopwatch.StartNew();
                ricercaLenta.Allinea(prefisso);
                invio.Add(sw.Elapsed.TotalMilliseconds);
                await ricercaLenta.UltimaRicerca;        // la ricerca annullata non applica nulla
            }

        _output.WriteLine($"[componente n={n:N0}] PRIMA filtro interno sul thread UI per tasto: p50={Percentile(prima, 0.50):F3} ms p95={Percentile(prima, 0.95):F3} ms max={prima.Max():F3} ms ({prima.Count} tasti)");
        _output.WriteLine($"[componente n={n:N0}] DOPO lavoro sul thread UI per tasto (Digita): p50={Percentile(uiPerTasto, 0.50):F4} ms p95={Percentile(uiPerTasto, 0.95):F4} ms max={uiPerTasto.Max():F4} ms");
        _output.WriteLine($"[componente n={n:N0}] DOPO fine-a-fine senza debounce: p50={Percentile(fineAFine, 0.50):F3} ms p95={Percentile(fineAFine, 0.95):F3} ms max={fineAFine.Max():F3} ms");
        _output.WriteLine($"[componente n={n:N0}] DOPO Invio con ricerca in attesa (Allinea, sul thread UI): p50={Percentile(invio, 0.50):F3} ms p95={Percentile(invio, 0.95):F3} ms max={invio.Max():F3} ms");
    }

    /// <summary>Replica del filtro interno di AutoCompleteBox con FilterMode Custom: un predicato per voce, poi la lista
    /// delle voci visibili nel menu a tendina. Usa il predicato reale dell'app (AutoCompleteFilters.Stringa).</summary>
    private static List<string> FiltroInterno(IReadOnlyList<string> elementi, string testo)
    {
        var risultato = new List<string>();
        for (var i = 0; i < elementi.Count; i++)
            if (AutoCompleteFilters.Stringa(testo, elementi[i])) risultato.Add(elementi[i]);
        return risultato;
    }

    private static List<string> Dataset(int n)
    {
        var rnd = new Random(11 + n);
        var voci = new List<string>(n);
        for (var i = 0; i < n; i++)
            voci.Add($"Componente-{i:D6} {Frase(rnd, 2)} gamma {rnd.Next(1, 60)}");
        return voci;
    }

    private static string Frase(Random rnd, int parole) =>
        string.Join(' ', Enumerable.Range(0, parole).Select(_ => Parole[rnd.Next(Parole.Length)]));

    private static double Percentile(List<double> campioni, double p)
    {
        var ordinati = campioni.OrderBy(x => x).ToList();
        var indice = (int)Math.Ceiling(p * ordinati.Count) - 1;
        return ordinati[Math.Clamp(indice, 0, ordinati.Count - 1)];
    }
}
