using TestLoom.App.Models;

namespace TestLoom.App.Services;

public class CoverageBucket
{
    public long Conformi { get; set; }
    public long ConformiConDeviazioni { get; set; }
    public long NonConformi { get; set; }
    public long NonTestati { get; set; }
    public long Totale => checked(Conformi + ConformiConDeviazioni + NonConformi + NonTestati);

    /// <summary>% completamento a 4 bucket: Conforme con deviazioni conta come completato ma resta separato.</summary>
    public double PercentualeCompletamento => Totale == 0 ? 0 : 100.0 * (Conformi + ConformiConDeviazioni) / Totale;
}

/// <summary>
/// Calcola coperture su requisiti, funzionalita, template ed componente. I requisiti Standard sono cross-sottoveicolo;
/// ogni requisito Extra genera invece una unita di copertura per ciascun sottoveicolo a cui e applicabile.
/// </summary>
public class CoverageService
{
    public static Esito? EsitoCorrente(IEnumerable<TestCase> testCasesSuTarget)
    {
        Esecuzione? piuRecente = null;
        foreach (var tc in testCasesSuTarget)
        {
            if (tc.Esecuzioni.Count == 0) continue;
            var e = tc.Esecuzioni[^1];
            if (piuRecente == null || e.Data > piuRecente.Data)
                piuRecente = e;
        }
        return piuRecente?.Esito;
    }

    public static CoverageBucket PerRequisito(IReadOnlyList<Requisito> requisiti, IReadOnlyList<TestCase> testCases, bool soloBanco, bool soloVeicolo)
    {
        var testPerRequisito = new Dictionary<string, List<TestCase>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
        foreach (var reqId in tc.RequisitoId)
        {
            if (!testPerRequisito.TryGetValue(reqId, out var lista)) testPerRequisito[reqId] = lista = new List<TestCase>();
            lista.Add(tc);
        }

        var bucket = new CoverageBucket();
        foreach (var req in requisiti.Where(r => !r.Archiviata))
        {
            if (soloBanco && !req.ApplicabileBanco) continue;
            if (soloVeicolo && !req.ApplicabileVeicolo) continue;

            var testDelRequisito = testPerRequisito.GetValueOrDefault(req.Id) ?? (IReadOnlyList<TestCase>)Array.Empty<TestCase>();
            if (!RequisitoApplicabilita.ED4D(req))
            {
                // Standard: una sola unita cross-sottoveicolo, comportamento storico invariato.
                Accumula(bucket, EsitoCorrente(testDelRequisito));
                continue;
            }

            var targets = RequisitoApplicabilita.SottoveicoliDi(req);
            if (targets.Count == 0)
            {
                // 6.5.0: sottoveicoli facoltativi. Extra senza sottoveicoli = applicabile a tutti: una sola unita
                // cross-sottoveicolo, come uno Standard (nessun buco di copertura).
                Accumula(bucket, EsitoCorrente(testDelRequisito));
                continue;
            }

            // Extra: la copertura e specifica. Un requisito su A+B rappresenta due unita distinte e un Test
            // contribuisce soltanto al/ai sottoveicoli che dichiara realmente in VeicoliEffettivi.
            foreach (var sottoveicolo in targets)
            {
                var candidati = testDelRequisito.Where(tc =>
                    tc.VeicoliEffettivi.Contains(sottoveicolo, StringComparer.OrdinalIgnoreCase));
                Accumula(bucket, EsitoCorrente(candidati));
            }
        }
        return bucket;
    }

    public static CoverageBucket PerFunzionalita(IReadOnlyList<Funzionalita> funzionalita, IReadOnlyList<TestCase> testCases)
    {
        var testPerFunz = new Dictionary<string, List<TestCase>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tc in testCases)
        foreach (var nome in tc.Funzionalita)
        {
            if (!testPerFunz.TryGetValue(nome, out var lista)) testPerFunz[nome] = lista = new List<TestCase>();
            lista.Add(tc);
        }

        var bucket = new CoverageBucket();
        foreach (var f in funzionalita.Where(f => !f.Archiviata))
        {
            var testDellaFunz = testPerFunz.GetValueOrDefault(f.Nome) ?? (IReadOnlyList<TestCase>)Array.Empty<TestCase>();
            Accumula(bucket, EsitoCorrentePerFunzionalita(f.Nome, testDellaFunz));
        }
        return bucket;
    }

    /// <summary>
    /// Valuta solo le operazioni della revisione piu recente taggate con la Funzionalita richiesta. Operazioni
    /// storiche completamente prive del tag ricadono sull'esito complessivo; dati moderni parzialmente taggati
    /// non fanno invece risultare coperta una funzionalita che non compare realmente nelle operazioni.
    /// </summary>
    private static Esito? EsitoCorrentePerFunzionalita(string nomeFunzionalita, IEnumerable<TestCase> testCasesSuTarget)
    {
        Esecuzione? piuRecente = null;
        foreach (var tc in testCasesSuTarget)
        {
            if (tc.Esecuzioni.Count == 0) continue;
            var e = tc.Esecuzioni[^1];
            if (piuRecente == null || e.Data > piuRecente.Data)
                piuRecente = e;
        }
        if (piuRecente == null) return null;
        var esecuzione = piuRecente;
        // Decisione utente 6.4.0, punto 1: solo le operazioni eseguite concorrono all'esito — altrimenti
        // "Da eseguire"/N.A. (gravità intermedia, vedi TestCase.GravitaEsito) potrebbero dominare un Conforme
        // realmente presente sulla stessa coppia Funzionalità/revisione.
        // 6.5.0 punto 7: le operazioni guasto (Condizione.Guasto) NON entrano mai nella copertura per Funzionalita, anche se hanno
        // pure una Funzionalita valorizzata (Condizione.Funzionalita + Guasto): l'editor lo segnala con un avviso
        // ("DtcAvvisoFunzionalitaIgnorata"), il conteggio qui resta volutamente escluso (decisione utente: sorgente unica Guasto).
        var effettiveNonDtc = esecuzione.CondizioniEffettive.Where(c => !c.RiguardaDtc).ToList();
        var condizioniDellaFunz = effettiveNonDtc
            .Where(c => c.Funzionalita == nomeFunzionalita && c.Esito.EEseguito()).ToList();
        if (condizioniDellaFunz.Count > 0) return Esecuzione.Peggiore(condizioniDellaFunz.Select(c => c.Esito));
        // Revisione fatta solo di operazioni guasto: nessun contributo alla Funzionalita (l'esito complessivo della
        // REV include le operazioni guasto e non deve ricadere qui).
        if (effettiveNonDtc.Count == 0 && esecuzione.Condizioni.Any(c => c.RiguardaDtc)) return null;
        if (!effettiveNonDtc.All(c => string.IsNullOrWhiteSpace(c.Funzionalita))) return null;
        // REV mista: esecuzione.Esito comprende le operazioni guasto e non va usato; si aggrega solo il non-guasto eseguito.
        if (esecuzione.Condizioni.Any(c => c.RiguardaDtc))
        {
            var eseguiti = effettiveNonDtc.Where(c => c.Esito.EEseguito()).Select(c => c.Esito).ToList();
            return eseguiti.Count > 0 ? Esecuzione.Peggiore(eseguiti) : null;
        }
        return esecuzione.Esito;
    }

    public static CoverageBucket PerTemplate(IReadOnlyList<TestCase> testCases, Guid testTemplateUid)
    {
        var bucket = new CoverageBucket();
        foreach (var tc in testCases.Where(tc => tc.TestTemplateUid == testTemplateUid))
            Accumula(bucket, tc.StatoCorrente);
        return bucket;
    }

    private static Esito? EsitoCorrentePerEcu(string componente, IEnumerable<TestCase> testCasesSuTarget)
    {
        Esecuzione? piuRecente = null;
        foreach (var tc in testCasesSuTarget)
        {
            if (tc.Esecuzioni.Count == 0) continue;
            var e = tc.Esecuzioni[^1];
            if (piuRecente == null || e.Data > piuRecente.Data)
                piuRecente = e;
        }
        if (piuRecente == null) return null;
        var esecuzione = piuRecente;
        var condizioniDellEcu = esecuzione.CondizioniEffettive
            .Where(c => c.Componente.Contains(componente, StringComparer.OrdinalIgnoreCase) && c.Esito.EEseguito()).ToList();
        if (condizioniDellEcu.Count > 0) return Esecuzione.Peggiore(condizioniDellEcu.Select(c => c.Esito));
        return esecuzione.CondizioniEffettive.All(c => c.Componente.Count == 0) ? esecuzione.Esito : null;
    }

    public static CoverageBucket PerEcu(IReadOnlyList<TestCase> testCases, string componente)
    {
        var bucket = new CoverageBucket();
        foreach (var tc in testCases.Where(tc => tc.Componente.Contains(componente, StringComparer.OrdinalIgnoreCase)))
            Accumula(bucket, EsitoCorrentePerEcu(componente, new[] { tc }));
        return bucket;
    }

    public static CoverageBucket PerEcuFunzionalita(IReadOnlyList<TestCase> testCases, string componente, string funzionalita)
    {
        var bucket = new CoverageBucket();
        foreach (var tc in testCases.Where(tc => tc.Componente.Contains(componente, StringComparer.OrdinalIgnoreCase) && tc.Funzionalita.Contains(funzionalita)))
        {
            var ultima = tc.Esecuzioni.Count > 0 ? tc.Esecuzioni[^1] : null;
            var condizioni = ultima?.CondizioniEffettive
                .Where(c => !c.RiguardaDtc && c.Componente.Contains(componente, StringComparer.OrdinalIgnoreCase) && c.Funzionalita == funzionalita && c.Esito.EEseguito())
                .ToList();
            Accumula(bucket, condizioni is { Count: > 0 } ? Esecuzione.Peggiore(condizioni.Select(c => c.Esito)) : null);
        }
        return bucket;
    }

    public static PunteggioConformita? PunteggioCorrentePerRequisito(string requisitoId, IReadOnlyList<TestCase> testCases)
    {
        Esecuzione? piuRecente = null;
        PunteggioConformita? valore = null;
        foreach (var tc in testCases)
        {
            if (!RequisitoIdConfronto.Contiene(tc.RequisitoId, requisitoId)) continue;
            foreach (var e in tc.Esecuzioni)
            {
                foreach (var p in e.PunteggiRequisiti)
                {
                    if (RequisitoIdConfronto.Uguali(p.RequisitoId, requisitoId))
                    {
                        if (piuRecente == null || e.Data > piuRecente.Data)
                        {
                            piuRecente = e;
                            valore = p.Valore;
                        }
                        break;
                    }
                }
            }
        }
        return valore;
    }

    public static PunteggioConformita? PunteggioCorrentePerRequisito(
        Requisito requisito,
        string? sottoveicolo,
        IReadOnlyList<TestCase> testCases)
    {
        IEnumerable<TestCase> candidati = testCases.Where(tc => RequisitoIdConfronto.Contiene(tc.RequisitoId, requisito.Id));
        if (RequisitoApplicabilita.ED4D(requisito))
        {
            if (string.IsNullOrWhiteSpace(sottoveicolo) || !RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, sottoveicolo))
                return null;
            candidati = candidati.Where(tc => tc.VeicoliEffettivi.Contains(sottoveicolo, StringComparer.OrdinalIgnoreCase));
        }
        return PunteggioCorrentePerRequisito(requisito.Id, candidati.ToList());
    }

    private static void Accumula(CoverageBucket bucket, Esito? esito)
    {
        switch (esito)
        {
            case Esito.Conforme: bucket.Conformi++; break;
            case Esito.ConformeConDeviazioni: bucket.ConformiConDeviazioni++; break;
            case Esito.NonConforme: bucket.NonConformi++; break;
            default: bucket.NonTestati++; break;
        }
    }
}
