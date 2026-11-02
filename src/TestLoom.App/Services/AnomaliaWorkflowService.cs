using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Regole condivise tra lista Test, editor Test e vista Anomalie. Centralizzarle evita che le tre
/// pagine interpretino in modo diverso quale sia la prova corrente o quando un'anomalia possa chiudersi.</summary>
public static class AnomaliaWorkflowService
{
    /// <summary>Costruisce una sola volta l'indice Test -> anomalie. Evita una scansione completa di tutte
    /// le anomalie per ciascuno dei 20.000 test mostrati nella lista. Con <paramref name="trovaTest"/> ogni
    /// aggancio per codice viene validato con <see cref="AppartieneAlTest"/> (codici riassegnati dopo una
    /// eliminazione definitiva); senza resta il comportamento storico per sola stringa.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<NonConformita>> IndicizzaPerTest(
        IEnumerable<NonConformita> anomalie, Func<string, TestCase?>? trovaTest = null, Func<Guid, TestCase?>? trovaUid = null)
    {
        var indice = new Dictionary<string, List<NonConformita>>(StringComparer.OrdinalIgnoreCase);
        foreach (var anomalia in anomalie)
        {
            var perUid = trovaUid == null ? new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase) :
                anomalia.ProveCollegate.Select(p => p.TestUid).Append(anomalia.TestUid)
                    .Where(u => u.HasValue).Select(u => trovaUid(u!.Value)).OfType<TestCase>()
                    .DistinctBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
            var testIds = anomalia.ProveCollegate.Select(p => p.TestId)
                .Append(anomalia.TestCaseId)
                .Concat(perUid.Keys)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var testId in testIds)
            {
                if (trovaTest != null && !AppartieneAlTest(anomalia, testId, trovaTest(testId) ?? perUid.GetValueOrDefault(testId))) continue;
                if (!indice.TryGetValue(testId, out var lista)) indice[testId] = lista = new();
                lista.Add(anomalia);
            }
        }
        return indice.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<NonConformita>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<NonConformita> PerTest(string testId, IEnumerable<NonConformita> anomalie, Func<string, TestCase?>? trovaTest = null)
    {
        var test = trovaTest?.Invoke(testId);
        var candidate = test != null ? anomalie.Where(n => AppartieneAlTest(n, testId, test))
            : anomalie.Where(n => n.TestCaseId == testId || n.ProveCollegate.Any(p => p.TestId == testId));
        if (trovaTest != null)
        {
            candidate = candidate.Where(n => AppartieneAlTest(n, testId, test));
        }
        return candidate.ToList();
    }

    /// <summary>Identità immutabile autorevole; un UID diverso non ripiega mai sul codice.
    /// I riferimenti legacy richiedono una CondizioneUid reale risolvibile nel Test attuale.
    /// Le date non provano l'identità e non vengono usate per confermare l'aggancio.</summary>
    public static bool AppartieneAlTest(NonConformita anomalia, string testId, TestCase? test)
    {
        if (test == null) return false;
        if (anomalia.TestUid.HasValue && anomalia.TestUid == test.Uid) return true;
        return anomalia.ProveCollegate.Any(p => ProvaAppartieneAlTest(test, ProvaConIdentita(anomalia, p)));
    }

    internal static RiferimentoProva ProvaConIdentita(NonConformita nc, RiferimentoProva p) =>
        p.TestUid.HasValue || !nc.TestUid.HasValue || !string.Equals(nc.TestCaseId, p.TestId, StringComparison.OrdinalIgnoreCase)
            ? p : new RiferimentoProva { TestId = p.TestId, TestUid = nc.TestUid,
                CondizioneUid = p.CondizioneUid, EsecuzioneIterazione = p.EsecuzioneIterazione };

    public static bool ProvaAppartieneAlTest(TestCase test, RiferimentoProva prova) =>
        prova.TestUid.HasValue ? prova.TestUid == test.Uid :
        string.Equals(prova.TestId, test.Id, StringComparison.OrdinalIgnoreCase) &&
        prova.CondizioneUid != Guid.Empty && RisolviProva(test, prova) != null;

    public static bool SegnalazioneAppartieneAlTest(TestCase test, SegnalazioneDaClassificare segnalazione) =>
        segnalazione.TestUid.HasValue ? segnalazione.TestUid == test.Uid :
        ProvaAppartieneAlTest(test, new RiferimentoProva { TestId = segnalazione.TestId,
            CondizioneUid = segnalazione.CondizioneUid, EsecuzioneIterazione = segnalazione.EsecuzioneIterazione });

    public static TestCase? RisolviTest(Guid? uid, string codice, IReadOnlyDictionary<string, TestCase> test)
    {
        var perCodice = test.GetValueOrDefault(codice);
        if (!uid.HasValue) return perCodice;
        return perCodice?.Uid == uid ? perCodice : test.Values.FirstOrDefault(t => t.Uid == uid);
    }

    /// <summary>Vero se l'anomalia e stata aperta prima che il test (con quel codice) fosse creato.</summary>
    public static bool PrecedenteAlTest(NonConformita anomalia, TestCase test) =>
        test.DataCreazione != default && anomalia.DataApertura != default && anomalia.DataApertura < test.DataCreazione;

    public static bool HaPolarion(TestCase test, IEnumerable<NonConformita> anomalie) =>
        test.TicketCollegati.Count > 0 || PerTest(test.Id, anomalie, _ => test).Any(n => n.CollegamentiPolarion.Count > 0);

    public static (Esecuzione Esecuzione, Condizione Condizione)? RisolviProva(TestCase test, RiferimentoProva prova)
    {
        if (prova.CondizioneUid == Guid.Empty ||
            (prova.TestUid.HasValue ? prova.TestUid != test.Uid :
                !string.Equals(prova.TestId, test.Id, StringComparison.OrdinalIgnoreCase))) return null;
        var revisioni = prova.EsecuzioneIterazione > 0
            ? test.Esecuzioni.Where(e => e.Iterazione == prova.EsecuzioneIterazione)
            : test.Esecuzioni.AsEnumerable().Reverse();
        foreach (var esecuzione in revisioni)
        {
            var condizione = esecuzione.Condizioni.FirstOrDefault(c => c.Uid == prova.CondizioneUid);
            if (condizione != null) return (esecuzione, condizione);
        }
        return null;
    }

    public static Guid Radice(Condizione condizione) => condizione.OperazioneOrigineUid ?? condizione.Uid;

    /// <summary>Restituisce l'ultimo tentativo noto della stessa operazione attraversando tutte le revisioni.
    /// Data e Iterazione ordinano le revisioni; l'indice ordina piu tentativi nella stessa revisione.</summary>
    public static (Esecuzione Esecuzione, Condizione Condizione)? UltimoTentativo(TestCase test, Guid radice)
    {
        var ultimo = test.Esecuzioni
            .SelectMany(e => e.Condizioni.Select((c, i) => new { Esecuzione = e, Condizione = c, Indice = i }))
            .Where(x => Radice(x.Condizione) == radice)
            .OrderBy(x => x.Esecuzione.Data)
            .ThenBy(x => x.Esecuzione.Iterazione)
            .ThenBy(x => x.Indice)
            .LastOrDefault();
        return ultimo == null ? null : (ultimo.Esecuzione, ultimo.Condizione);
    }

    public static TestCase? TestCorrente(NonConformita anomalia, IEnumerable<TestCase> test) =>
        TestCorrente(anomalia, test.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Sovraccarico su dizionario già costruito (perf, feedback utente 2026-08-21): la versione
    /// IEnumerable ricostruisce un Dictionary da tutti i test ad ogni chiamata, il che in un ciclo per riga
    /// (NonConformitaViewModel.Ricarica) diventa O(anomalie × test). Chi itera più anomalie deve costruire il
    /// dizionario una sola volta e passarlo qui.</summary>
    public static TestCase? TestCorrente(NonConformita anomalia, IReadOnlyDictionary<string, TestCase> tutti)
    {
        var candidati = new List<(TestCase Test, DateTime Data)>();
        foreach (var prova in anomalia.ProveCollegate)
        {
            var effettiva = ProvaConIdentita(anomalia, prova);
            var tc = RisolviTest(effettiva.TestUid, effettiva.TestId, tutti);
            if (tc == null) continue;
            var risolta = RisolviProva(tc, effettiva);
            if (risolta != null) candidati.Add((tc, risolta.Value.Esecuzione.Data));
        }
        if (candidati.Count > 0) return candidati.OrderByDescending(x => x.Data).First().Test;
        var perCodice = RisolviTest(anomalia.TestUid, anomalia.TestCaseId, tutti);
        // Un codice coincidente senza prova certa non conferma il collegamento.
        return perCodice != null && !AppartieneAlTest(anomalia, perCodice.Id, perCodice) ? null : perCodice;
    }

    /// <summary>Un'anomalia puo chiudersi solo se l'ultimo tentativo di ogni operazione/test esplicitamente
    /// collegato e Conforme. Restituisce anche motivi leggibili che spiegano perche resta aperta.</summary>
    public static IReadOnlyList<string> MotiviPerCuiRestaAperta(NonConformita anomalia, IEnumerable<TestCase> test) =>
        MotiviPerCuiRestaAperta(anomalia, test.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase));

    /// <summary>Sovraccarico su dizionario già costruito (stesso motivo di TestCorrente sopra, feedback utente
    /// 2026-09-17: apertura di un Test rifaceva il Dictionary da tutti i Test una volta per ogni anomalia
    /// collegata, invece che una sola volta per l'intero pannello — con 100k Test il costo diventava multi
    /// secondo anche per un test con una sola anomalia). Chi itera più anomalie deve costruire il dizionario
    /// una sola volta e passarlo qui.</summary>
    public static IReadOnlyList<string> MotiviPerCuiRestaAperta(NonConformita anomalia, IReadOnlyDictionary<string, TestCase> tutti)
    {
        var motivi = new List<string>();
        foreach (var gruppo in anomalia.ProveCollegate.Select(p => ProvaConIdentita(anomalia, p)).GroupBy(p => (p.TestId, p.TestUid)))
        {
            var tc = RisolviTest(gruppo.Key.TestUid, gruppo.Key.TestId, tutti);
            if (tc == null)
            {
                motivi.Add($"{gruppo.Key.TestId}: test non disponibile");
                continue;
            }
            if (gruppo.Any(p => RisolviProva(tc, p) == null))
            {
                // Prova di un test omonimo cancellato (codice riassegnato): non e una prova di questo test e non
                // deve far risultare chiudibile l'anomalia in base agli esiti di un altro test.
                motivi.Add($"{gruppo.Key}: prova non appartenente al test attuale");
                continue;
            }
            foreach (var radice in gruppo.Select(p => RisolviProva(tc, p))
                         .Where(x => x != null).Select(x => Radice(x!.Value.Condizione)).Distinct())
            {
                var ultimo = UltimoTentativo(tc, radice);
                if (ultimo == null)
                {
                    motivi.Add($"{tc.Id}: prova non disponibile");
                    continue;
                }
                if (ultimo.Value.Condizione.Esito != Esito.Conforme)
                    motivi.Add($"{tc.Id} · REV {ultimo.Value.Esecuzione.Iterazione}: {ultimo.Value.Condizione.Esito.Label()}");
            }
        }
        return motivi.Distinct().ToList();
    }

    public static bool PuoChiudersiPerConformita(NonConformita anomalia, IEnumerable<TestCase> test) =>
        anomalia.ProveCollegate.Count > 0 && MotiviPerCuiRestaAperta(anomalia, test).Count == 0;

    public static bool PuoChiudersiPerConformita(NonConformita anomalia, IReadOnlyDictionary<string, TestCase> tutti) =>
        anomalia.ProveCollegate.Count > 0 && MotiviPerCuiRestaAperta(anomalia, tutti).Count == 0;
}
