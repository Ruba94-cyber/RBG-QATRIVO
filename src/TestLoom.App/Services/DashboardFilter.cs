using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Filtri della dashboard Home (Addendum §17.8), estratti in una classe pura e testabile — stesso
/// principio di ReportFilter — invece di restare inline nel ViewModel, dove non sarebbero verificabili da un
/// test automatico.</summary>
public class DashboardFilter
{
    // Liste vuote = nessun filtro su quel campo (Addendum §9, feedback QA: prima un solo valore, ora
    // selezione multipla — un elenco vuoto continua a significare "tutti", coerente col comportamento
    // precedente quando il singolo filtro era null).
    public List<string> Gamma { get; set; } = new();
    public List<string> Veicolo { get; set; } = new();
    public List<string> Componente { get; set; } = new();
    public List<string> Funzionalita { get; set; } = new();
    public List<string> CategoriaFunzionalita { get; set; } = new();
    public List<string> Requisito { get; set; } = new();
    public List<string> Strumento { get; set; } = new();
    public List<string> Operatore { get; set; } = new();
    public List<string> Assegnatario { get; set; } = new();
    public List<Guid> TestTemplateUid { get; set; } = new();
    public List<Esito> Esito { get; set; } = new();
    public DateTime? DataDa { get; set; }
    public DateTime? DataA { get; set; }

    /// <summary>Filtro sui campi custom del catalogo (Ruolo=Nessuno): chiave = CampoSistemaDefinizione.Chiave,
    /// valore = valori accettati per TestCase.CampiCustom[chiave] (Addendum: grill-me 2026-08-18, seconda
    /// follow-up — "includilo ora" invece di rimandarlo a un giro successivo). Lista vuota = nessun filtro su
    /// quella chiave, stessa convenzione degli altri filtri qui sopra.</summary>
    public Dictionary<string, List<string>> CampiCustom { get; set; } = new();

    /// <summary>Filtro TIME (6.6.1) sulla durata delle operazioni (almeno un'operazione eseguita che soddisfa il criterio). Null = nessun filtro.</summary>
    public FiltroDurataMinuti? TempoOperazione { get; set; }

    /// <summary>Filtro TIME (6.6.1) sul totale minuti del Test. Null = nessun filtro.</summary>
    public FiltroDurataMinuti? TempoTotaleTest { get; set; }

    public bool HaFiltriAttivi =>
        Gamma.Count > 0 || Veicolo.Count > 0 || Componente.Count > 0 || Funzionalita.Count > 0 || CategoriaFunzionalita.Count > 0 ||
        Requisito.Count > 0 || Strumento.Count > 0 || Operatore.Count > 0 || Assegnatario.Count > 0 ||
        TestTemplateUid.Count > 0 || Esito.Count > 0 || DataDa != null || DataA != null ||
        CampiCustom.Values.Any(v => v.Count > 0) || TempoOperazione != null || TempoTotaleTest != null;

    /// <summary>gammaDi risolve il nome Gamma di un Veicolo, categorieDiFunzionalita le Categorie di una
    /// Funzionalità — può averne più di una (feedback utente 2026-08-24) — (lookup esterni, non
    /// responsabilità del filtro).</summary>
    public bool Corrisponde(TestCase tc, Func<string, string?> gammaDi, Func<string, IEnumerable<string>>? categorieDiFunzionalita = null) =>
        (Gamma.Count == 0 || tc.VeicoliEffettivi.Any(v => gammaDi(v) is { } gamma && Gamma.Contains(gamma))) &&
        (Veicolo.Count == 0 || tc.VeicoliEffettivi.Any(Veicolo.Contains)) &&
        (Componente.Count == 0 || tc.Componente.Any(Componente.Contains)) &&
        (Funzionalita.Count == 0 || tc.Funzionalita.Any(Funzionalita.Contains)) &&
        (CategoriaFunzionalita.Count == 0 || categorieDiFunzionalita == null ||
            tc.Funzionalita.Any(f => categorieDiFunzionalita(f).Any(cat => CategoriaFunzionalita.Contains(cat)))) &&
        (Requisito.Count == 0 || tc.RequisitoId.Any(Requisito.Contains)) &&
        (Strumento.Count == 0 || tc.StrumentiUsati.Any(Strumento.Contains)) &&
        (Operatore.Count == 0 || tc.Esecuzioni.Any(e => OperationalSemantics.EsecuzioneAttribuibileAOperatore(e) && Operatore.Contains(e.Operatore))) &&
        (Assegnatario.Count == 0 || (tc.Assegnatario != null && Assegnatario.Contains(tc.Assegnatario))) &&
        (TestTemplateUid.Count == 0 || TestTemplateUid.Contains(tc.TestTemplateUid)) &&
        (Esito.Count == 0 || (tc.StatoCorrente.HasValue && Esito.Contains(tc.StatoCorrente.Value))) &&
        (DataDa == null || tc.DataCreazione >= DataDa.Value) &&
        (DataA == null || tc.DataCreazione < DataAInclusiva.FineEsclusa(DataA.Value)) &&
        CampiCustom.All(kv => kv.Value.Count == 0 || (tc.CampiCustom.TryGetValue(kv.Key, out var valore) && kv.Value.Contains(valore, StringComparer.OrdinalIgnoreCase))) &&
        (TempoOperazione == null || ReportMatriceService.HaOperazioneConDurata(tc, TempoOperazione)) &&
        (TempoTotaleTest == null || TempoTotaleTest.Corrisponde(ReportMatriceService.TotaleMinutiTest(tc)));

    public List<TestCase> Applica(IEnumerable<TestCase> testCases, Func<string, string?> gammaDi, Func<string, IEnumerable<string>>? categorieDiFunzionalita = null)
    {
        if (!HaFiltriAttivi)
            return testCases as List<TestCase> ?? testCases.ToList();

        var hsGamma = Gamma.Count > 0 ? new HashSet<string>(Gamma, StringComparer.OrdinalIgnoreCase) : null;
        var hsVeicolo = Veicolo.Count > 0 ? new HashSet<string>(Veicolo, StringComparer.OrdinalIgnoreCase) : null;
        var hsEcu = Componente.Count > 0 ? new HashSet<string>(Componente, StringComparer.OrdinalIgnoreCase) : null;
        var hsFunz = Funzionalita.Count > 0 ? new HashSet<string>(Funzionalita, StringComparer.OrdinalIgnoreCase) : null;
        var hsCat = CategoriaFunzionalita.Count > 0 ? new HashSet<string>(CategoriaFunzionalita, StringComparer.OrdinalIgnoreCase) : null;
        var hsReq = Requisito.Count > 0 ? new HashSet<string>(Requisito, StringComparer.OrdinalIgnoreCase) : null;
        var hsStr = Strumento.Count > 0 ? new HashSet<string>(Strumento, StringComparer.OrdinalIgnoreCase) : null;
        var hsOp = Operatore.Count > 0 ? new HashSet<string>(Operatore, StringComparer.OrdinalIgnoreCase) : null;
        var hsAss = Assegnatario.Count > 0 ? new HashSet<string>(Assegnatario, StringComparer.OrdinalIgnoreCase) : null;
        var hsTpl = TestTemplateUid.Count > 0 ? new HashSet<Guid>(TestTemplateUid) : null;
        var hsEsito = Esito.Count > 0 ? new HashSet<Esito>(Esito) : null;
        var hsCustom = CampiCustom
            .Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase));

        var risultato = new List<TestCase>();
        foreach (var tc in testCases)
        {
            if (DataDa != null && tc.DataCreazione < DataDa.Value) continue;
            if (DataA != null && tc.DataCreazione >= DataAInclusiva.FineEsclusa(DataA.Value)) continue;
            if (hsEsito != null && (!tc.StatoCorrente.HasValue || !hsEsito.Contains(tc.StatoCorrente.Value))) continue;
            if (hsTpl != null && !hsTpl.Contains(tc.TestTemplateUid)) continue;
            if (hsAss != null && (tc.Assegnatario == null || !hsAss.Contains(tc.Assegnatario))) continue;
            if (hsVeicolo != null && !tc.VeicoliEffettivi.Any(hsVeicolo.Contains)) continue;
            if (hsEcu != null && !tc.Componente.Any(hsEcu.Contains)) continue;
            if (hsGamma != null && !tc.VeicoliEffettivi.Any(v => gammaDi(v) is { } g && hsGamma.Contains(g))) continue;
            if (hsFunz != null && !tc.Funzionalita.Any(hsFunz.Contains)) continue;
            if (hsCat != null && (categorieDiFunzionalita == null || !tc.Funzionalita.Any(f => categorieDiFunzionalita(f).Any(hsCat.Contains)))) continue;
            if (hsReq != null && !tc.RequisitoId.Any(hsReq.Contains)) continue;
            if (hsStr != null && !tc.StrumentiUsati.Any(hsStr.Contains)) continue;
            if (hsOp != null && !tc.Esecuzioni.Any(e => OperationalSemantics.EsecuzioneAttribuibileAOperatore(e) && hsOp.Contains(e.Operatore))) continue;
            if (hsCustom.Count > 0 && !hsCustom.All(kv => tc.CampiCustom.TryGetValue(kv.Key, out var valore) && kv.Value.Contains(valore))) continue;
            if (TempoOperazione is { } operazione && !ReportMatriceService.HaOperazioneConDurata(tc, operazione)) continue;
            if (TempoTotaleTest is { } totale && !totale.Corrisponde(ReportMatriceService.TotaleMinutiTest(tc))) continue;

            risultato.Add(tc);
        }
        return risultato;
    }
}
