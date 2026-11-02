using TestLoom.App.Models;

namespace TestLoom.App.Services;

public class KpiIterazioniConforme
{
    public double MediaIterazioni { get; set; }
    public long CasiRaggiuntiConforme { get; set; }
    public long CasiMaiRaggiuntiConforme { get; set; }
    public double PercentualeMaiRaggiunti =>
        (CasiRaggiuntiConforme + CasiMaiRaggiuntiConforme) == 0 ? 0
        : 100.0 * CasiMaiRaggiuntiConforme / (CasiRaggiuntiConforme + CasiMaiRaggiuntiConforme);
}

/// <summary>KPI "iterazioni per arrivare a Conforme" (§8). Esclude dalla media i test case che non hanno mai
/// raggiunto Conforme (es. NC chiusa manualmente per accettazione del rischio, senza retest): la media resta
/// significativa, e i casi esclusi sono comunque tracciati e mostrati separatamente (Addendum §16.5).</summary>
public static class KpiService
{
    /// <summary>Decisione utente 6.4.8, punto 5: le iterazioni sono solo le REV realmente eseguite
    /// (OperationalSemantics.EsecuzioneEseguita); una REV di sola pianificazione non e' un'iterazione.</summary>
    public static int? IterazioniPerConforme(TestCase tc) => OperationalSemantics.IterazioniEseguitePerConforme(tc);

    public static KpiIterazioniConforme CalcolaAggregato(IEnumerable<TestCase> testCases)
    {
        var conclusi = testCases.Where(tc => OperationalSemantics.TestTestato(tc)).ToList();
        var iterazioni = conclusi.Select(IterazioniPerConforme).Where(i => i.HasValue).Select(i => i!.Value).ToList();
        var maiRaggiunti = conclusi.LongCount(tc => IterazioniPerConforme(tc) == null);

        return new KpiIterazioniConforme
        {
            MediaIterazioni = iterazioni.Count == 0 ? 0 : iterazioni.Average(),
            CasiRaggiuntiConforme = iterazioni.LongCount(),
            CasiMaiRaggiuntiConforme = maiRaggiunti
        };
    }
}
