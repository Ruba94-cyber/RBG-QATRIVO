using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;

namespace TestLoom.App.ViewModels;

/// <summary>
/// Parte OneDrive del MainViewModel: pulsante "Riprova" del banner di sola lettura (quando all'avvio OneDrive non era allineato)
/// e radice per la sincronizzazione di chiusura. La logica sta in <see cref="OneDriveSync"/>; qui solo stato e comando.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Vero se il workspace è in sola lettura perché OneDrive non era allineato all'avvio: mostra "Riprova".</summary>
    [ObservableProperty] private bool mostraRiprovaOneDrive;
    [ObservableProperty] private bool riprovaOneDriveInCorso;
    [ObservableProperty] private string testoRiprovaOneDrive = "";

    /// <summary>Cartella dati corrente, per la sincronizzazione di chiusura (MainWindow).</summary>
    public string? RadiceCondivisaCorrente => _state.Config.CartellaCondivisa;

    private void AggiornaStatoOneDrive() =>
        MostraRiprovaOneDrive = OneDriveSyncGate.Attivo(_state.Config.CartellaCondivisa);

    [RelayCommand]
    private async Task RiprovaOneDrive()
    {
        var radice = _state.Config.CartellaCondivisa;
        if (RiprovaOneDriveInCorso || string.IsNullOrWhiteSpace(radice)) return;
        RiprovaOneDriveInCorso = true;
        TestoRiprovaOneDrive = LocalizationService.Instance.Traduci("OneDriveVerificaTesto");
        try
        {
            // Progress costruito sul thread UI: i Report arrivano già marshallati.
            var progress = new Progress<AvanzamentoSyncOneDrive>(a => TestoRiprovaOneDrive = OneDriveSync.TestoAvanzamento(a));
            var risultato = await OneDriveSync.VerificaEApplicaAsync(radice, progress);
            // Allineato: il blocco è sciolto e la rilettura riporta il workspace operativo (prova di scrittura inclusa).
            if (risultato.Sicuro || !OneDriveSyncGate.Attivo(radice)) await AggiornaOra();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Riprova sincronizzazione OneDrive", ex);
        }
        finally
        {
            RiprovaOneDriveInCorso = false;
            TestoRiprovaOneDrive = "";
            AggiornaBannerCondivisione();
        }
    }
}
