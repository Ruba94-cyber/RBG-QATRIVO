using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Cleanup degli allegati della segnalazione soltanto dopo una conferma scoped di abbandono, stesso schema di
/// ChangeRequestEditorViewModel.Abandonment. Gli upload possono precedere il primo salvataggio, quindi per una
/// nuova NC si pulisce il bucket temporaneo solo se non esiste una bozza locale che lo possa recuperare.
/// Crash/kill non arrivano qui e lasciano intatta la recovery.
/// </summary>
public partial class SegnalazioneReteEditorViewModel : IConfirmedAbandonmentParticipant
{
    void IConfirmedAbandonmentParticipant.PrepareConfirmedAbandonmentCleanup()
    {
        // Allegati staccati ma non ancora persistiti: il cleanup sotto li tratta come aggiunte di sessione.
        var staccati = _rimozioniAllegati.Abbandona();
        if (!ModificheNonSalvate || Nc.Uid == Guid.Empty || !_state.CartellaValida) return;

        var nuova = string.IsNullOrWhiteSpace(Nc.Id);
        if (nuova && HaBozzaLocale()) return;

        var working = EntityStateFingerprint.Clone(Nc);
        AttachmentRemovalQueue.UnisciAllaCopiaDiLavoro(working.Allegati, staccati);
        var id = Nc.Id;
        var expectedUid = Nc.Uid;
        var paths = _state.Paths;
        var repository = _state.NonConformita;

        // ConfirmService invoca questa callback solo dopo un Sì sul dialogo scoped a QUESTO editor. La
        // navigazione/chiusura avviene subito dopo il ritorno della conferma; rimandando di un giro UI evitiamo
        // di modificare file mentre controlli/binding della pagina stanno ancora leggendo la working copy.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                repository.RefreshIndex();
                var authoritative = nuova ? repository.GetByUid(expectedUid) : repository.GetById(id);
                if (authoritative != null && authoritative.Uid != expectedUid)
                {
                    // L'ID ora appartiene a un'altra entità: nessuna cleanup locale può assumere ownership del
                    // suo bucket. Lasciare eventuale staging è il failure mode sicuro.
                    CrashLogService.Registra("Cleanup allegati segnalazione scartata",
                        new IOException($"La NC {id} ha cambiato UID durante la sessione; cleanup binari annullata."));
                    return;
                }

                DraftAttachmentCleanupService.ReconcileAfterExplicitDiscard(paths, working, authoritative);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Cleanup allegati segnalazione scartata {id}", ex);
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private bool HaBozzaLocale()
    {
        const int pagina = 100;
        for (var skip = 0; ; skip += pagina)
        {
            var bozze = FailedSaveDraftInspector.ListPage(_state.Paths.Root, skip, pagina,
                domain: "nonconformita", entityUid: Nc.Uid);
            if (bozze.Count == 0) return false;
            if (bozze.Any(b => b.IsReadable && b.EntityUid == Nc.Uid)) return true;
        }
    }
}
