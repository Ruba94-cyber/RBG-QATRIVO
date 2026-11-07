using Avalonia.Controls;

namespace TestLoom.App.Views;

/// <summary>
/// Finestra di avvio mostrata SUBITO, prima che AppState inizi l'indicizzazione
/// del workspace: elimina la precedente attesa intenzionale di 500 ms, durante
/// la quale l'app sembrava non partire. Resta visibile e reattiva durante il
/// caricamento dei dati e viene chiusa dopo MainWindow.Show(). Le pagine
/// secondarie vengono precaricate solo in seguito, a finestra principale aperta.
///
/// AutomationId="AvvioWindow" e Title diverso da "TestLoom": TestLoomApplication.WaitForMainWindow (suite UI) cerca
/// finestre con titolo "TestLoom"/"TestLoom*" ed esclude esplicitamente questo AutomationId, cosi' un collaudo non
/// puo' agganciarsi per errore a questa finestra transitoria invece che alla finestra principale reale.
/// </summary>
public partial class AvvioWindow : Window
{
    public AvvioWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
        // Transitoria per definizione: l'utente non deve poterla chiudere durante il bootstrap (lascerebbe il
        // processo senza alcuna finestra, con ShutdownMode=OnLastWindowClose, prima che AppState sia pronto).
        // App.axaml.cs la chiude programmaticamente a bootstrap concluso, ignorando questo guard.
        Closing += (_, e) => { if (!_chiusuraConsentita) e.Cancel = true; };
    }

    private bool _chiusuraConsentita;

    public void ConsentiChiusura()
    {
        _chiusuraConsentita = true;
    }
}
