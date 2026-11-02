using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Benchmark sintetico mirato (review indipendente 2026-09-24, HIGH), non il gate 100k Test completo
/// (macchina condivisa — da eseguire a parte): isola l'hotspot corretto in
/// AuditMatriceService.CalcolaKpiComplessivi — per ogni cella (Componente, Requisito, Veicolo) priva di Diretto/
/// valutazione salvata, ValutazioneRequisitoEcuVeicoloService.CandidatiPropagazione rifaceva da zero la
/// scansione completa dei Test della coppia per OGNUNO degli altri veicoli di Componente.VeicoliAssociati: su
/// un componente condivisa da molti veicoli, lo stesso lavoro veniva ripetuto una volta per ciascun veicolo,
/// costo quadratico nel numero di veicoli condivisi invece che lineare nel numero di Test. Confronta
/// direttamente, sullo stesso dataset sintetico e nello stesso processo, l'overload originale (ancora in uso
/// da chi risolve UNA sola cella, es. RisolviCella) con quello che riusa AuditMatriceService.
/// CandidatiDirettiPerVeicolo precalcolato una volta per coppia (Componente, RequisitoId) — il percorso ora usato da
/// CalcolaKpiComplessivi.</summary>
public class AuditKpiPropagazioneScaleTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public AuditKpiPropagazioneScaleTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Scale")]
    public void CandidatiPropagazione_EcuCondivisaDaMoltiVeicoli_VersionePrecalcolataMoltoPiuVeloce()
    {
        const int numeroVeicoli = 30;
        const int numeroTest = 2000;
        const string requisitoId = "R1";

        var veicoli = Enumerable.Range(0, numeroVeicoli).Select(i => $"V{i:D2}").ToList();
        var componente = new Componente { Nome = "SHARED", VeicoliAssociati = veicoli };

        var testDellaCoppia = new List<TestCase>();
        for (var i = 0; i < numeroTest; i++)
        {
            var veicolo = veicoli[i % numeroVeicoli];
            var tc = new TestCase { Id = $"T{i}", Componente = "SHARED", RequisitoId = { requisitoId }, Veicolo = veicolo };
            tc.Esecuzioni.Add(new Esecuzione
            {
                Data = new DateTime(2026, 1, 1).AddMinutes(i),
                Esito = Esito.Conforme,
                Matricola = "V",
                PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = requisitoId, Valore = PunteggioConformita.Conforme } },
            });
            testDellaCoppia.Add(tc);
        }

        var valutazioniVuote = new Dictionary<string, ValutazioneRequisitoEcuVeicolo>(StringComparer.OrdinalIgnoreCase);

        // Warmup: una passata di entrambi i percorsi fuori dal cronometro, cosi' il confronto non e' distorto
        // dal JIT/dall'allocazione a freddo del primo metodo misurato.
        _ = ValutazioneRequisitoEcuVeicoloService.CandidatiPropagazione(requisitoId, componente, veicoli[0], testDellaCoppia, valutazioniVuote);
        _ = ValutazioneRequisitoEcuVeicoloService.CandidatiPropagazione(componente, veicoli[0], AuditMatriceService.CandidatiDirettiPerVeicolo(requisitoId, testDellaCoppia), valutazioniVuote);

        // "Prima" del fix: overload originale, invariato — ancora legittimo per chi risolve una sola cella
        // (RisolviCella) senza condividere il calcolo fra piu' veicoli nello stesso giro.
        var swPrima = Stopwatch.StartNew();
        var trovatiPrima = 0;
        foreach (var v in veicoli)
            trovatiPrima += ValutazioneRequisitoEcuVeicoloService
                .CandidatiPropagazione(requisitoId, componente, v, testDellaCoppia, valutazioniVuote).Count;
        swPrima.Stop();

        // "Dopo" il fix: un solo passaggio su testDellaCoppia per l'intera coppia Componente×Requisito (come fa ora
        // CalcolaKpiComplessivi con la sua cache per (Componente, RequisitoId)), poi lookup O(1) per ciascun veicolo.
        var swDopo = Stopwatch.StartNew();
        var direttiPerVeicolo = AuditMatriceService.CandidatiDirettiPerVeicolo(requisitoId, testDellaCoppia);
        var trovatiDopo = 0;
        foreach (var v in veicoli)
            trovatiDopo += ValutazioneRequisitoEcuVeicoloService
                .CandidatiPropagazione(componente, v, direttiPerVeicolo, valutazioniVuote).Count;
        swDopo.Stop();

        _output.WriteLine($"BENCHMARK|veicoli={numeroVeicoli}|test={numeroTest}|prima_ms={swPrima.Elapsed.TotalMilliseconds:F1}|dopo_ms={swDopo.Elapsed.TotalMilliseconds:F1}|trovati_prima={trovatiPrima}|trovati_dopo={trovatiDopo}|rapporto={(swDopo.Elapsed.TotalMilliseconds < 0.001 ? double.NaN : swPrima.Elapsed.TotalMilliseconds / swDopo.Elapsed.TotalMilliseconds):F1}x");

        Assert.Equal(trovatiPrima, trovatiDopo); // stesso risultato, solo il costo cambia
        Assert.Equal(numeroVeicoli * (numeroVeicoli - 1), trovatiDopo); // ogni veicolo trova tutti gli altri come propagabili (stesso punteggio ovunque)

        // Soglia generosa e relativa (macchina condivisa, mai un confronto assoluto in ms): la versione
        // precalcolata deve restare nettamente piu' veloce della scansione ripetuta O(veicoli) volte.
        Assert.True(swDopo.Elapsed.TotalMilliseconds * 3 < swPrima.Elapsed.TotalMilliseconds,
            $"Atteso un netto miglioramento (>=3x): prima={swPrima.Elapsed.TotalMilliseconds:F1}ms, dopo={swDopo.Elapsed.TotalMilliseconds:F1}ms");
    }
}
