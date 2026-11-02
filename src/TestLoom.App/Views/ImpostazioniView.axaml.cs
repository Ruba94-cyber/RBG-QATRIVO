using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class ImpostazioniView : UserControl
{
    public ImpostazioniView() => InitializeComponent();

    /// <summary>Finestra proprietaria di un dialogo aperto ORA, o null se non c'e' una finestra visibile. Va riletta dopo
    /// ogni await: la finestra puo' essere stata nascosta (riduzione nel tray) o chiusa nel frattempo.</summary>
    public Window? ProprietarioVisibile() =>
        TopLevel.GetTopLevel(this) is Window { IsVisible: true } finestra ? finestra : null;

    /// <summary>
    /// Il comando e intenzionalmente opt-in. Il pinning dei dati operativi puo richiedere
    /// download e spazio su disco: non viene eseguito durante startup o refresh automatici.
    /// </summary>
    private async void OnMantieniDatiOperativi(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImpostazioniViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var dialog = ConfirmDialog.Crea(
                LocalizationService.Instance.Traduci("ImpDatiOperativiTitolo"),
                LocalizationService.Instance.Traduci("ImpDatiOperativiMessaggio"),
                LocalizationService.Instance.Traduci("ProcediBtn"), LocalizationService.Instance.Traduci("Annulla"));
            if (await dialog.ShowDialog<bool>(owner))
                await vm.RichiediDatiLocaliAsync();
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Conferma pinning dati operativi", ex);
        }
    }

    private async void OnAttivaSolaLetturaLocale(object? sender, RoutedEventArgs e)
    {
        await ConfermaModalitaLocale(true);
    }

    private async void OnDisattivaSolaLetturaLocale(object? sender, RoutedEventArgs e)
    {
        await ConfermaModalitaLocale(false);
    }

    private async System.Threading.Tasks.Task ConfermaModalitaLocale(bool enabled)
    {
        if (DataContext is not ImpostazioniViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var msg = enabled
                ? LocalizationService.Instance.Traduci("ImpModalitaBloccoMsg")
                : vm.EModalitaRete
                    // 6.6.1 (g): il riferimento a OneDrive compare solo nella modalità OneDrive/SharePoint.
                    ? LocalizationService.Instance.Traduci("ImpModalitaRiabilitaReteMsg")
                    : LocalizationService.Instance.Traduci("ImpModalitaRiabilitaLocaleMsg");
            var dialog = ConfirmDialog.Crea(
                enabled ? LocalizationService.Instance.Traduci("ImpAttivaVisLocaleTitolo") : LocalizationService.Instance.Traduci("ImpDisattivaVisLocaleTitolo"),
                msg, LocalizationService.Instance.Traduci("ConfermaSemplice"), LocalizationService.Instance.Traduci("Annulla"));
            if (await dialog.ShowDialog<bool>(owner))
                vm.ImpostaModalitaLocaleSolaLettura(enabled);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Cambio modalità visualizzatore locale", ex);
        }
    }

    /// <summary>
    /// Diagnostica locale non mutante: l'inventario non verifica la sincronizzazione
    /// remota né scarica gli allegati Files On-Demand.
    /// </summary>
    private async void OnCentroIntegrita(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImpostazioniViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            await new WorkspaceDiagnosticsWindow(vm.CartellaAttuale, vm.PercorsoBackupEsterno)
                .ShowDialog(owner);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Apertura Centro integrità", ex);
        }
    }

    /// <summary>
    /// La finestra legge SOLO bozze locali del workspace corrente; nessun restore,
    /// overwrite, sincronizzazione o cancellazione. Accessibile anche in modalità Viewer.
    /// </summary>
    private async void OnEsaminaBozze(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImpostazioniViewModel vm) return;
        if (ProprietarioVisibile() is null) return;
        try
        {
            var root = vm.CartellaAttuale;
            if (string.IsNullOrWhiteSpace(root)) return;
            // 6.6.1 (correzione 1): la verifica della radice ha un tetto di tempo e avviene fuori dal thread UI.
            var esito = await System.Threading.Tasks.Task.Run(() => TestLoom.App.Services.RadiceCondivisaSonda.Verifica(root));
            if (esito is not (TestLoom.App.Services.RadiceCondivisaSonda.Esito.Presente or TestLoom.App.Services.RadiceCondivisaSonda.Esito.Offline)) return;
            // Il proprietario va riletto DOPO l'attesa: la verifica puo' durare fino al suo tetto e nel frattempo la finestra
            // puo' essere stata nascosta o chiusa. ShowDialog su un proprietario nascosto non apre nulla: senza proprietario
            // visibile la finestra si apre comunque, come finestra non modale.
            var proprietarioAttuale = ProprietarioVisibile();
            if (proprietarioAttuale is null) new FailedSaveDraftsWindow(root).Show();
            else await new FailedSaveDraftsWindow(root).ShowDialog(proprietarioAttuale);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Finestra bozze di emergenza", ex);
        }
    }

    /// <summary>Stesso schema di NonConformitaView/AuditView.OnEsportaExcel (feedback utente 2026-09-17,
    /// "è presente un log da poter inviare"): picker nativo di salvataggio, mai un percorso fisso automatico.</summary>
    private async void OnEsportaLogDiagnostico(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImpostazioniViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Scegli("Salva pacchetto log diagnostico", "Save diagnostic log package", "Enregistrer le paquet de diagnostic"),
                SuggestedFileName = $"TestLoom_Diagnostica_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "zip",
                FileTypeChoices = new[] { new FilePickerFileType("Zip") { Patterns = new[] { "*.zip" } } }
            });

            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(percorso)) await vm.EsportaLogDiagnosticoVerso(percorso);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione pacchetto log diagnostico", ex); }
    }
}
