using TestLoom.App.Models;

namespace TestLoom.App.Services;

public class ReportFilter
{
    public List<string> Componente { get; set; } = new();
    public List<string> Veicoli { get; set; } = new();
    public DateTime? DataDa { get; set; }
    public DateTime? DataA { get; set; }
    public List<Guid> TemplateUid { get; set; } = new();

    /// <summary>Filtro sui campi custom del catalogo (Ruolo=Nessuno): chiave = CampoSistemaDefinizione.Chiave,
    /// valore = valori accettati per TestCase.CampiCustom[chiave] (Addendum: grill-me 2026-08-18, seconda
    /// follow-up — "includilo ora" invece di rimandarlo a un giro successivo). Lista vuota = nessun filtro su
    /// quella chiave, stessa convenzione degli altri filtri qui sopra.</summary>
    public Dictionary<string, List<string>> CampiCustom { get; set; } = new();

    /// <summary>Filtro TIME (6.6.1) sulla durata delle operazioni: il Test ha almeno un'operazione eseguita con durata che soddisfa
    /// il criterio (&gt; o &lt; X minuti). Null = nessun filtro. Un'operazione senza durata non corrisponde mai.</summary>
    public FiltroDurataMinuti? TempoOperazione { get; set; }

    /// <summary>Filtro TIME (6.6.1) sul totale minuti del Test (somma delle operazioni eseguite con durata nota). Null = nessun filtro.</summary>
    public FiltroDurataMinuti? TempoTotaleTest { get; set; }

    /// <summary>Criteri della sola Home (6.7.1, Stato %): operatore che ha eseguito una REV e strumento usato. Il Report non li
    /// offre come filtro di pagina: vuoti = nessun filtro. Sono criteri non di identita' (spengono l'universo "Non testato").</summary>
    public List<string> Operatore { get; set; } = new();
    public List<string> Strumento { get; set; } = new();

    /// <summary>Riepilogo leggibile dei filtri attivi (intestazione del foglio "Grafici" e titolo delle card): "nessun filtro" se vuoto.
    /// I campi custom si mostrano con l'Etichetta del catalogo (<paramref name="etichetteCampi"/>, chiave = Chiave), mai con la chiave
    /// interna; senza etichetta nota ricade sulla chiave.</summary>
    public string Descrizione(IReadOnlyDictionary<string, string>? etichetteCampi = null)
    {
        var loc = LocalizationService.Instance;
        var parti = new List<string>();
        if (Componente.Count > 0) parti.Add($"{loc.Traduci("ColEcu")}: {string.Join(", ", Componente)}");
        if (Veicoli.Count > 0) parti.Add($"{loc.Traduci("SottoveicoloLabel")}: {string.Join(", ", Veicoli)}");
        if (TemplateUid.Count > 0) parti.Add($"{loc.Traduci("TipoTestLabel")}: {TemplateUid.Count}");
        if (DataDa.HasValue || DataA.HasValue)
            parti.Add($"{loc.Traduci("DataDaLabel")} {DataDa?.ToString("dd/MM/yyyy") ?? "—"} – {loc.Traduci("DataALabel")} {DataA?.ToString("dd/MM/yyyy") ?? "—"}");
        if (CampiCustom.Values.Any(v => v.Count > 0)) parti.Add(string.Join(", ", CampiCustom.Where(kv => kv.Value.Count > 0).Select(kv => $"{EtichettaCampo(kv.Key, etichetteCampi)}: {string.Join("/", kv.Value)}")));
        if (TempoOperazione is { } tempoOperazione) parti.Add($"{loc.Traduci("FiltroTempoOperazioneLabel")} {tempoOperazione.Etichetta}");
        if (TempoTotaleTest is { } tempoTotale) parti.Add($"{loc.Traduci("FiltroTempoTotaleTestLabel")} {tempoTotale.Etichetta}");
        return parti.Count == 0 ? loc.Traduci("GraficoNessunFiltro") : string.Join(" · ", parti);
    }

    private static string EtichettaCampo(string chiave, IReadOnlyDictionary<string, string>? etichetteCampi) =>
        etichetteCampi != null && etichetteCampi.TryGetValue(chiave, out var etichetta) && !string.IsNullOrWhiteSpace(etichetta)
            ? etichetta
            : chiave;

    /// <summary>Liste, anteprima, grafici ed elenco: tutti i criteri, TIME compresi a livello di Test (vedi <see cref="CorrispondeTempo"/>).</summary>
    public bool Corrisponde(TestCase tc) => CorrispondeSenzaTempo(tc) && CorrispondeTempo(tc);

    /// <summary>Matrice (Report, export Excel e PDF): criteri non TIME e totale del Test. Il filtro sulla durata delle operazioni non sta
    /// qui: la matrice lo applica per combinazione (Stato, Stato %) o per Test (Test %) e per tentativo (Tempo, base Tutte le esecuzioni).</summary>
    public bool CorrispondeMatrice(TestCase tc) => CorrispondeSenzaTempo(tc) && CorrispondeTempoTotale(tc);

    /// <summary>Criteri non TIME: componente, veicolo, tipo di Test, date, campi custom. L'indice li applica in SQL.</summary>
    public bool CorrispondeSenzaTempo(TestCase tc)
    {
        if (tc.Eliminato) return false;
        if (Componente.Count > 0 && !tc.Componente.Any(Componente.Contains)) return false;
        if (Veicoli.Count > 0 && !tc.VeicoliEffettivi.Any(Veicoli.Contains)) return false;
        if (TemplateUid.Count > 0 && !TemplateUid.Contains(tc.TestTemplateUid)) return false;
        if (DataDa.HasValue && tc.DataCreazione < DataDa.Value) return false;
        if (DataA.HasValue && tc.DataCreazione >= DataAInclusiva.FineEsclusa(DataA.Value)) return false;
        foreach (var (chiave, valoriAmmessi) in CampiCustom)
        {
            if (valoriAmmessi.Count == 0) continue;
            if (!tc.CampiCustom.TryGetValue(chiave, out var valore) || !valoriAmmessi.Contains(valore, StringComparer.OrdinalIgnoreCase)) return false;
        }
        if (Operatore.Count > 0 && !tc.Esecuzioni.Any(e => OperationalSemantics.EsecuzioneAttribuibileAOperatore(e) && Operatore.Contains(e.Operatore, StringComparer.OrdinalIgnoreCase))) return false;
        if (Strumento.Count > 0 && !tc.StrumentiUsati.Any(s => Strumento.Contains(s, StringComparer.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>Solo i filtri TIME: l'indice (proiezione) non li conosce, quindi il chiamante li applica dopo la lettura SQL. A livello di
    /// Test: almeno una operazione corrente effettiva corrisponde (vedi <see cref="ReportMatriceService.HaOperazioneConDurata"/>) e il
    /// totale del Test corrisponde.</summary>
    public bool CorrispondeTempo(TestCase tc) => CorrispondeTempoOperazione(tc) && CorrispondeTempoTotale(tc);

    /// <summary>Filtro "Tempo operazione" a livello di Test (liste, grafici, anteprima; Test % della matrice).</summary>
    public bool CorrispondeTempoOperazione(TestCase tc) =>
        TempoOperazione is not { } operazione || ReportMatriceService.HaOperazioneConDurata(tc, operazione);

    /// <summary>Filtro "Tempo totale Test": somma di tutti i tentativi eseguiti del Test, ripetizioni e guasto incluse (TotaleMinutiTest).</summary>
    public bool CorrispondeTempoTotale(TestCase tc) =>
        TempoTotaleTest is not { } totale || totale.Corrisponde(ReportMatriceService.TotaleMinutiTest(tc));

    /// <summary>Vero se almeno un filtro TIME e' attivo.</summary>
    public bool HaFiltroTempo => TempoOperazione != null || TempoTotaleTest != null;
}
