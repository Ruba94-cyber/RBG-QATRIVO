using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class RequisitiView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private RequisitiViewModel? _vmCorrente;

    public RequisitiView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Il layout colonne salvato viene riapplicato in differita all'attach: la visibilita si riafferma anche li.
        Loaded += (_, _) => AggiornaVisibilitaColonne();
        AttachedToVisualTree += (_, _) => AggiornaVisibilitaColonne();
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        MasterDataRenameEditGuard.OnBeginningEdit(sender, e);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as RequisitiViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;
        AggiornaVisibilitaColonne();
    }

    /// <summary>Il tipo e fisso per lista: i veicoli Extra hanno senso solo nella lista Extra. La colonna e marcata
    /// non applicabile cosi il layout colonne salvato (TableLayoutPreferences) non la riporta in vista.</summary>
    private void AggiornaVisibilitaColonne()
    {
        var extra = _vmCorrente?.ESchedaD4d ?? false;
        foreach (var c in GridPrincipale.Columns.Where(c => c.Tag as string is "ColSottoveicoliD4d" or "ColVeicoliEcuD4d"))
        {
            var eraNascosta = TableLayoutPreferences.GetNonApplicabile(c);
            TableLayoutPreferences.SetNonApplicabile(c, !extra);
            if (!extra) c.IsVisible = false;
            else if (eraNascosta) c.IsVisible = true;   // altrimenti resta la scelta dell'utente dal menu colonne
        }
    }

    private void OnScegliVeicoliEcu(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequisitiViewModel { InModifica: true } vm ||
            sender is not Button { Tag: Requisito requisito } button || !RequisitoApplicabilita.ED4D(requisito)) return;

        var panel = CreaPannelloVeicoliEcu(vm, requisito, button);
        new Flyout { Content = new ScrollViewer { MaxHeight = 420, Content = panel } }.ShowAt(button);
    }

    internal static StackPanel CreaPannelloVeicoliEcu(RequisitiViewModel vm, Requisito requisito, Button button)
    {
        // Conserva e rende rimovibili anche le associazioni a voci successivamente archiviate.
        var associazioni = requisito.VeicoliEcuD4D ?? new List<RequisitoVeicoloEcu>();
        var veicoli = (RequisitoApplicabilita.SottoveicoliDi(requisito).Count > 0
                ? RequisitoApplicabilita.SottoveicoliDi(requisito) : vm.VeicoliAnagrafica)
            .Concat(associazioni.Select(a => a.Veicolo)).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var ecuPerVeicolo = vm.EcuMontatePerVeicolo();
        var panel = new StackPanel { Spacing = 6, MinWidth = 260 };
        foreach (var veicolo in veicoli)
        {
            var espandi = new Expander { Header = veicolo };
            var elenco = new StackPanel { Spacing = 3 };
            var montate = ecuPerVeicolo.TryGetValue(veicolo, out var valori) ? valori : new List<string>();
            var componente = montate.Concat(associazioni.Where(a => a.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase)).Select(a => a.Componente))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            foreach (var nome in componente)
            {
                var giaAssociata = associazioni.Any(a => a.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase) && a.Componente.Equals(nome, StringComparison.OrdinalIgnoreCase));
                var veicoloAmmesso = RequisitoApplicabilita.SiApplicaAlSottoveicolo(requisito, veicolo);
                var selezionabile = veicoloAmmesso && montate.Contains(nome, StringComparer.OrdinalIgnoreCase);
                var check = new CheckBox { Content = nome, IsChecked = giaAssociata, IsEnabled = selezionabile || giaAssociata };
                check.IsCheckedChanged += (_, _) =>
                {
                    associazioni.RemoveAll(a => a.Veicolo.Equals(veicolo, StringComparison.OrdinalIgnoreCase) && a.Componente.Equals(nome, StringComparison.OrdinalIgnoreCase));
                    if (check.IsChecked == true) associazioni.Add(new RequisitoVeicoloEcu { Veicolo = veicolo, Componente = nome });
                    requisito.VeicoliEcuD4D = associazioni.Count == 0 ? null : associazioni;
                    if (!selezionabile && check.IsChecked != true) check.IsEnabled = false;
                    button.Content = RequisitoDisplayConverter.Instance.Convert(requisito, typeof(string), "veicoliecu", System.Globalization.CultureInfo.CurrentCulture);
                };
                elenco.Children.Add(check);
            }
            espandi.Content = elenco;
            panel.Children.Add(espandi);
        }
        if (veicoli.Count == 0) panel.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("RequisitiNessunVeicoloMsg") });
        return panel;
    }

    private void OnRigaCreata(Requisito item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>
    /// Extra usa una selezione multipla di sottoveicoli reali. Il catalogo viene letto dalla stessa cartella
    /// condivisa configurata dall'app; i target gia salvati vengono mantenuti tra le opzioni anche se nel
    /// frattempo il sottoveicolo e stato archiviato, cosi una semplice riapertura non fa perdere storico.
    /// </summary>
    private async void OnScegliSottoveicoli(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequisitiViewModel vm || !vm.InModifica ||
            sender is not Button { Tag: Requisito requisito }) return;

        if (!RequisitoApplicabilita.ED4D(requisito))
        {
            vm.MessaggioErrore = LocalizationService.Instance.Traduci("RequisitoVeicoliSoloD4d");
            return;
        }

        try
        {
            var config = new AppConfigService().Load();
            var disponibili = new List<string>();
            // 6.6.1 (correzione 1): controllo non bloccante sull'ultimo esito della sonda con tetto di tempo.
            if (!string.IsNullOrWhiteSpace(config.CartellaCondivisa) && TestLoom.App.Services.RadiceCondivisaSonda.PresenteUi(config.CartellaCondivisa))
            {
                var paths = new DataPaths(config.CartellaCondivisa);
                disponibili.AddRange(new MasterDataRepository<Veicolo>(paths.VeicoliFile).Load()
                    .Where(v => !v.Archiviata).Select(v => v.Nome));
            }

            disponibili.AddRange(RequisitoApplicabilita.SottoveicoliDi(requisito));
            var voci = disponibili.Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Select(v => new VoceSelezionabile { Valore = v, TestoPrincipale = v, TestoRicerca = v })
                .ToList();

            if (voci.Count == 0)
            {
                vm.MessaggioErrore = LocalizationService.Instance.Traduci("RequisitiNessunVeicoloMsg");
                return;
            }

            var current = string.Join(", ", RequisitoApplicabilita.SottoveicoliDi(requisito));
            var scelte = await SelezionaDialogService.ScegliMultipli(
                string.IsNullOrWhiteSpace(current)
                    ? LocalizationService.Instance.Traduci("RequisitiVeicoliD4dTitolo")
                    : string.Format(LocalizationService.Instance.Traduci("RequisitiVeicoliD4dAttualiFmt"), current),
                voci);
            if (scelte.Count == 0) return; // annullamento: non cancella per errore la selezione esistente

            var selezionati = scelte.Select(s => (string)s.Valore).ToList();
            var esclusi = RequisitoApplicabilita.VeicoliEcuEsclusi(requisito, selezionati);
            if (esclusi.Count > 0)
            {
                vm.MessaggioErrore = LocalizationService.Instance.Scegli(
                    "Rimuovi prima i componenti associate ai veicoli esclusi: ",
                    "First remove the Components associated with the excluded vehicles: ",
                    "Supprimez d’abord les componente associés aux véhicules exclus : ") + string.Join(", ", esclusi);
                return;
            }
            RequisitoApplicabilita.ImpostaSottoveicoli(requisito, selezionati);
            vm.MessaggioErrore = null;
            RefreshGrid(requisito);
            vm.NotificaSottoveicoliCambiati(); // menu del guasto e segnali "non allineata" dell'albero
        }
        catch (Exception ex)
        {
            vm.MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("RequisitiSelezioneD4dFallitaFmt"), ex.Message);
            CrashLogService.Registra("Selezione sottoveicoli Extra", ex);
        }
    }

    /// <summary>Resettare ItemsSource (necessario perché Requisito è un POCO con JsonExtensionData e non
    /// notifica il binding) fa perdere scroll e selezione della DataGrid, riportando sempre l'utente in cima
    /// alla lista (stesso problema già noto e risolto per Archivia() in MasterDataEditorViewModelBase, dove si
    /// evita un reset completo della collezione — feedback utente: "torna all'inizio dell'elenco e non è
    /// chiaro il comportamento"). Ripristina esplicitamente la riga appena modificata, cosi l'operatore vede
    /// subito che l'edit ha avuto effetto invece di doverla ricercare da capo.</summary>
    private void RefreshGrid(Requisito? daRiselezionare = null)
    {
        if (DataContext is not RequisitiViewModel vm) return;
        GridPrincipale.ItemsSource = null;
        GridPrincipale.ItemsSource = vm.ItemsAttivi;
        if (daRiselezionare == null) return;
        GridPrincipale.SelectedItem = daRiselezionare;
        GridPrincipale.ScrollIntoView(daRiselezionare, null);
    }

    /// <summary>Import dei guasti del requisito selezionato da CSV (colonne guasto;componente, intestazione opzionale) o Excel
    /// (foglio e riga di intestazione scelti nel dialog standard, colonne A=guasto, B=componente o per intestazione).</summary>
    private void OnAnnullaImportDtc(object? sender, RoutedEventArgs e) => (DataContext as RequisitiViewModel)?.AnnullaImportDtc();

    private async void OnImportaDtc(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequisitiViewModel { InModifica: true } vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        try
        {
            if (!await ImportExcelGuidaService.Chiedi(ImportExcelCatalogo.DtcRequisiti)) return;
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("DtcImportaTitolo"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("CSV / Excel") { Patterns = new[] { "*.csv", "*.txt", "*.xlsx", "*.xls" } }
                }
            });
            var percorso = file.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(percorso)) return;

            List<List<string>> righe;
            var avvisiLettura = new List<string>();
            if (percorso.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || percorso.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            {
                var richiesta = await ImportExcelOpzioniWindow.ChiediOpzioni(topLevel as Window, percorso);
                if (richiesta == null) return;
                using var fs = System.IO.File.OpenRead(richiesta.PercorsoFile);
                righe = ExcelImportService.LeggiRigheOrdinate(fs, richiesta.Foglio, richiesta.RigaIntestazione, avvisiLettura);
            }
            else
            {
                await vm.ImportaDtcDaCsvAsync(percorso, massivo: vm.ESchedaD4d);
                return;
            }
            await vm.ImportaDtcDaRighe(righe, massivo: vm.ESchedaD4d);
            // Le colonne senza intestazione non sono state importate: l'avviso resta accanto all'esito dell'import.
            if (avvisiLettura.Count > 0)
                vm.Messaggio = string.Join(" ", new[] { vm.Messaggio }.Concat(avvisiLettura).Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch (Exception ex)
        {
            vm.MessaggioErrore = string.Format(LocalizationService.Instance.Traduci("ImportExcelErroreFmt"), ex.Message);
            CrashLogService.Registra("Importazione guasto requisito", ex);
        }
    }

    /// <summary>Esporta in Excel i requisiti salvati della scheda corrente nel formato letto dall'import (colonne A-E).</summary>
    private async void OnEsportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequisitiViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        try
        {
            var loc = LocalizationService.Instance;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = loc.Scegli("Esporta requisiti in Excel", "Export requirements to Excel", "Exporter les exigences vers Excel"),
                SuggestedFileName = $"TestLoom_Requisiti_{(vm.ESchedaD4d ? "Extra_" : "")}{System.DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "xlsx",
                FileTypeChoices = new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } }
            });
            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(percorso)) await vm.EsportaExcelVerso(percorso);
        }
        catch (Exception ex)
        {
            vm.MessaggioErrore = LocalizationService.Instance.Scegli($"Esportazione Excel non riuscita: {ex.Message}",
                $"Excel export failed: {ex.Message}", $"Échec de l'export Excel : {ex.Message}");
            CrashLogService.Registra("Esportazione Excel Requisiti", ex);
        }
    }

    /// <summary>Stesso schema di TestCaseEditorView.OnSceglieFileAllegato: picker nativo, "async void" perché
    /// non c'è un try/catch a monte per un comando (l'utente ha solo cliccato un pulsante, non serve un
    /// gestore dedicato all'errore oltre a non far crashare l'app se il picker non è disponibile).</summary>
    private async void OnImportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RequisitiViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            if (!await ImportExcelGuidaService.Chiedi(ImportExcelCatalogo.Requisiti)) return;
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
            CrashLogService.Registra("Importazione Excel Requisiti", ex);
        }
    }
}
