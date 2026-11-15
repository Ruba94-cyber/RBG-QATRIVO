using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Finestra mostrata alla chiusura mentre OneDrive carica le ultime modifiche ("sincronizzazione in corso"). Barra indeterminata,
/// conteggio dei file ancora da caricare, pulsante "Chiudi senza attendere". A tempo scaduto (o dopo l'annullamento) mostra l'avviso
/// che restano file da caricare, ritentati al prossimo avvio. Costruita nel codice come le altre finestre di servizio.
/// </summary>
public sealed class SincronizzazioneUscitaWindow : Window
{
    private readonly TextBlock _stato;
    private readonly ProgressBar _barra;
    private readonly Button _azione;
    private readonly CancellationTokenSource _cts;

    public SincronizzazioneUscitaWindow(CancellationTokenSource cts)
    {
        _cts = cts;
        var loc = LocalizationService.Instance;
        Title = loc.Traduci("OneDriveUscitaTitolo");
        Width = 460;
        Height = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AutomationProperties.SetAutomationId(this, "SincUscita_Window");

        var titolo = new TextBlock { Text = loc.Traduci("OneDriveUscitaTesto"), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _stato = new TextBlock { Text = loc.Traduci("OneDriveVerificaTesto"), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        AutomationProperties.SetAutomationId(_stato, "SincUscita_Stato_Text");
        _barra = new ProgressBar { IsIndeterminate = true, Height = 10 };
        AutomationProperties.SetAutomationId(_barra, "SincUscita_Barra");
        _azione = new Button { Content = loc.Traduci("OneDriveUscitaChiudiSubito"), HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(_azione, "SincUscita_Azione_Button");
        _azione.Click += (_, _) =>
        {
            if (_terminata) Close();
            else _cts.Cancel();
        };

        Content = new StackPanel { Spacing = 12, Margin = new Thickness(20), Children = { titolo, _barra, _stato, _azione } };
        // L'utente non può chiudere la finestra con la X durante l'attesa: userebbe il pulsante (annulla in modo esplicito).
        Closing += (_, e) => { if (!_terminata && !_chiusuraProgrammatica) { e.Cancel = true; _cts.Cancel(); } };
    }

    private bool _terminata;
    private bool _chiusuraProgrammatica;

    public void Aggiorna(AvanzamentoSyncOneDrive a)
    {
        if (_terminata) return;
        _stato.Text = a.DaCaricare > 0
            ? string.Format(LocalizationService.Instance.Traduci("OneDriveUscitaFmt"), a.DaCaricare)
            : OneDriveSync.TestoAvanzamento(a);
    }

    /// <summary>Fine attesa: mostra l'avviso (se restano file) e lascia il pulsante "Chiudi" per proseguire. Senza avviso si chiude da sola.</summary>
    public void Termina(string? avviso)
    {
        _terminata = true;
        if (avviso == null)
        {
            _chiusuraProgrammatica = true;
            Close();
            return;
        }
        _barra.IsVisible = false;
        _stato.Text = avviso;
        _stato.Foreground = Brushes.DarkOrange;
        _azione.Content = LocalizationService.Instance.Traduci("OneDriveUscitaChiudi");
    }

    public void ChiudiProgrammaticamente()
    {
        _chiusuraProgrammatica = true;
        Close();
    }
}
