using Avalonia.Controls;
using Avalonia.Interactivity;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Finestra "Componente e funzionalità" di un veicolo (vedi AssociazioniVeicoloViewModel). Chiudendo con modifiche non
/// salvate chiede conferma di abbandono; in ogni caso i lock della sessione di modifica vengono rilasciati.</summary>
public partial class AssociazioniVeicoloWindow : Window
{
    private bool _chiusuraConfermata;
    private bool _chiusuraInCorso;

    public AssociazioniVeicoloWindow()
    {
        InitializeComponent();
        AdattamentoDialogo.Registra(this);
    }

    private void OnChiudi(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_chiusuraConfermata || DataContext is not AssociazioniVeicoloViewModel vm)
        {
            base.OnClosing(e);
            return;
        }
        if (e.CloseReason is WindowCloseReason.OwnerWindowClosing or WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            // App/owner shutdown gets one bounded best-effort release in Dispose, never an endless close veto.
            vm.Dispose();
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_chiusuraInCorso) return;
        _chiusuraInCorso = true;
        ConfermaEChiudi(vm);
    }

    private async void ConfermaEChiudi(AssociazioniVeicoloViewModel vm)
    {
        try
        {
            if (!await vm.ConfermaChiusuraAsync()) return;
            vm.Dispose();
            _chiusuraConfermata = true;
            Close();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Chiusura associazioni veicolo", ex);
        }
        finally
        {
            _chiusuraInCorso = false;
        }
    }
}
