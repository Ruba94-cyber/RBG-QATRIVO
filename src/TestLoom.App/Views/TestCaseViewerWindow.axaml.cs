using Avalonia.Controls;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Finestra non modale per visualizzare un test correlato senza abbandonare quello attualmente
/// aperto (feedback utente 2026-08-21): stesso pattern non modale di TestFormPreviewWindow (Show, non
/// ShowDialog) — resta aperta accanto alla finestra principale. Si apre sempre in sola visualizzazione (il
/// comportamento di default di TestCaseEditorViewModel per un test esistente, nessun cambiamento necessario
/// lì); il bottone "Modifica" al suo interno non acquisisce un lock qui, richiede invece alla finestra
/// principale di scartare il test che ha aperto e passare a modificare questo (vedi
/// TestCaseEditorViewModel.RichiediModificaCorrelato), dopodiché questa finestra si chiude da sola.</summary>
public partial class TestCaseViewerWindow : Window
{
    public TestCaseViewerWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this, grande: true);
    }

    public static void Apri(Window owner, TestCaseEditorViewModel vmPrincipale, string testCaseId)
    {
        var w = new TestCaseViewerWindow { Title = string.Format(LocalizationService.Instance.Traduci("FinestraVisualizzazioneTestTitoloFmt"), testCaseId) };

        var vmSecondario = vmPrincipale.CreaVisualizzatoreCorrelato(testCaseId, async () =>
        {
            if (await vmPrincipale.RichiediModificaCorrelato(testCaseId)) w.Close();
        });
        w.DataContext = vmSecondario;
        w.Closed += (_, _) => vmSecondario.Dispose();

        w.Show(owner);
    }
}
