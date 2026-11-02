using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Passi interattivi dell'associazione guasto ↔ requisito Extra, condivisi dal Registro guasto e dall'editor dei requisiti: scelta del requisito
/// (o delle righe), conferma esplicita di ciò che verrà aggiunto al requisito, esecuzione in background e riepilogo. Tutte le scritture
/// passano da <see cref="DtcRegistroAssociazioneService"/>; qui non si modifica mai nulla senza una conferma dell'operatore.
/// </summary>
public sealed class DtcAssociazioneFlusso(AppState state)
{
    private readonly AppState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly DtcRegistroAssociazioneService _servizio = new(state);

    private static string S(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    private static string Marcatore(DtcRegistroAssociazioneService.CandidatoRequisito c) =>
        c.Compatibile ? S("  ✓ contiene già questo guasto", "  ✓ already contains this guasto", "  ✓ contient déjà ce guasto")
        : c.NumeroDtc == 0 ? S("  · senza guasto", "  · no Guasti", "  · sans guasto")
        : string.Format(S("  · {0} guasto", "  · {0} Guasti", "  · {0} guasto"), c.NumeroDtc);

    /// <summary>Dalla lista: associa una o più righe guasto a un requisito Extra scelto fra tutti quelli attivi. i guasti mancanti si aggiungono
    /// all'elenco del requisito solo dopo conferma. Ritorna il messaggio di esito, o null se annullato.</summary>
    public async Task<string?> AssociaRigheAsync(IReadOnlyList<RigaDtcRegistro> righe)
    {
        if (righe.Count == 0) return null;
        var titolo = S("Associa a requisito Extra", "Assign to Extra requirement", "Associer à une exigence Extra");
        var candidati = await Task.Run(() => _servizio.Candidati(righe.Count == 1 ? righe[0] : null));
        if (candidati.Count == 0)
            return S("Non ci sono requisiti Extra attivi a cui associare.", "There are no active Extra requirements to assign to.",
                "Il n’y a aucune exigence Extra active à laquelle associer.");

        var voci = candidati.Select(c => new VoceSelezionabile
        {
            Valore = c,
            TestoPrincipale = c.Requisito.Id + Marcatore(c),
            TestoSecondario = c.Requisito.Testo,
            TestoRicerca = $"{c.Requisito.Id} {c.Requisito.Testo}"
        }).ToList();
        var scelta = await SelezionaDialogService.Scegli(titolo, voci);
        if (scelta?.Valore is not DtcRegistroAssociazioneService.CandidatoRequisito candidato) return null;

        // Una riga già compatibile con il requisito: associazione diretta, senza modificare l'elenco guasto del requisito.
        if (righe.Count == 1 && candidato.Compatibile)
        {
            var diretto = await Task.Run(() => _servizio.Associa(righe[0], candidato.Requisito.Id));
            return string.Format(S(
                "Associazione salvata nella nuova REV {0}. La riga della revisione precedente resta invariata.",
                "Assignment saved in new revision {0}. The previous revision row remains unchanged.",
                "Association enregistrée dans la nouvelle révision {0}. La ligne de la révision précédente reste inchangée."),
                diretto.NumeroNuovaRevisione);
        }

        // Se il requisito non contiene già il guasto (o si associano più righe) si dice cosa verrà aggiunto prima di scrivere.
        if (righe.Count > 1 || !candidato.Compatibile)
        {
            var messaggio = string.Format(S(
                "{0} righe guasto verranno associate al requisito {1}. i guasti (con componente e veicolo) che il requisito non contiene ancora saranno aggiunti al suo elenco guasto, nel rispetto delle regole del requisito. Procedere?",
                "{0} fault rows will be assigned to requirement {1}. Faults (with component and vehicle) not yet in the requirement will be added to its fault list, following the requirement rules. Continue?",
                "{0} lignes guasto seront associées à l’exigence {1}. Les guasto (avec componente et véhicule) absents de l’exigence seront ajoutés à sa liste guasto, selon ses règles. Continuer ?"),
                righe.Count, candidato.Requisito.Id);
            if (!await ConfirmService.Chiedi(titolo, messaggio, S("Associa", "Assign", "Associer"), S("Annulla", "Cancel", "Annuler")))
                return null;
        }
        var esito = await Task.Run(() => _servizio.AssociaInBlocco(righe, candidato.Requisito.Id, aggiungiDtc: true));
        return Riepilogo(esito, candidato.Requisito.Id);
    }

    /// <summary>Dal requisito: sceglie un requisito Extra (quelli senza guasto per primi) oppure usa quello indicato, poi sceglie fra le righe del
    /// registro ancora senza requisito quelle da associargli. Ritorna il messaggio di esito, o null se annullato.</summary>
    public async Task<string?> DaRequisitoAsync(Requisito? requisito = null)
    {
        var titolo = S("Associa guasto a un requisito Extra", "Assign Guasti to a Extra requirement", "Associer des guasto à une exigence Extra");
        if (requisito == null)
        {
            var elenco = await Task.Run(() => _servizio.ElencoRequisitiD4d().OrderBy(r => r.NumeroDtc == 0 ? 0 : 1).ToList());
            if (elenco.Count == 0)
                return S("Non ci sono requisiti Extra attivi.", "There are no active Extra requirements.", "Il n’y a aucune exigence Extra active.");
            var vociReq = elenco.Select(r => new VoceSelezionabile
            {
                Valore = r.Requisito,
                TestoPrincipale = r.Requisito.Id + (r.NumeroDtc == 0
                    ? S("  · senza guasto", "  · no Guasti", "  · sans guasto")
                    : string.Format(S("  · {0} guasto", "  · {0} Guasti", "  · {0} guasto"), r.NumeroDtc)),
                TestoSecondario = r.Requisito.Testo,
                TestoRicerca = $"{r.Requisito.Id} {r.Requisito.Testo}"
            }).ToList();
            var sceltoReq = await SelezionaDialogService.Scegli(titolo, vociReq);
            requisito = sceltoReq?.Valore as Requisito;
            if (requisito == null) return null;
        }

        var scelto = requisito;
        var (righe, gia) = await Task.Run(() => (RigheAssociabili(), TupleDelRequisito(scelto)));
        if (righe.Count == 0)
            return S("Non ci sono righe guasto della REV corrente senza requisito da associare.",
                "There are no current-revision fault rows without a requirement to assign.",
                "Il n’y a aucune ligne guasto de la révision courante sans exigence à associer.");
        var voci = righe.Select(r =>
        {
            var presente = gia.Contains(DtcRequisito.Chiave(r.Veicolo, r.Componente, r.Guasto));
            return new VoceSelezionabile
            {
                Valore = r,
                TestoPrincipale = $"{r.Guasto} · {r.EcuVisualizzata} · {r.VeicoloVisualizzato}",
                TestoSecondario = $"{r.TestId} · {r.DataTesto} · {r.PassoTesto} · {r.EsitoTesto} · " + (presente
                    ? S("già nell’elenco del requisito", "already in the requirement list", "déjà dans la liste de l’exigence")
                    : S("verrà aggiunto al requisito", "will be added to the requirement", "sera ajouté à l’exigence")),
                TestoRicerca = $"{r.Guasto} {r.Componente} {r.Veicolo} {r.TestId} {r.Operatore}"
            };
        }).ToList();
        var scelte = await SelezionaDialogService.ScegliMultipli(
            string.Format(S("Guasto da associare a {0} (selezione multipla)", "Faults to assign to {0} (multiple selection)", "Guasto à associer à {0} (sélection multiple)"), requisito.Id), voci);
        var daAssociare = scelte.Select(v => v.Valore).OfType<RigaDtcRegistro>().ToList();
        if (daAssociare.Count == 0) return null;

        var nuovi = daAssociare.Count(r => !gia.Contains(DtcRequisito.Chiave(r.Veicolo, r.Componente, r.Guasto)));
        var messaggio = string.Format(S(
            "{0} righe guasto verranno associate al requisito {1}; {2} guasto non ancora presenti saranno aggiunti al suo elenco guasto. Procedere?",
            "{0} fault rows will be assigned to requirement {1}; {2} Faults not yet present will be added to its fault list. Continue?",
            "{0} lignes guasto seront associées à l’exigence {1} ; {2} guasto absents seront ajoutés à sa liste guasto. Continuer ?"),
            daAssociare.Count, requisito.Id, nuovi);
        if (!await ConfirmService.Chiedi(titolo, messaggio, S("Associa", "Assign", "Associer"), S("Annulla", "Cancel", "Annuler"))) return null;

        var idRequisito = requisito.Id;
        var esito = await Task.Run(() => _servizio.AssociaInBlocco(daAssociare, idRequisito, aggiungiDtc: true));
        return Riepilogo(esito, idRequisito);
    }

    /// <summary>Righe del registro senza requisito e appartenenti alla REV corrente del loro Test (le uniche associabili).</summary>
    public List<RigaDtcRegistro> RigheAssociabili()
    {
        var percorso = _state.TestCases.LocalIndexDatabasePath;
        if (!_state.CartellaValida || !_state.TestCases.HasPersistentSnapshot || string.IsNullOrWhiteSpace(percorso)) return new List<RigaDtcRegistro>();
        var query = new LocalDtcRegistroQueryService(percorso);
        var risultato = new List<RigaDtcRegistro>();
        foreach (var riga in query.StreamRighe(new FiltroDtcRegistro(SoloSenzaRequisito: true)))
        {
            if (!Guid.TryParse(riga.TestUid, out var uid)) continue;
            var test = _state.TestCases.GetByUid(uid);
            if (test != null && !test.Eliminato && riga.RevisioneIndice == test.Esecuzioni.Count - 1) risultato.Add(riga);
        }
        return risultato;
    }

    private HashSet<string> TupleDelRequisito(Requisito requisito) =>
        new(_state.Master.RequisitiDtc.Leggi(requisito).Voci.Select(DtcRequisito.Chiave), StringComparer.OrdinalIgnoreCase);

    private static string Riepilogo(DtcRegistroAssociazioneService.EsitoBlocco esito, string requisitoId)
    {
        var testo = string.Format(S(
            "Associate {0} righe al requisito {1}", "Assigned {0} rows to requirement {1}", "{0} lignes associées à l’exigence {1}"),
            esito.Associate, requisitoId);
        if (esito.DtcAggiuntiAlRequisito > 0)
            testo += string.Format(S("; aggiunti {0} guasto al requisito", "; added {0} Faults to the requirement", "; {0} guasto ajoutés à l’exigence"), esito.DtcAggiuntiAlRequisito);
        if (esito.Associate > 0)
            testo += S(". Ogni Test modificato ha una nuova REV; le REV precedenti restano invariate.",
                ". Each changed Test has a new revision; previous revisions remain unchanged.",
                ". Chaque Test modifié a une nouvelle révision ; les révisions précédentes restent inchangées.");
        if (esito.NonAssociate > 0)
        {
            var primo = esito.Righe.First(r => r.Esito == null);
            testo += string.Format(S(". Non associate {0}: {1}", ". Not assigned {0}: {1}", ". {0} non associées : {1}"), esito.NonAssociate, primo.Errore);
        }
        return testo;
    }
}
