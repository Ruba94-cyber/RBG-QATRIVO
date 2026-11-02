using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Percorsi di persistenza fail-closed per le azioni di workflow della pagina Anomalie. Il ViewModel storico
/// mutava alcuni oggetti live prima del Save e classificava una Segnalazione con due write indipendenti.
/// Queste varianti lavorano su copie staged e, quando sono coinvolti due record, usano una WorkflowTransaction
/// durevole: un conflitto non puo' lasciare ne' una UI in uno stato mai persistito ne' una NC orfana.
/// </summary>
public partial class NonConformitaViewModel
{
    [RelayCommand]
    private async Task ChiudiManualmenteSicuro(NonConformita? nc)
    {
        if (nc == null) return;
        if (string.IsNullOrWhiteSpace(nc.MotivoChiusura))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ChiudereAnomaliaMotivo");
            return;
        }

        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("ChiudiAnomaliaConfermaTitolo"), loc.Traduci("ChiudiAnomaliaConfermaBody"),
                loc.Traduci("ChiudiAnomaliaBtn"), loc.Traduci("Annulla")))
            return;

        var revisioneAttesa = nc.RevisioneDati;
        var candidato = EntityStateFingerprint.Clone(nc);
        NonConformitaRulesService.Chiudi(candidato);
        if (!string.IsNullOrWhiteSpace(NuovaAzione)) candidato.Azione = NuovaAzione;

        try { _state.NonConformita.Save(candidato, revisioneAttesa); }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ChiusuraNonRiuscitaFmt"), ex.Message);
            return;
        }

        MessaggioErrore = null;
        NuovaAzione = "";
        Ricarica();
    }


    /// <summary>
    /// Eliminazione definitiva NC con gli stessi guard del repository, ma senza affidarsi al solo IsEnabled
    /// della View. Ruolo e stato condiviso vengono rivalutati DOPO la conferma; l'oggetto passato dalla UI e'
    /// una working-copy e viene clonato prima dell'operazione, così un errore di revisione/I/O non può mutare
    /// la riga visualizzata. DomainRecoveryService nel repository conserva la cancellazione recuperabile.
    /// </summary>
    [RelayCommand]
    private async Task EliminaAnomaliaSicuro(NcRiga? riga)
    {
        if (riga == null || !_state.EMaster || !_state.PuoScrivere) return;
        var loc = LocalizationService.Instance;
        if (!await ConfirmService.Chiedi(loc.Traduci("EliminaAnomaliaConfermaTitolo"), loc.Traduci("EliminaAnomaliaConfermaBody"),
                loc.Traduci("EliminaBtn"), loc.Traduci("Annulla")))
            return;

        // Il dialog può essere rimasto aperto mentre ruolo, manifest o connettività sono cambiati.
        if (!_state.EMaster || !_state.PuoScrivere)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ArchivioAzioneNonAutorizzata");
            return;
        }

        try { AnomaliaAzioniService.Elimina(_state, riga.Nc); }
        catch (Exception ex)
        {
            // Gli indici sono gia' stati riallineati da AnomaliaAzioniService.Elimina.
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message);
            Ricarica();
            return;
        }

        MessaggioErrore = null;
        _state.RicaricaTutto();
        Ricarica();
    }

    /// <summary>
    /// Collega a posteriori una NC nata senza Test (tipicamente una segnalazione dalla rete assistenziale,
    /// feedback utente 2026-09-08) a un Test creato/eseguito per indagarla. I campi propri della segnalazione
    /// (EcuSegnalate/VeicoliSegnalati/... e DaSegnalazioneRete) restano intatti: registrano cosa la rete ha
    /// effettivamente segnalato, non vengono sostituiti da quanto il Test poi verifica. Non aggiunge una
    /// ProveCollegate: senza una Condizione specifica da puntare non c'e' una prova su cui basare la chiusura
    /// automatica per conformita' (AnomaliaWorkflowService.PuoChiudersiPerConformita), che resta quindi una
    /// decisione umana come per qualunque NC non ancora verificata da un esito.
    /// </summary>
    [RelayCommand]
    private void CollegaATestSicuro(NcRiga? riga)
    {
        if (riga == null || !PuoModificare) return;
        var testId = riga.TestIdDaCollegare.Trim();
        if (string.IsNullOrWhiteSpace(testId))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("CollegaATestIdObbl");
            return;
        }
        var test = _state.TestCases.GetById(testId);
        if (test == null || test.Eliminato)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("CollegaATestNonTrovatoFmt"), testId);
            return;
        }

        var revisioneAttesa = riga.Nc.RevisioneDati;
        var candidato = EntityStateFingerprint.Clone(riga.Nc);
        candidato.TestCaseId = test.Id;
        candidato.TestUid = test.Uid;
        candidato.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = _state.Config.OperatoreAttivo,
            Campo = "collegamento a test",
            ValoreNuovo = test.Id
        });

        try { _state.NonConformita.Save(candidato, revisioneAttesa); }
        catch (Exception ex)
        {
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoFmt"), ex.Message);
            return;
        }

        MessaggioErrore = null;
        Ricarica();
    }

    [RelayCommand]
    private void ClassificaSegnalazioneSicuro(SegnalazioneRiga? riga)
    {
        if (riga == null) return;
        if (!riga.Severita.HasValue)
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("ScegliSeveritaPrima");
            return;
        }

        var segOriginale = riga.Segnalazione;
        var revisioneSegnalazioneAttesa = segOriginale.RevisioneDati;
        var segCandidato = EntityStateFingerprint.Clone(segOriginale);
        var testOrigine = segOriginale.TestUid.HasValue ? _state.TestCases.GetByUid(segOriginale.TestUid.Value) : _state.TestCases.GetById(segOriginale.TestId);
        if (testOrigine != null && !AnomaliaWorkflowService.SegnalazioneAppartieneAlTest(testOrigine, segOriginale)) testOrigine = null;
        var testUid = segOriginale.TestUid ?? testOrigine?.Uid;
        segCandidato.TestUid = testUid;
        var nc = new NonConformita
        {
            Id = _state.NonConformita.NextId(),
            TestCaseId = segOriginale.TestId, TestUid = testUid,
            DataApertura = DateTime.Now,
            Owner = _state.Config.OperatoreAttivo,
            Severita = riga.Severita,
            Priorita = testOrigine?.Priorita ?? PrioritaOperativa.Normal,
            StatoWorkflow = StatoAnomalia.Confermata,
            Descrizione = string.IsNullOrWhiteSpace(riga.TitoloAnomalia) ? riga.Reiterazione : riga.TitoloAnomalia,
            ProveCollegate = new List<RiferimentoProva>
            {
                new() { TestId = segOriginale.TestId, TestUid = testUid, EsecuzioneIterazione = segOriginale.EsecuzioneIterazione, CondizioneUid = segOriginale.CondizioneUid }
            }
        };

        segCandidato.Stato = StatoSegnalazione.Classificata;
        segCandidato.AnomaliaUid = nc.Uid;

        try
        {
            using var tx = WorkflowTransactionService.Begin(_state.Paths, $"classify-signal:{segOriginale.Uid:N}");
            _state.NonConformita.Save(nc);
            _state.Segnalazioni.Save(segCandidato, revisioneSegnalazioneAttesa);
            tx.Commit();
        }
        catch (Exception ex)
        {
            // Dispose della transazione avviene prima di entrare qui e ripristina i file. I repository possono
            // pero' avere gia' aggiornato la cache dopo il primo Save: riallineiamo esplicitamente entrambe le
            // viste locali prima di tornare all'utente.
            _state.NonConformita.RefreshIndex();
            _state.Segnalazioni.RefreshIndex();
            SalvaBozzaNcFallita(nc, ex);
            SalvaBozzaSegnalazioneFallita(segCandidato, ex, segOriginale);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ClassificazioneNonRiuscitaFmt"), ex.Message);
            Ricarica();
            return;
        }

        MessaggioErrore = null;
        _state.RicaricaTutto();
        Ricarica();
    }

    [RelayCommand]
    private void EscludiSegnalazioneSicuro(SegnalazioneRiga? riga)
    {
        if (riga == null) return;
        if (string.IsNullOrWhiteSpace(riga.MotivazioneEsclusione))
        {
            MessaggioErrore = LocalizationService.Instance.Traduci("MotivazioneEsclusioneObbl");
            return;
        }

        var segOriginale = riga.Segnalazione;
        var revisioneAttesa = segOriginale.RevisioneDati;
        var candidato = EntityStateFingerprint.Clone(segOriginale);
        candidato.Stato = StatoSegnalazione.Esclusa;
        candidato.MotivazioneEsclusione = riga.MotivazioneEsclusione.Trim();
        try { _state.Segnalazioni.Save(candidato, revisioneAttesa); }
        catch (Exception ex)
        {
            SalvaBozzaSegnalazioneFallita(candidato, ex, segOriginale);
            MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("EsclusioneNonRiuscitaFmt"), ex.Message);
            return;
        }

        MessaggioErrore = null;
        Ricarica();
    }
}
