using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Presentation-only, local status of browser handoffs initiated from this Test.
/// A pending receipt is never displayed as an TICKET created or linked by Ticketing.
/// No network request, persisted schema update, token or REST write is involved.
/// </summary>
public partial class TestCaseEditorViewModel
{
    public ObservableCollection<string> EstaStatoRighe { get; } = new();
    public int EstaAtteseLocali { get; private set; }
    public int EstaCollegamentiDitm { get; private set; }
    public bool EstaStatoNonDisponibile { get; private set; }
    public string EstaStatoSintesi { get; private set; } = "";
    public string EstaStatoNota { get; private set; } = "";

    [RelayCommand]
    public void AggiornaStatoEstaLocale()
    {
        if (_disposto) return;
        var loc = LocalizationService.Instance;
        try
        {
            // There is deliberately no attempt to infer a receipt from a coincidentally
            // related NC/CR: only OriginTestUid and OriginTestId identify this Test.
            var unreadableReceipt = false;
            IReadOnlyList<PolarionCreationReceiptStore.Receipt> receipts = _state.CartellaValida
                ? new PolarionCreationReceiptStore(_state.Paths).PendingBrowserReceipts(out unreadableReceipt)
                : Array.Empty<PolarionCreationReceiptStore.Receipt>();
            // Background GET reconciliation can write a linked ticket after this editor
            // was opened; show the persisted reference without changing unsaved Test data.
            var persisted = !IsNew ? _state.TestCases.GetByUid(TestCase.Uid) : null;
            IEnumerable<TestLoom.App.Models.TicketRif> linked =
                persisted is not null &&
                string.Equals(persisted.Id, TestCase.Id, StringComparison.OrdinalIgnoreCase)
                    ? persisted.TicketCollegati
                    : Array.Empty<TestLoom.App.Models.TicketRif>();
            var summary = PolarionTestHandoffStatusService.Summarize(
                TestCase, receipts, linked, _state.Config.OperatoreAttivo);
            EstaAtteseLocali = summary.PendingCount;
            EstaCollegamentiDitm = summary.LinkedCount;
            EstaStatoNonDisponibile = unreadableReceipt;

            EstaStatoRighe.Clear();
            foreach (var item in summary.PendingHandoffs)
            {
                var kind = item.IsCr ? "CR" : "NC";
                var state = item.VerifiedTicketId is { Length: > 0 } ticket
                    ? loc.Scegli(ticket + " identificato; collegamento TestLoom da completare",
                        ticket + " identified; TestLoom link still pending",
                        ticket + " identifié ; liaison TestLoom en attente")
                    : loc.Scegli("In attesa di salvataggio/verifica in Ticketing",
                        "Awaiting save/verification in Ticketing",
                        "En attente de sauvegarde/vérification dans Ticketing");
                EstaStatoRighe.Add(item.SourceId + " (" + kind + ") · " + state);
            }
            foreach (var ticketId in summary.LinkedTicketIds)
                EstaStatoRighe.Add(ticketId + " · " +
                    loc.Scegli("Collegato nel Test TestLoom", "Linked in TestLoom Test", "Lié dans le test TestLoom"));

            EstaStatoSintesi = string.Format(loc.Scegli(
                unreadableReceipt ? "Almeno {0} richieste locali visibili · {1} TICKET collegati nel Test" : "{0} richieste locali in attesa · {1} TICKET collegati nel Test",
                unreadableReceipt ? "At least {0} visible local handoffs · {1} TICKET linked in the Test" : "{0} local handoffs pending · {1} TICKET linked in the Test",
                unreadableReceipt ? "Au moins {0} demandes locales visibles · {1} TICKET liés dans le test" : "{0} demandes locales en attente · {1} TICKET liés dans le test"),
                EstaAtteseLocali, EstaCollegamentiDitm);
            EstaStatoNota = loc.Scegli(
                "Stato delle richieste avviate da questo Test con l'operatore corrente su questo PC. Una bozza nel browser non è un TICKET salvato. Il monitor verifica via GET quando configurato; Aggiorna rilegge solo lo stato locale.",
                "Status of handoffs started from this Test by the current operator on this PC. A browser draft is not a saved TICKET. The monitor verifies via GET when configured; Refresh reads local state only.",
                "État des demandes lancées depuis ce test par l'opérateur courant sur ce PC. Un brouillon du navigateur n'est pas un TICKET enregistré. Le suivi vérifie par GET si configuré ; Actualiser relit uniquement l'état local.");
            if (unreadableReceipt)
                EstaStatoNota += " " + loc.Scegli(
                    "Attenzione: alcune ricevute locali non sono leggibili; il conteggio è parziale.",
                    "Warning: some local receipts cannot be read; the count is incomplete.",
                    "Attention : certains reçus locaux ne sont pas lisibles ; le décompte est incomplet.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            EstaAtteseLocali = 0;
            EstaCollegamentiDitm = 0;
            EstaStatoNonDisponibile = true;
            EstaStatoRighe.Clear();
            EstaStatoSintesi = loc.Scegli("Stato TICKET locale non disponibile",
                "Local TICKET status unavailable", "État TICKET local indisponible");
            EstaStatoNota = loc.Scegli(
                "Impossibile leggere le ricevute locali; nessun collegamento è stato modificato.",
                "Local receipts cannot be read; no link was modified.",
                "Impossible de lire les reçus locaux ; aucune liaison n'a été modifiée.");
            DiagnosticTraceService.Registra("Test.TICKET.Status", "read-failed kind=" + ex.GetType().Name);
        }
        OnPropertyChanged(nameof(EstaAtteseLocali));
        OnPropertyChanged(nameof(EstaCollegamentiDitm));
        OnPropertyChanged(nameof(EstaStatoNonDisponibile));
        OnPropertyChanged(nameof(EstaStatoSintesi));
        OnPropertyChanged(nameof(EstaStatoNota));
        OnPropertyChanged(nameof(HaStatoEstaRapido));
    }
}
