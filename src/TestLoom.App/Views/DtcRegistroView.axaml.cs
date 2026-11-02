using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class DtcRegistroView : UserControl
{
    public DtcRegistroView() => InitializeComponent();

    /// <summary>Selezione multipla della griglia: il ViewModel la usa per associare in blocco più righe a un requisito Extra.</summary>
    private void OnSelezioneCambiata(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is DtcRegistroViewModel vm && sender is DataGrid griglia)
            vm.ImpostaRigheSelezionate(griglia.SelectedItems.OfType<RigaDtcRegistro>());
    }

    private async void OnEsportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DtcRegistroViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            // Dialogo unico di export: colonne e filtri (che partono da quelli a video e, confermando, restano applicati alla pagina).
            if (topLevel is not Window finestraProprietaria) return;
            var opzioni = await EsportaOpzioniDialogo.Apri(finestraProprietaria, vm.StatoPerExport, vm.CreaDescrittoreExport());
            if (opzioni == null) return;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Traduci("DtcRegistroEsportaExcelTitolo"),
                SuggestedFileName = $"TestLoom_guasto_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "xlsx",
                FileTypeChoices = new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } }
            });

            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(percorso)) await vm.EsportaExcelVerso(percorso, opzioni);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione registro guasto Excel", ex); }
    }
}
