using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Esito di <see cref="AnomaliaChiusuraService.ChiudiConNuovaRev"/>.</summary>
public sealed record ChiusuraAnomaliaRisultato(Esecuzione? NuovaRev, bool EsitoCambiato, bool StatoLavoroCambiato);

/// <summary>
/// Chiusura di un'anomalia (NonConformita) cambiando gli esiti del test collegato: gli esiti nuovi nascono in una
/// NUOVA REV n+1 con Origine esplicita (Rete / RieseguitoDaNoi); la REV n e tutte le precedenti non vengono mai
/// toccate (immutabilita REV, vedi <see cref="TestRevisionSafety"/>). L'Assegnatario del test non cambia.
/// </summary>
public static class AnomaliaChiusuraService
{
    /// <summary>
    /// Applica in memoria su <paramref name="tc"/> e <paramref name="nc"/> (nessuna persistenza). Chiave del
    /// dizionario: Uid di una Condizione della REV corrente; l'esito nuovo diventa un nuovo tentativo (stessa
    /// OperazioneOrigineUid radice, come "Ripeti operazione") invece di mutare la condizione esistente. Le
    /// condizioni il cui esito non cambia sono ignorate. Se nessun esito cambia non nasce nessuna REV; un cambio
    /// del solo <paramref name="nuovoStatoLavoro"/> e metadato di workflow e non crea REV.
    /// </summary>
    public static ChiusuraAnomaliaRisultato ChiudiConNuovaRev(
        TestCase tc, NonConformita nc,
        IReadOnlyDictionary<Guid, Esito> nuoviEsitiPerCondizione,
        StatoLavoroTest? nuovoStatoLavoro,
        OrigineRevisione origine,
        string operatore, DateTime now, string motivoChiusura,
        IReadOnlyDictionary<Guid, int?>? durataPerCondizione = null,
        bool durataObbligatoria = false)
    {
        ArgumentNullException.ThrowIfNull(tc);
        ArgumentNullException.ThrowIfNull(nc);
        ArgumentNullException.ThrowIfNull(nuoviEsitiPerCondizione);
        if (string.IsNullOrWhiteSpace(motivoChiusura))
            throw new ArgumentException("Il motivo di chiusura dell'anomalia e obbligatorio.", nameof(motivoChiusura));
        if (nc.StatoWorkflow == StatoAnomalia.Conclusa)
            throw new InvalidOperationException($"L'anomalia {nc.Id} e gia chiusa.");

        Esecuzione? nuovaRev = null;
        if (nuoviEsitiPerCondizione.Count > 0)
        {
            if (tc.Esecuzioni.Count == 0)
                throw new InvalidOperationException($"Il Test {tc.Id} non ha revisioni: impossibile cambiare gli esiti.");
            var ultima = tc.Esecuzioni[^1];
            var bozza = EntityStateFingerprint.Clone(ultima);

            var modifiche = new List<(Condizione Origine, Esito Nuovo)>();
            // TIME: durata dichiarata nel dialogo per ogni operazione cambiata (chiave: radice, come la copia). Assente = nessuna durata.
            var durataPerRadice = new Dictionary<Guid, int?>();
            foreach (var (uid, esito) in nuoviEsitiPerCondizione)
            {
                var scelta = bozza.Condizioni.FirstOrDefault(c => c.Uid == uid)
                    ?? throw new ArgumentException($"La condizione {uid} non appartiene alla REV {ultima.Iterazione} del Test {tc.Id}.", nameof(nuoviEsitiPerCondizione));
                var radice = AnomaliaWorkflowService.Radice(scelta);
                var effettiva = bozza.CondizioniEffettive.First(c => AnomaliaWorkflowService.Radice(c) == radice);
                if (durataPerCondizione is not null && durataPerCondizione.TryGetValue(uid, out var minuti)) durataPerRadice[radice] = minuti;
                if (effettiva.Esito != esito && modifiche.All(m => AnomaliaWorkflowService.Radice(m.Origine) != radice))
                    modifiche.Add((effettiva, esito));
            }

            if (modifiche.Count > 0)
            {
                // Sulla REV n+1 l'esito DERIVA dalle condizioni: la clonazione della REV n non deve portarsi dietro override
                // di esito/punteggi, motivazioni, deviazioni, note libere o il collegamento NC della REV precedente
                // (SincronizzaEsito non ricalcola con override attivo, quindi la chiusura non cambierebbe l'esito).
                bozza.EsitoSovrascritto = false;
                bozza.MotivazioneOverride = "";
                bozza.Deviazioni = "";
                bozza.Note = "";
                bozza.NonConformitaId = null;
                foreach (var p in bozza.PunteggiRequisiti) { p.Sovrascritto = false; p.MotivazioneOverride = ""; }
                var notaChiusura = $"Chiusura anomalia {nc.Id} (origine {origine.Label()}): {motivoChiusura.Trim()}";

                foreach (var (origineCond, esito) in modifiche)
                {
                    var radice = AnomaliaWorkflowService.Radice(origineCond);
                    var ultimoIndice = bozza.Condizioni.FindLastIndex(c => AnomaliaWorkflowService.Radice(c) == radice);
                    var copia = new Condizione
                    {
                        Esito = esito,
                        Componente = new EcuSelection(origineCond.Componente),
                        Funzionalita = origineCond.Funzionalita,
                        Guasto = origineCond.Guasto, DtcRequisitoId = origineCond.DtcRequisitoId, DtcRequisitoVuotoScelto = origineCond.DtcRequisitoVuotoScelto, DtcPasso = origineCond.DtcPasso,
                        DtcVeicolo = origineCond.DtcVeicolo, Strumento = origineCond.Strumento,
                        OperazioneOrigineUid = radice,
                        ValoriStrumenti = DurataOperazione.CopiaSenzaDurata(origineCond.ValoriStrumenti),
                        // Campi richiesti dalle validazioni per-operazione dell'editor (Nota/Motivazione N.A./Deviazione/
                        // Problema in base all'esito): senza, il primo Salva sul test sarebbe bloccato. Il testo del
                        // tentativo precedente della stessa radice si riusa quando c'e', altrimenti la nota di chiusura.
                        Nota = notaChiusura,
                        MotivazioneNonApplicabile = esito == Esito.NonApplicabile ? PrimoNonVuoto(origineCond.MotivazioneNonApplicabile, notaChiusura) : "",
                        DescrizioneDeviazione = esito == Esito.ConformeConDeviazioni ? PrimoNonVuoto(origineCond.DescrizioneDeviazione, notaChiusura) : "",
                        DescrizioneProblema = esito == Esito.NonConforme ? PrimoNonVuoto(origineCond.DescrizioneProblema, notaChiusura) : "",
                    };
                    // TIME: la durata della nuova prova viene dal dialogo (proposta dalla regola di copia). La politica Master la
                    // richiede per le operazioni eseguite: senza durata la chiusura non si salva (la vista la blocca prima con un messaggio).
                    copia.DurataMinuti = durataPerRadice.TryGetValue(radice, out var durata) ? durata : null;
                    if (durataObbligatoria && esito.EEseguito() && copia.DurataMinuti is null)
                        throw new InvalidOperationException(
                            $"La durata dell'operazione e obbligatoria per l'anomalia {nc.Id} (politica Master): indicare i minuti prima di chiudere.");
                    bozza.Condizioni.Insert(ultimoIndice + 1, copia);
                }
                bozza.Iterazione = tc.Esecuzioni.Count + 1;
                bozza.Data = now;
                bozza.Operatore = operatore;
                bozza.Origine = origine;
                bozza.NonConformitaOrigineId = nc.Id;
                bozza.SincronizzaEsito();
                PunteggioConformitaService.SincronizzaPunteggi(bozza, tc.RequisitoId);
                tc.Esecuzioni.Add(bozza);
                nuovaRev = bozza;
                tc.Changelog.Add(new ChangelogEntry
                {
                    Data = now, Operatore = operatore, Campo = "REV da chiusura anomalia",
                    ValorePrecedente = $"REV {ultima.Iterazione}",
                    ValoreNuovo = $"REV {bozza.Iterazione} ({origine}) da {nc.Id}"
                });
            }
        }

        var statoCambiato = nuovoStatoLavoro.HasValue && nuovoStatoLavoro.Value != tc.StatoLavoro;
        if (statoCambiato)
        {
            tc.Changelog.Add(new ChangelogEntry
            {
                Data = now, Operatore = operatore, Campo = "StatoLavoro",
                ValorePrecedente = tc.StatoLavoro.ToString(), ValoreNuovo = nuovoStatoLavoro!.Value.ToString()
            });
            tc.StatoLavoro = nuovoStatoLavoro.Value;
        }
        if (nuovaRev != null || statoCambiato)
        {
            tc.UltimaModifica = now;
            tc.ModificatoDa = operatore;
        }

        NonConformitaRulesService.Chiudi(nc);
        nc.DataChiusura = now;
        nc.MotivoChiusura = motivoChiusura.Trim();
        nc.Changelog.Add(new ChangelogEntry
        {
            Data = now, Operatore = operatore, Campo = "Chiusura",
            ValorePrecedente = null,
            ValoreNuovo = nuovaRev != null ? $"{nc.MotivoChiusura} (REV {nuovaRev.Iterazione}, {origine})" : nc.MotivoChiusura
        });
        return new ChiusuraAnomaliaRisultato(nuovaRev, nuovaRev != null, statoCambiato);
    }

    private static string PrimoNonVuoto(string? preferito, string alternativa) =>
        string.IsNullOrWhiteSpace(preferito) ? alternativa : preferito;

    /// <summary>
    /// Orchestrazione atomica: dentro una Unit of Work rilegge Test (da disco) e NC (clone della cache, mai
    /// l'istanza condivisa), applica la chiusura e salva Test e NC con controllo di RevisioneDati. Qualunque errore
    /// (incluso <see cref="ConflittoRevisioneException"/>) fa ripristinare i file gia toccati e viene rilanciato.
    /// <paramref name="revisioneTestAttesa"/>/<paramref name="revisioneNcAttesa"/> sono le RevisioneDati viste
    /// dall'utente quando ha aperto la finestra: se diverse da quelle correnti il salvataggio e rifiutato.
    /// </summary>
    public static ChiusuraAnomaliaRisultato ChiudiESalva(
        AppState state, string testId, string ncId,
        IReadOnlyDictionary<Guid, Esito> nuoviEsitiPerCondizione,
        StatoLavoroTest? nuovoStatoLavoro,
        OrigineRevisione origine,
        string operatore, DateTime now, string motivoChiusura,
        long? revisioneTestAttesa = null, long? revisioneNcAttesa = null,
        IReadOnlyDictionary<Guid, int?>? durataPerCondizione = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(motivoChiusura))
            throw new ArgumentException("Il motivo di chiusura dell'anomalia e obbligatorio.", nameof(motivoChiusura));

        string? percorsoTest = null;
        string? percorsoNc = null;
        try
        {
        return WorkflowTransactionService.Execute(state.Paths, $"Chiusura anomalia {ncId}", () =>
        {
            var testNoto = state.TestCases.GetById(testId)
                ?? throw new InvalidOperationException($"Test {testId} non trovato.");
            var tc = state.TestCases.LoadCurrent(testNoto)
                ?? throw new InvalidOperationException($"Test {testId} non leggibile su disco.");
            var ncNota = state.NonConformita.GetById(ncId)
                ?? throw new InvalidOperationException($"Anomalia {ncId} non trovata.");
            var nc = EntityStateFingerprint.Clone(ncNota);

            percorsoTest = state.TestCases.FilePathOf(tc);
            var attesaTest = revisioneTestAttesa ?? tc.RevisioneDati;
            var attesaNc = revisioneNcAttesa ?? nc.RevisioneDati;
            if (attesaTest != tc.RevisioneDati)
                throw new ConflittoRevisioneException(tc.Id, attesaTest, tc.RevisioneDati);
            if (attesaNc != nc.RevisioneDati)
                throw new ConflittoRevisioneException(nc.Id, attesaNc, nc.RevisioneDati);

            // Stessa regola della politica nell'editor: non si applica ai Test importati da UnitLogger (nessuna operazione di lavoro).
            var durataObbligatoria = PoliticaDurataService.RichiedeDurata(state.PoliticaDurata) && tc.Origine != OrigineTest.ImportStatoUdt;
            var risultato = ChiudiConNuovaRev(tc, nc, nuoviEsitiPerCondizione, nuovoStatoLavoro, origine, operatore, now, motivoChiusura,
                durataPerCondizione, durataObbligatoria);
            if (risultato.NuovaRev != null || risultato.StatoLavoroCambiato)
                state.TestCases.Save(tc, false, attesaTest);
            percorsoNc = state.NonConformita.FilePathOf(nc);
            state.NonConformita.Save(nc, attesaNc);
            return risultato;
        });
        }
        catch
        {
            // Il rollback ha gia ripristinato i file su disco ma non la cache in-process del repository (aggiornata
            // dal Save del Test): rileggiamo il solo file toccato cosi la cache non mostra una REV mai committata.
            if (percorsoTest != null)
            {
                try { state.TestCases.RefreshPaths(new[] { percorsoTest }); } catch { /* best effort: l'errore originale prevale */ }
            }
            // Stessa incoerenza per la NC: il Save in cache ha preceduto il rollback del file, e al Salva successivo
            // la cache stantia darebbe un falso conflitto di RevisioneDati.
            if (percorsoNc != null)
            {
                try { state.NonConformita.RefreshPaths(new[] { percorsoNc }); } catch { /* best effort */ }
            }
            throw;
        }
    }
}
