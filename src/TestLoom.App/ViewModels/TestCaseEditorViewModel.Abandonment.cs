using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Cleanup della sola sessione che viene realmente abbandonata. La conferma è centralizzata in ConfirmService,
/// ma ogni editor conserva la propria identità/lock e decide in modo conservativo se quella conferma lo
/// riguarda. Nessun percorso di crash entra qui: crash => bozza e recovery rimangono disponibili.
/// </summary>
public partial class TestCaseEditorViewModel : IConfirmedAbandonmentParticipant
{
    void IConfirmedAbandonmentParticipant.PrepareConfirmedAbandonmentCleanup()
    {
        // Allegati staccati ma non ancora persistiti: il cleanup sotto li tratta come aggiunte di sessione.
        var staccati = _rimozioniAllegati.Abbandona();
        if (!ModificheNonSalvate || TestCase == null || TestCase.Uid == Guid.Empty ||
            string.IsNullOrWhiteSpace(_state.Config.OperatoreAttivo)) return;

        var working = EntityStateFingerprint.Clone(TestCase);
        AttachmentRemovalQueue.UnisciAllaCopiaDiLavoro(working.Allegati, staccati);
        var authoritative = _snapshot == null ? null : EntityStateFingerprint.Clone(_snapshot);
        var added = _allegatiAggiuntiNellaSessione.ToArray();
        var paths = _state.Paths;
        var operatore = _state.Config.OperatoreAttivo;
        var drafts = _state.BozzaAutosave;

        if (IsNew)
        {
            // Un Test nuovo non possiede lock. Nell'app principale può esistere un solo editor nuovo attivo;
            // la conferma che ha appena generato questo callback è quindi il suo abbandono. Posticipiamo di un
            // giro UI per lasciare terminare la navigazione/chiusura che ha chiesto la conferma.
            // IsNew resta vero per tutta la sessione dell'editor, anche dopo un Salva riuscito: se il Test e' gia' su
            // disco, il record autorevole e' l'ultimo snapshot salvato. Senza questo, gli allegati gia' persistiti
            // verrebbero trattati come aggiunte mai salvate e cancellati fisicamente (perdita di evidenza).
            var autorevole = _salvatoSuDisco && _snapshot != null ? EntityStateFingerprint.Clone(_snapshot) : null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, autorevole, added);
                drafts.Elimina(operatore);
            }, Avalonia.Threading.DispatcherPriority.Background);
            return;
        }

        if (!InModifica || string.IsNullOrWhiteSpace(PercorsoFile)) return;
        var path = PercorsoFile;
        var lockBefore = _state.Locks.GetActiveLock(path);
        if (lockBefore == null || string.IsNullOrWhiteSpace(lockBefore.SessionId)) return;

        var sessionId = lockBefore.SessionId;
        var locks = _state.Locks;
        var uid = TestCase.Uid;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Una conferma proveniente da un altro editor non deve toccare questa sessione: se il token del
            // lock è ancora lo stesso, questo editor non è stato abbandonato. Solo la scomparsa/cambio di QUEL
            // SessionId autorizza la cleanup della bozza e degli allegati di staging catturati sopra.
            var current = locks.GetActiveLock(path);
            if (current != null && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)) return;

            DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, authoritative, added);
            drafts.EliminaModifica(operatore, uid);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}
