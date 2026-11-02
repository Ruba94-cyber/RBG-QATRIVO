using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Punto unico di calcolo dell'avanzamento guasto per le superfici dell'app (6.5.0, nessuna pagina dedicata):
/// pannello guasto del requisito Extra (Impostazioni), riepilogo nell'operazione del Test, sezione Extra dell'Audit e
/// export. Motore = DtcAvanzamentoService; con l'indice test locale v9 disponibile gli esiti arrivano in streaming
/// da SQLite (LocalDtcQueryService, opzionalmente limitati a UN requisito), altrimenti ripiega sul calcolo in
/// memoria. Pensato per essere chiamato da un thread di background (Task.Run), mai dal thread UI.</summary>
public static class DtcAvanzamentoProvider
{
    /// <summary>Calcola l'avanzamento per i requisiti dati (dettaglio per guasto). i guasti archiviati vengono letti per requisito
    /// dall'archivio (RequisitoDtcArchivio) solo per i requisiti passati. <paramref name="soloRequisitoId"/> restringe la
    /// lettura SQL esiti a quel requisito (pannello del singolo requisito: costo proporzionale al requisito).</summary>
    public static DtcAvanzamentoRisultato Calcola(AppState state, IEnumerable<Requisito> requisiti,
        string? soloRequisitoId = null, CancellationToken ct = default)
    {
        var reqs = Idrata(state, requisiti.ToList(), null, ct);
        return DtcAvanzamentoService.Calcola(reqs, LeggiEsiti(state, soloRequisitoId, ct));
    }

    /// <summary>Riepilogo k/n per requisito SENZA costruire il dettaglio per guasto: il denominatore viene dal conteggio dell'archivio
    /// (Requisito.DtcTotale); gli archivi si leggono solo per i requisiti che hanno almeno un esito guasto (gli altri sono tutti
    /// "da fare"). Pensato per la sezione Extra dell'Audit.</summary>
    public static DtcAvanzamentoRisultato CalcolaRiepilogo(AppState state, IEnumerable<Requisito> requisiti, CancellationToken ct = default)
    {
        var tutti = requisiti.ToList();
        state.Master.RequisitiDtc.Risolvi(tutti);
        var reqs = tutti.Where(r => !r.Archiviata && r.DtcTotale > 0).ToList();
        var esiti = LeggiEsiti(state, null, ct).ToList();
        var conEsiti = esiti.Select(e => (e.RequisitoId ?? "").Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var daIdratare = reqs.Where(r => conEsiti.Contains(r.Id.Trim())).ToList();
        var parziale = DtcAvanzamentoService.Calcola(Idrata(state, daIdratare, null, ct), esiti);
        var perReq = parziale.PerRequisito.ToList();
        var fatti = perReq.Select(r => r.RequisitoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reqs)
            if (!fatti.Contains(r.Id.Trim())) perReq.Add(new RequisitoDtcAvanzamento(r.Id.Trim(), r.DtcTotale, 0, 0, 0, r.DtcTotale));
        perReq.Sort((x, y) => NaturalStringComparer.Instance.Compare(x.RequisitoId, y.RequisitoId));
        var tot = new DtcRiepilogo(perReq.Sum(x => x.Totale), perReq.Sum(x => x.Completi), perReq.Sum(x => x.Validati),
            perReq.Sum(x => x.NonConformi), perReq.Sum(x => x.DaFare), perReq.Count, perReq.Count(x => x.Coperto));
        return new DtcAvanzamentoRisultato(Array.Empty<DtcAvanzamento>(), perReq, tot, parziale.CelleSenzaAnagrafica);
    }

    /// <summary>Proiezione di calcolo dei requisiti con i guasti letti dall'archivio (embedded se presenti): copie minime
    /// (Id, Archiviata, Guasto) usate solo dal calcolo, mai salvate.</summary>
    public static List<Requisito> Idrata(AppState state, IReadOnlyList<Requisito> requisiti, Action<string>? avviso = null, CancellationToken ct = default)
    {
        var archivio = state.Master.RequisitiDtc;
        archivio.Risolvi(requisiti);
        var risultato = new List<Requisito>(requisiti.Count);
        foreach (var r in requisiti)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Guasto != null || r.DtcTotale == 0) { risultato.Add(r); continue; }
            var letto = archivio.Leggi(r);
            if (letto.Avviso != null) avviso?.Invoke(letto.Avviso);
            // ExtensionData (Tipo/SottoveicoliD4D dichiarati) serve a Calcola per segnalare le voci con veicolo non piu dichiarato.
            risultato.Add(new Requisito { Id = r.Id, Archiviata = r.Archiviata, Guasto = letto.Voci, ExtensionData = r.ExtensionData });
        }
        return risultato;
    }

    private static IEnumerable<EsitoDtcRilevato> LeggiEsiti(AppState state, string? soloRequisitoId, CancellationToken ct)
    {
        var dbPath = state.TestCases.HasPersistentSnapshot ? state.TestCases.LocalIndexDatabasePath : null;
        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            var q = new LocalDtcQueryService(dbPath!);
            if (q.Disponibile()) return q.StreamEsiti(ct, soloRequisitoId);
        }
        var esiti = DtcAvanzamentoService.RaccogliEsiti(state.TestCases.GetAll());
        if (!string.IsNullOrWhiteSpace(soloRequisitoId))
            esiti = esiti.Where(e => e.RequisitoId.Equals(soloRequisitoId.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return esiti;
    }

    /// <summary>Testo del passo mancante di una cella (per la UI): "manca validazione/devalidazione/entrambi".</summary>
    public static string PassoMancante(DtcAvanzamento a)
    {
        if (a.Stato == StatoDtc.Completo) return "";
        var val = a.EsitoValidazione.HasValue && a.EsitoValidazione != Esito.NonConforme;
        var dev = a.EsitoDevalidazione.HasValue && a.EsitoDevalidazione != Esito.NonConforme;
        var t = LocalizationService.Instance;
        if (!val && !dev) return t.Traduci("DtcMancaEntrambi");
        return !val ? t.Traduci("DtcMancaValidazione") : t.Traduci("DtcMancaDevalidazione");
    }

    public static string StatoEtichetta(DtcAvanzamento a) =>
        LocalizationService.Instance.Traduci(a.Stato switch
        {
            StatoDtc.Completo => "DtcStatoCompleto", StatoDtc.Validato => "DtcStatoValidato",
            StatoDtc.NonConforme => "DtcStatoNonConforme", _ => "DtcStatoDaFare"
        }) + (a.Conflitto ? " ⚠" : "");
}
