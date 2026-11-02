using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class VeicoliView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private VeicoliViewModel? _vmCorrente;

    // Vero se GammeContenuto.DataContext e' gia' stato agganciato al GammaViewModel annidato di _vmCorrente
    // (perf: profilo rivisita 2026-09-25): costruire quel nested costa da solo circa 140ms del ctor di
    // VeicoliViewModel, quasi tutto sprecato quando l'Expander resta collassato per l'intera visita (caso
    // comune). Si aggancia solo alla PRIMA apertura reale, mai a ogni cambio di DataContext.
    private bool _gammeCollegata;

    public VeicoliView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        GammeExpander.PropertyChanged += OnGammeExpanderPropertyChanged;
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        MasterDataRenameEditGuard.OnBeginningEdit(sender, e);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as VeicoliViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;

        // Ogni navigazione ricrea un VeicoliViewModel nuovo (ImpostazioniViewModel.SostituisciPaginaCorrente):
        // l'agganciamento precedente non vale piu' per il nuovo VM, altrimenti GammeContenuto resterebbe legato
        // al GammaViewModel della pagina abbandonata invece di ripartire pigro per questa nuova visita.
        _gammeCollegata = false;
        GammeContenuto.DataContext = null;
        if (GammeExpander.IsExpanded) CollegaGamme();
    }

    private void OnGammeExpanderPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Expander.IsExpandedProperty && GammeExpander.IsExpanded) CollegaGamme();
    }

    /// <summary>Costruisce (se non gia' fatto) il GammaViewModel annidato e lo aggancia come DataContext del
    /// solo contenuto dell'Expander — l'accesso a vm.Gamme e' quello che, alla prima chiamata, materializza
    /// davvero l'editor (VeicoliViewModel.Gamme e' pigro, vedi MasterDataEditors.cs).</summary>
    private void CollegaGamme()
    {
        if (_gammeCollegata || _vmCorrente == null) return;
        _gammeCollegata = true;
        GammeContenuto.DataContext = _vmCorrente.Gamme;
    }

    private void OnRigaCreata(Veicolo item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>Apre "Componente e funzionalità" per il veicolo selezionato: vista dal veicolo sui componenti montate e sulle funzionalità
    /// applicabili. La finestra gestisce da sé lettura, lock e salvataggio; qui resta solo l'apertura.</summary>
    private void OnApriAssociazioniVeicolo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VeicoliViewModel vm || vm.ElementoSelezionato is not { } veicolo) return;
        try
        {
            var finestra = new AssociazioniVeicoloWindow { DataContext = vm.CreaAssociazioniVeicolo(veicolo.Nome) };
            if (TopLevel.GetTopLevel(this) is Window proprietaria) _ = finestra.ShowDialog(proprietaria);
            else finestra.Show();
        }
        catch (Exception ex)
        {
            vm.MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("SalvataggioNonRiuscitoModificheQuiFmt"), ex.Message);
            CrashLogService.Registra("Apertura associazioni veicolo", ex);
        }
    }

    /// <summary>Stesso schema di TestCaseEditorView.OnSceglieFileAllegato: picker nativo, "async void" perché
    /// non c'è un try/catch a monte per un comando (l'utente ha solo cliccato un pulsante, non serve un
    /// gestore dedicato all'errore oltre a non far crashare l'app se il picker non è disponibile).</summary>
    private async void OnImportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VeicoliViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            if (!await ImportExcelGuidaService.Chiedi(ImportExcelCatalogo.Veicoli)) return;
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
            CrashLogService.Registra("Importazione Excel Veicoli", ex);
        }
    }
}
