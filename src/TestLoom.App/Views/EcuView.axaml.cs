using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

public partial class EcuView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): con la View
    // riusata la lambda precedente restava agganciata al ViewModel precedente se il DataContext cambiasse
    // istanza (o si accumulava un handler in più se lo stesso evento si ripetesse) senza mai disiscriversi.
    // Metodo nominato + campo del ViewModel corrente cosi' il disaggancio dal vecchio VM e' esplicito.
    private EcuViewModel? _vmCorrente;

    public EcuView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        MasterDataRenameEditGuard.OnBeginningEdit(sender, e);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as EcuViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;
    }

    private void OnRigaCreata(Componente item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>Stesso schema di TestCaseEditorView.OnSceglieFileAllegato: picker nativo, "async void" perché
    /// non c'è un try/catch a monte per un comando (l'utente ha solo cliccato un pulsante, non serve un
    /// gestore dedicato all'errore oltre a non far crashare l'app se il picker non è disponibile).</summary>
    private async void OnImportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EcuViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            if (!await ImportExcelGuidaService.Chiedi(ImportExcelCatalogo.Componente)) return;
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
            CrashLogService.Registra("Importazione Excel componente", ex);
        }
    }

    /// <summary>Un componente può essere montata su più veicoli (Addendum redesign 2026-08, revisione feedback QA):
    /// stesso schema di OperatoriView.OnScegliGamme, costruito in code-behind per lo stesso motivo — una
    /// DataGrid TemplateColumn non ha un modo dichiarativo comodo per un multi-select per-riga. In testa,
    /// "Copia da un'altra componente" (feedback utente 2026-08-20, punto 10) sostituisce l'intera selezione con quella
    /// di un'altra componente già censita, invece di doverle spuntare una a una a mano.</summary>
    private void OnScegliVeicoli(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Componente componente) return;
        if (DataContext is not EcuViewModel vm) return;

        var panel = new StackPanel { Spacing = 4, MinWidth = 200 };
        var altreEcu = vm.Items.Where(altra => altra != componente && altra.VeicoliAssociati.Count > 0).ToList();
        var checkBoxes = new Dictionary<string, CheckBox>(System.StringComparer.OrdinalIgnoreCase);
        Flyout? flyoutCorrente = null;
        var aggiornandoCheckbox = false;

        void AggiornaCheckbox()
        {
            aggiornandoCheckbox = true;
            try
            {
                foreach (var (veicolo, checkBox) in checkBoxes) checkBox.IsChecked = componente.VeicoliAssociati.Contains(veicolo, System.StringComparer.OrdinalIgnoreCase);
                button.Content = componente.VeicoliAssociatiTesto;
            }
            finally { aggiornandoCheckbox = false; }
        }

        if (altreEcu.Count > 0)
        {
            var segnaposto = LocalizationService.Instance.Traduci("EcuCopiaDaAltraPlaceholder");
            Componente? sorgenteScelta = null;
            var copiaBtn = new Button { Content = LocalizationService.Instance.Traduci("EcuCopiaAssociazioniBtn"), Classes = { "Secondary" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, IsEnabled = false };
            Control selettoreSorgente;
            if (altreEcu.Count > ListaOltreSogliaConverter.Soglia)
            {
                // Regola app: oltre ListaOltreSogliaConverter.Soglia voci serve la ricerca, la ComboBox pura non
                // ne ha. Pulsante che apre il picker con ricerca (SelezionaDialogService).
                var scegliBtn = new Button
                {
                    Content = segnaposto, Classes = { "Secondary" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left
                };
                Avalonia.Automation.AutomationProperties.SetAutomationId(scegliBtn, "ComponenteBtnScegliCopiaDa");
                scegliBtn.Click += async (_, _) =>
                {
                    var voci = altreEcu.Select(altra => new VoceSelezionabile { Valore = altra, TestoPrincipale = altra.Nome, TestoRicerca = altra.Nome }).ToList();
                    var scelta = await SelezionaDialogService.Scegli(segnaposto, voci);
                    if (scelta?.Valore is not Componente sorgente) return;
                    // Il modale fa chiudere il Flyout (light-dismiss): la scelta va applicata subito al componente
                    // (niente secondo clic su "Copia associazioni" su un pannello ormai staccato) e, se il flyout
                    // si e' chiuso, lo si riapre per mostrare il risultato.
                    sorgenteScelta = sorgente;
                    scegliBtn.Content = sorgente.Nome;
                    componente.VeicoliAssociati = new List<string>(sorgente.VeicoliAssociati);
                    AggiornaCheckbox();
                    if (flyoutCorrente != null && !flyoutCorrente.IsOpen) flyoutCorrente.ShowAt(button);
                };
                selettoreSorgente = scegliBtn;
            }
            else
            {
                var comboSorgente = new ComboBox
                {
                    ItemsSource = altreEcu, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Componente.Nome)),
                    PlaceholderText = segnaposto, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                comboSorgente.SelectionChanged += (_, _) =>
                {
                    sorgenteScelta = comboSorgente.SelectedItem as Componente;
                    copiaBtn.IsEnabled = sorgenteScelta != null;
                };
                selettoreSorgente = comboSorgente;
            }
            copiaBtn.Click += (_, _) =>
            {
                if (sorgenteScelta == null) return;
                componente.VeicoliAssociati = new List<string>(sorgenteScelta.VeicoliAssociati);
                AggiornaCheckbox();
            };
            panel.Children.Add(selettoreSorgente);
            // Oltre soglia la scelta dal picker applica subito la copia: un "Copia associazioni" sempre
            // disabilitato sarebbe solo rumore, quindi compare soltanto con la ComboBox.
            if (altreEcu.Count <= ListaOltreSogliaConverter.Soglia) panel.Children.Add(copiaBtn);
            panel.Children.Add(new Separator { Margin = new Avalonia.Thickness(0, 4) });
        }

        if (vm.VeicoliDisponibili.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("EcuNessunVeicoloCensito"), Classes = { "Muted" } });
        }

        // Ricerca solo oltre soglia (feedback utente 2026-08-21): sotto 10 veicoli l'elenco a scorrimento
        // basta, sopra 10 filtrare a mano diventa scomodo — stessa soglia di ListaOltreSogliaConverter.
        TextBox? ricerca = null;
        if (vm.VeicoliDisponibili.Count > 10)
        {
            ricerca = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            panel.Children.Add(ricerca);
        }
        var lista = new StackPanel { Spacing = 2 };
        foreach (var veicolo in vm.VeicoliDisponibili)
        {
            var checkBox = new CheckBox { Content = veicolo, IsChecked = componente.VeicoliAssociati.Contains(veicolo, System.StringComparer.OrdinalIgnoreCase) };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (aggiornandoCheckbox) return;
                var veicoliNonDisponibili = componente.VeicoliAssociati.Where(nome => !checkBoxes.ContainsKey(nome));
                componente.VeicoliAssociati = veicoliNonDisponibili.Concat(checkBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key))
                    .Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
                button.Content = componente.VeicoliAssociatiTesto;
            };
            checkBoxes[veicolo] = checkBox;
            lista.Children.Add(checkBox);
        }
        if (ricerca != null)
            ricerca.TextChanged += (_, _) =>
            {
                var q = ricerca.Text ?? "";
                foreach (var (veicolo, checkBox) in checkBoxes)
                    checkBox.IsVisible = string.IsNullOrWhiteSpace(q) || veicolo.Contains(q, System.StringComparison.OrdinalIgnoreCase);
            };
        var azioni = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        var selezionaVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerSelezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(selezionaVisibili, "Componente_Veicoli_SelectVisible_Button");
        selezionaVisibili.Click += (_, _) => { foreach (var c in checkBoxes.Values.Where(c => c.IsVisible).ToList()) c.IsChecked = true; };
        var deselezionaVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerDeselezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(deselezionaVisibili, "Componente_Veicoli_DeselectVisible_Button");
        deselezionaVisibili.Click += (_, _) => { foreach (var c in checkBoxes.Values.Where(c => c.IsVisible).ToList()) c.IsChecked = false; };
        azioni.Children.Add(selezionaVisibili);
        azioni.Children.Add(deselezionaVisibili);
        panel.Children.Add(azioni);
        panel.Children.Add(new ScrollViewer { Content = lista, MaxHeight = 260 });

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyoutCorrente = flyout;
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }
}
