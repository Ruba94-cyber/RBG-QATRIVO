using TestLoom.App.Models;

namespace TestLoom.App.Services;

public sealed record AnomalieQueryRichiesta(
    AnomalieFiltroNc Nc,
    AnomalieFiltroAmbito Ambito,
    AnomalieOrdinamento Ordinamento,
    long Pagina,
    int DimensionePagina);

/// <summary>Riga di risultato: <see cref="Nc"/> e' sempre una COPIA isolata (sicura da modificare/salvare).</summary>
public sealed record AnomalieRigaRisultato(NonConformita Nc, TestCase? TestCase, string TestCaseIdVisualizzato);

public sealed record AnomalieQueryRisultato(
    long Totale,
    long PaginaEffettiva,
    IReadOnlyList<AnomalieRigaRisultato> Righe,
    bool DaIndice);

/// <summary>
/// Esegue filtro + ordinamento + paginazione della lista Anomalie FUORI dal ViewModel (thread-safe, nessun
/// accesso a controlli), cosi' puo' girare su un thread di lavoro con annullamento cooperativo.
///
/// Strategia: i filtri propri della NC (stato, priorita', gravita', assegnatari, intervalli di data, presenza del
/// ticket TICKET collegato direttamente e ricerca libera) e l'ordinamento sono delegati all'indice SQLite locale
/// (LocalNonConformitaIndexStore) quando e' sano e allineato alla cache; i filtri d'ambito che dipendono dal Test
/// collegato (componente/veicolo/gamma/funzionalita', ticket del Test) si applicano in memoria sui soli candidati gia' filtrati e ordinati dall'indice. Se l'indice non e'
/// utilizzabile (degradato, in ricostruzione, cache non allineata, working-copy attiva, errore SQLite) si usa lo
/// STESSO insieme di predicati interamente in memoria: stessa semantica, solo piu' lento.
/// </summary>
public static class AnomalieQueryService
{
    private const int ControlloAnnullamentoOgni = 2048;

    public static AnomalieQueryRisultato Esegui(
        NonConformitaRepository repository,
        Func<string, TestCase?> trovaTest,
        AnomalieQueryRichiesta richiesta,
        bool consentiIndice = true,
        CancellationToken ct = default, Func<Guid, TestCase?>? trovaUid = null)
    {
        if (consentiIndice)
        {
            var daIndice = ProvaIndice(repository, trovaTest, richiesta, ct, trovaUid);
            if (daIndice != null) return daIndice;
        }
        return EseguiInMemoria(repository, trovaTest, richiesta, ct, trovaUid);
    }

    // ------------------------------------------------------------------ percorso indicizzato

    private static AnomalieQueryRisultato? ProvaIndice(
        NonConformitaRepository repository, Func<string, TestCase?> trovaTest, AnomalieQueryRichiesta r, CancellationToken ct, Func<Guid, TestCase?>? trovaUid)
    {
        // Regole di instradamento basate sul benchmark a 100k NC (NonConformitaFiltriAvanzatiBenchmarkTests):
        //  - filtri d'ambito (dipendono dal Test collegato): sempre in memoria. Servirebbe esportare dall'indice
        //    TUTTI i percorsi candidati (centinaia di ms per 100k) solo per poi scartarne quasi tutti.
        //  - pagine molto profonde: OFFSET grande in SQLite costringe a ordinare offset+limite righe, molto piu'
        //    lento della selezione parziale in memoria.
        if (r.Ambito.Attivo || r.Nc.DurataMinima.HasValue) return null;
        // Test collegato, Descrizione breve, Ticket e Veicolo dipendono dal Test: l'indice non li conosce.
        if (AnomalieOrdinamentoChiaveEstensioni.RichiedeTest(r.Ordinamento.Chiave)) return null;
        var pagina = Math.Max(1L, r.Pagina);
        if ((pagina - 1) * r.DimensionePagina > OffsetMassimoIndice) return null;
        if (!repository.IndiceFiltriUtilizzabile) return null;
        ct.ThrowIfCancellationRequested();

        if (!repository.TryQueryIndicizzata(r.Nc, r.Ordinamento, (pagina - 1) * r.DimensionePagina, r.DimensionePagina, out var totale, out var nc))
            return null;
        var pagineTotali = PagineTotali(totale, r.DimensionePagina);
        if (pagina > pagineTotali)
        {
            pagina = pagineTotali;
            if (!repository.TryQueryIndicizzata(r.Nc, r.Ordinamento, (pagina - 1) * r.DimensionePagina, r.DimensionePagina, out totale, out nc))
                return null;
        }
        ct.ThrowIfCancellationRequested();
        return new AnomalieQueryRisultato(totale, pagina, CostruisciRighe(nc, trovaTest, trovaUid), true);
    }

    /// <summary>Oltre questo OFFSET (righe saltate) l'interrogazione dell'indice non conviene piu'.</summary>
    internal const int OffsetMassimoIndice = 10_000;

    // ------------------------------------------------------------------ ripiego in memoria

    private static AnomalieQueryRisultato EseguiInMemoria(
        NonConformitaRepository repository, Func<string, TestCase?> trovaTest, AnomalieQueryRichiesta r, CancellationToken ct, Func<Guid, TestCase?>? trovaUid)
    {
        var tutte = repository.PeekAllReadOnly();
        var comparer = AnomalieOrdinamentoComparer.Crea(r.Ordinamento);

        if (!r.Ambito.Attivo && !AnomalieOrdinamentoChiaveEstensioni.RichiedeTest(r.Ordinamento.Chiave))
        {
            var candidati = new List<NonConformita>();
            for (var i = 0; i < tutte.Count; i++)
            {
                if ((i & (ControlloAnnullamentoOgni - 1)) == 0) ct.ThrowIfCancellationRequested();
                if (r.Nc.Corrisponde(tutte[i])) candidati.Add(tutte[i]);
            }
            var pagina = Math.Min(Math.Max(1L, r.Pagina), PagineTotali(candidati.Count, r.DimensionePagina));
            // OrderBy+Skip+Take: selezione parziale, non un ordinamento completo dell'intero risultato.
            var fetta = candidati.OrderBy(x => x, comparer)
                .Skip(checked((int)((pagina - 1) * r.DimensionePagina))).Take(r.DimensionePagina).ToList();
            return new AnomalieQueryRisultato(candidati.Count, pagina, CostruisciRighe(fetta, trovaTest, trovaUid), false);
        }

        // Con filtri d'ambito si filtra PRIMA (propri della NC, poi Test/ambito) e si ordina solo dopo, sui
        // soli sopravvissuti: ordinare prima 100k candidati per poi scartarne la quasi totalita' e' puro spreco.
        var senzaTesto = new List<NonConformita>();
        var nc = r.Nc.SenzaTesto();
        for (var i = 0; i < tutte.Count; i++)
        {
            if ((i & (ControlloAnnullamentoOgni - 1)) == 0) ct.ThrowIfCancellationRequested();
            if (nc.Corrisponde(tutte[i])) senzaTesto.Add(tutte[i]);
        }
        var filtrate = FiltraPerAmbito(senzaTesto, trovaTest, r, ct, trovaUid);
        if (AnomalieOrdinamentoChiaveEstensioni.RichiedeTest(r.Ordinamento.Chiave))
            filtrate.Sort(AnomalieOrdinamentoConTestComparer.Crea(r.Ordinamento));
        else
            filtrate.Sort((a, b) => comparer.Compare(a.Nc, b.Nc));
        return Impagina(filtrate, r, daIndice: false);
    }

    // ------------------------------------------------------------------ comune

    private static List<(NonConformita Nc, TestCase? Test, string TestId)> FiltraPerAmbito(
        IReadOnlyList<NonConformita> candidati, Func<string, TestCase?> trovaTest, AnomalieQueryRichiesta r, CancellationToken ct, Func<Guid, TestCase?>? trovaUid)
    {
        var idTest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in candidati)
        {
            if (!string.IsNullOrWhiteSpace(n.TestCaseId)) idTest.Add(n.TestCaseId);
            for (var i = 0; i < n.ProveCollegate.Count; i++)
                if (!string.IsNullOrWhiteSpace(n.ProveCollegate[i].TestId)) idTest.Add(n.ProveCollegate[i].TestId);
        }
        var testPerId = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in idTest)
        {
            ct.ThrowIfCancellationRequested();
            var tc = trovaTest(id);
            if (tc != null) testPerId[id] = tc;
        }

        AggiungiIdentita(candidati, testPerId, trovaUid);
        var qTesto = r.Nc.HaTesto ? r.Nc.Testo!.Trim() : null;
        var risultato = new List<(NonConformita, TestCase?, string)>(candidati.Count);
        for (var i = 0; i < candidati.Count; i++)
        {
            if ((i & (ControlloAnnullamentoOgni - 1)) == 0) ct.ThrowIfCancellationRequested();
            var nc = candidati[i];
            var tc = AnomaliaWorkflowService.TestCorrente(nc, testPerId);
            var testIdVis = tc?.Id ?? nc.TestCaseId;
            if (qTesto != null && !AnomalieFiltroNc.TestoCorrisponde(nc, qTesto, testIdVis)) continue;
            if (!r.Ambito.Corrisponde(nc, tc)) continue;
            risultato.Add((nc, tc, testIdVis));
        }
        return risultato;
    }

    private static AnomalieQueryRisultato Impagina(
        List<(NonConformita Nc, TestCase? Test, string TestId)> filtrate, AnomalieQueryRichiesta r, bool daIndice)
    {
        var pagina = Math.Min(Math.Max(1L, r.Pagina), PagineTotali(filtrate.Count, r.DimensionePagina));
        var offset = checked((int)((pagina - 1) * r.DimensionePagina));
        var righe = filtrate.Skip(offset).Take(r.DimensionePagina)
            .Select(x => new AnomalieRigaRisultato(x.Nc.Clona(), x.Test, x.TestId)).ToList();
        return new AnomalieQueryRisultato(filtrate.Count, pagina, righe, daIndice);
    }

    /// <summary>Clona solo le righe della pagina finale (mai l'intero risultato) e risolve il Test corrente solo
    /// per quelle.</summary>
    private static List<AnomalieRigaRisultato> CostruisciRighe(IReadOnlyList<NonConformita> pagina, Func<string, TestCase?> trovaTest, Func<Guid, TestCase?>? trovaUid)
    {
        var clonate = pagina.Select(n => n.Clona()).ToList();
        var idTest = clonate
            .SelectMany(n => n.ProveCollegate.Select(p => p.TestId).Append(n.TestCaseId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var testPerId = new Dictionary<string, TestCase>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in idTest)
        {
            var tc = trovaTest(id);
            if (tc != null) testPerId[id] = tc;
        }
        AggiungiIdentita(clonate, testPerId, trovaUid);
        return clonate.Select(nc =>
        {
            var tc = AnomaliaWorkflowService.TestCorrente(nc, testPerId);
            return new AnomalieRigaRisultato(nc, tc, tc?.Id ?? nc.TestCaseId);
        }).ToList();
    }

    private static void AggiungiIdentita(IEnumerable<NonConformita> nc, Dictionary<string, TestCase> test, Func<Guid, TestCase?>? trovaUid)
    {
        if (trovaUid == null) return;
        foreach (var uid in nc.SelectMany(n => n.ProveCollegate.Select(p => p.TestUid).Append(n.TestUid)).Where(u => u.HasValue).Select(u => u!.Value).Distinct())
        {
            var current = trovaUid(uid);
            if (current != null) test[current.Id] = current;
        }
    }

    public static long PagineTotali(long totale, int dimensione) => Math.Max(1L, (totale + dimensione - 1L) / dimensione);
}
