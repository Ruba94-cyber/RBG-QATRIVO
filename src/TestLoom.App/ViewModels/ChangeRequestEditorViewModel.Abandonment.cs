using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Cleanup degli allegati della Change Request soltanto dopo una conferma scoped di abbandono. Gli upload CR
/// sono consentiti esclusivamente su CR già persistite, quindi al momento dello scarto possiamo rileggere la
/// versione autorevole corrente e usare quella come baseline: eventuali aggiornamenti di un altro PC vincono
/// sempre sulla working copy locale. Crash/kill non arrivano qui e lasciano intatta la recovery.
/// </summary>
public partial class ChangeRequestEditorViewModel : IConfirmedAbandonmentParticipant
{
    void IConfirmedAbandonmentParticipant.PrepareConfirmedAbandonmentCleanup()
    {
        // Allegati staccati ma non ancora persistiti: il cleanup sotto li tratta come aggiunte di sessione.
        var staccati = _rimozioniAllegati.Abbandona();
        if (!ModificheNonSalvate || ChangeRequest == null || ChangeRequest.Uid == Guid.Empty ||
            string.IsNullOrWhiteSpace(ChangeRequest.Id) || !_state.CartellaValida) return;

        var working = EntityStateFingerprint.Clone(ChangeRequest);
        AttachmentRemovalQueue.UnisciAllaCopiaDiLavoro(working.Allegati, staccati);
        var id = ChangeRequest.Id;
        var expectedUid = ChangeRequest.Uid;
        var paths = _state.Paths;
        var repository = _state.ChangeRequests;

        // ConfirmService invoca questa callback solo dopo un Sì sul dialogo scoped a QUESTO editor. La
        // navigazione/chiusura avviene subito dopo il ritorno della conferma; rimandando di un giro UI evitiamo
        // di modificare file mentre controlli/binding della pagina stanno ancora leggendo la working copy.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                repository.RefreshIndex();
                var authoritative = repository.GetById(id);
                if (authoritative != null && authoritative.Uid != expectedUid)
                {
                    // L'ID ora appartiene a un'altra entità: nessuna cleanup locale può assumere ownership del
                    // suo bucket. Lasciare eventuale staging è il failure mode sicuro.
                    CrashLogService.Registra("Cleanup allegati CR scartata",
                        new IOException($"La CR {id} ha cambiato UID durante la sessione; cleanup binari annullata."));
                    return;
                }

                DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, authoritative);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Cleanup allegati CR scartata {id}", ex);
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}
