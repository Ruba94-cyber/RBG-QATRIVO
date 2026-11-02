using Avalonia.Controls;

namespace TestLoom.App.Views;

/// <summary>
/// Finestra di avvio mostrata SOLO quando il bootstrap di AppState supera una soglia breve (vedi
/// App.axaml.cs, RunStartupAsync): su un workspace con indice locale già presente il bootstrap dura pochi
/// secondi e questa finestra non viene mai creata, per non far sfarfallare nulla. Quando invece serve
/// davvero (indice locale assente/scaduto su un workspace da centinaia di migliaia di elementi, misurato
/// fino a 5-7 minuti senza alcuna finestra visibile) resta aperta e reattiva finché App.axaml.cs non la
/// chiude esplicitamente dopo aver mostrato la finestra principale.
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
