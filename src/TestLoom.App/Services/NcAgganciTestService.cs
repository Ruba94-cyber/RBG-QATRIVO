using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum MotivoAgganciaIncoerente
{
    /// <summary>Nessuna prova della NC e risolvibile nel test e la NC e precedente alla creazione del test:
    /// quasi certamente il codice test e stato riassegnato dopo una eliminazione definitiva. Questa NC non
    /// viene piu agganciata al test (AnomaliaWorkflowService.AppartieneAlTest).</summary>
    NcPrecedenteAlTest,
    /// <summary>Nessuna prova è risolvibile nel test: codice ambiguo, conservato senza aggancio automatico.</summary>
    ProvaNonRisolvibile,
    TestNonDisponibile,
    IdentitaDifferente,
}

public sealed record NcAgganciataIncoerente(string NcId, Guid NcUid, StatoAnomalia Stato, string TestId, MotivoAgganciaIncoerente Motivo);

public sealed record NcReferenziaTest(string NcId, StatoAnomalia Stato);

/// <summary>Letture pure (nessuna scrittura, nessuna modifica delle NC) su come le NC si agganciano ai Test per
/// codice: diagnostica degli agganci incoerenti e elenco delle NC che bloccherebbero/avvertirebbero
/// l'eliminazione definitiva di un test.</summary>
public static class NcAgganciTestService
{
    public static IReadOnlyList<NcAgganciataIncoerente> ElencaAgganciIncoerenti(
        IEnumerable<NonConformita> anomalie, Func<string, TestCase?> trovaTest, Func<Guid, TestCase?>? trovaUid = null)
    {
        var cache = new Dictionary<(string Code, Guid? Uid), TestCase?>();
        var risultato = new List<NcAgganciataIncoerente>();
        foreach (var nc in anomalie)
        {
            var references = nc.ProveCollegate.Select(p => AnomaliaWorkflowService.ProvaConIdentita(nc, p)).ToList();
            if (!string.IsNullOrWhiteSpace(nc.TestCaseId) || nc.TestUid.HasValue)
                references.Add(new RiferimentoProva { TestId = nc.TestCaseId, TestUid = nc.TestUid });
            foreach (var group in references.GroupBy(p => (p.TestId, p.TestUid)))
            {
                var (testId, uid) = group.Key;
                if (string.IsNullOrWhiteSpace(testId) && !uid.HasValue) continue;
                if (!cache.TryGetValue(group.Key, out var test))
                    cache[group.Key] = test = uid.HasValue && trovaUid != null ? trovaUid(uid.Value) : trovaTest(testId);
                MotivoAgganciaIncoerente? reason = test == null ? MotivoAgganciaIncoerente.TestNonDisponibile :
                    uid.HasValue && uid != test.Uid ? MotivoAgganciaIncoerente.IdentitaDifferente : null;
                if (reason == null && !uid.HasValue && !group.Any(p => AnomaliaWorkflowService.RisolviProva(test!, p) != null))
                    reason = AnomaliaWorkflowService.PrecedenteAlTest(nc, test!) ? MotivoAgganciaIncoerente.NcPrecedenteAlTest : MotivoAgganciaIncoerente.ProvaNonRisolvibile;
                if (reason.HasValue) risultato.Add(new(nc.Id, nc.Uid, nc.StatoWorkflow, testId, reason.Value));
            }
        }
        return risultato.OrderBy(r => r.NcId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Tutte le NC (aperte e chiuse) che citano il codice test, senza validazione di appartenenza:
    /// e cio che resterebbe orfano eliminando definitivamente il test.</summary>
    public static IReadOnlyList<NcReferenziaTest> NcCheCitanoIlTest(string testId, IEnumerable<NonConformita> anomalie, Guid? testUid = null) =>
        anomalie.Where(n => (testUid.HasValue && n.TestUid.HasValue ? n.TestUid == testUid :
                                string.Equals(n.TestCaseId, testId, StringComparison.OrdinalIgnoreCase)) ||
                            n.ProveCollegate.Select(p => AnomaliaWorkflowService.ProvaConIdentita(n, p)).Any(p =>
                                testUid.HasValue && p.TestUid.HasValue ? p.TestUid == testUid :
                                    string.Equals(p.TestId, testId, StringComparison.OrdinalIgnoreCase)))
            .Select(n => new NcReferenziaTest(n.Id, n.StatoWorkflow))
            .OrderBy(n => n.NcId, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Testo localizzato di avviso per l'eliminazione definitiva, o null se nessuna NC/Segnalazione
    /// cita il test. Non cancella nulla.</summary>
    public static string? AvvisoEliminazioneDefinitiva(string testId,
        IEnumerable<NonConformita> anomalie, IEnumerable<SegnalazioneDaClassificare> segnalazioni, Guid? testUid = null)
    {
        var nc = NcCheCitanoIlTest(testId, anomalie, testUid);
        var nSegnalazioni = segnalazioni.Count(s => testUid.HasValue && s.TestUid.HasValue ? s.TestUid == testUid :
            string.Equals(s.TestId, testId, StringComparison.OrdinalIgnoreCase));
        if (nc.Count == 0 && nSegnalazioni == 0) return null;
        var loc = LocalizationService.Instance;
        const int massimo = 15;
        var elenco = string.Join("\n", nc.Take(massimo).Select(n => $"- {n.NcId} ({n.Stato.Label()})"));
        if (nc.Count > massimo) elenco += $"\n- … (+{nc.Count - massimo})";
        return string.Format(loc.Traduci("EliminaTestConNcAvvisoFmt"), testId, nc.Count, nSegnalazioni, elenco);
    }
}
