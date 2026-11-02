using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class StrumentiView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private StrumentiViewModel? _vmCorrente;

    public StrumentiView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        MasterDataRenameEditGuard.OnBeginningEdit(sender, e);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as StrumentiViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;
    }

    private void OnRigaCreata(StrumentoDiagnostico item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>Stesso schema di TestCaseEditorView.OnSceglieFileAllegato: picker nativo, "async void" perché
    /// non c'è un try/catch a monte per un comando (l'utente ha solo cliccato un pulsante, non serve un
    /// gestore dedicato all'errore oltre a non far crashare l'app se il picker non è disponibile).</summary>
    private async void OnImportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StrumentiViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            if (!await ImportExcelGuidaService.Chiedi(ImportExcelCatalogo.Strumenti)) return;
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("EcuScegliFileExcel"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType> { new("Excel") { Patterns = new[] { "*.xlsx", "*.xls" } } }
            });

            var percorso = file.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(percorso)) return;
            var richiesta = await ImportExcelOpzioniWindow.ChiediOpzioni(topLevel as Window, percorso);
            if (richiesta != null && vm.ImportaDaExcelCommand.CanExecute(richiesta))
                vm.ImportaDaExcelCommand.Execute(richiesta);
        }
        catch (Exception ex)
        {
            // Prima finiva SOLO nel crash log (feedback utente 2026-09-16): un file scelto che non è un
            // Excel leggibile (es. un .xls storico rinominato .xlsx) sembrava "non aver fatto niente",
            // nessun messaggio a video. Stesso formato del comando ImportaDaExcel, per coerenza.
            vm.MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImportExcelErroreFmt"), ex.Message);
            CrashLogService.Registra("Importazione Excel Strumenti", ex);
        }
    }

    /// <summary>Editor dei campi personalizzati per uno strumento (Addendum §5/§8, feedback QA): ora una vera
    /// tabella condivisa con TestCaseEditorView (vedi CampiStrumentoEditorWindow), non più duplicata qui.</summary>
    private async void OnGestisciCampi(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not StrumentiViewModel { InModifica: true }
            || sender is not Button button || button.Tag is not StrumentoDiagnostico strumento) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        await UiExceptionGuard.Esegui(() => CampiStrumentoEditorWindow.Apri(owner, strumento), "Apertura campi strumento");
    }
}
