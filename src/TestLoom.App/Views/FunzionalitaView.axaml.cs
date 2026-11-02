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

public partial class FunzionalitaView : UserControl
{
    // Sottoscrizione simmetrica a vm.RigaCreata (fix review 2026-09-25, riuso View/ViewLocator): vedi
    // EcuView.axaml.cs per il motivo (la lambda precedente non si disiscriveva mai dal ViewModel precedente).
    private FunzionalitaViewModel? _vmCorrente;

    // Vero se CategorieContenuto.DataContext e' gia' stato agganciato al CategoriaViewModel annidato di
    // _vmCorrente (perf: profilo rivisita 2026-09-25): costruire quel nested costa da solo circa 52ms del ctor
    // di FunzionalitaViewModel, quasi tutto sprecato quando l'Expander resta collassato per l'intera visita
    // (caso comune). Si aggancia solo alla PRIMA apertura reale, mai a ogni cambio di DataContext.
    private bool _categorieCollegate;

    public FunzionalitaView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        CategorieExpander.PropertyChanged += OnCategorieExpanderPropertyChanged;
    }

    private void OnGridBeginningEdit(object? sender, DataGridBeginningEditEventArgs e) =>
        MasterDataRenameEditGuard.OnBeginningEdit(sender, e);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vmCorrente != null) _vmCorrente.RigaCreata -= OnRigaCreata;
        _vmCorrente = DataContext as FunzionalitaViewModel;
        if (_vmCorrente != null) _vmCorrente.RigaCreata += OnRigaCreata;

        // Ogni navigazione ricrea un FunzionalitaViewModel nuovo (ImpostazioniViewModel.
        // SostituisciPaginaCorrente): l'agganciamento precedente non vale piu' per il nuovo VM, altrimenti
        // CategorieContenuto resterebbe legato al CategoriaViewModel della pagina abbandonata invece di
        // ripartire pigro per questa nuova visita.
        _categorieCollegate = false;
        CategorieContenuto.DataContext = null;
        if (CategorieExpander.IsExpanded) CollegaCategorie();
    }

    private void OnCategorieExpanderPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Expander.IsExpandedProperty && CategorieExpander.IsExpanded) CollegaCategorie();
    }

    /// <summary>Costruisce (se non gia' fatto) il CategoriaViewModel annidato e lo aggancia come DataContext del
    /// solo contenuto dell'Expander — l'accesso a vm.Categorie e' quello che, alla prima chiamata, materializza
    /// davvero l'editor (FunzionalitaViewModel.Categorie e' pigro, vedi MasterDataEditors.cs).</summary>
    private void CollegaCategorie()
    {
        if (_categorieCollegate || _vmCorrente == null) return;
        _categorieCollegate = true;
        CategorieContenuto.DataContext = _vmCorrente.Categorie;
    }

    private void OnRigaCreata(Funzionalita item)
    {
        GridPrincipale.ScrollIntoView(item, null);
        GridPrincipale.SelectedItem = item;
    }

    /// <summary>Stesso schema di TestCaseEditorView.OnSceglieFileAllegato: picker nativo, "async void" perché
    /// non c'è un try/catch a monte per un comando (l'utente ha solo cliccato un pulsante, non serve un
    /// gestore dedicato all'errore oltre a non far crashare l'app se il picker non è disponibile). Import
    /// limitato ai campi semplici (Nome/Archiviata): le Verifiche riutilizzabili restano da aggiungere a mano.</summary>
    private async void OnImportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FunzionalitaViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            if (!await ImportExcelGuidaService.Chiedi(ImportExcelCatalogo.Funzionalita)) return;
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
            CrashLogService.Registra("Importazione Excel Funzionalità", ex);
        }
    }

    /// <summary>Una funzionalità può appartenere a più categorie (feedback utente 2026-08-24): stesso schema
    /// di EcuView.OnScegliVeicoli, senza il "copia da un'altra riga" (non richiesto qui) — una DataGrid
    /// TemplateColumn non ha un modo dichiarativo comodo per un multi-select per-riga.</summary>
    private void OnScegliCategorie(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Funzionalita funzionalita) return;
        if (DataContext is not FunzionalitaViewModel vm) return;

        var disponibili = vm.CategorieDisponibili;
        var panel = new StackPanel { Spacing = 4, MinWidth = 200 };
        var checkBoxes = new Dictionary<string, CheckBox>();

        if (disponibili.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Nessuna categoria censita.", Classes = { "Muted" } });

        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter.
        TextBox? ricerca = null;
        if (disponibili.Count > 10)
        {
            ricerca = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            panel.Children.Add(ricerca);
        }
        var azioni = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        var selezionaVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerSelezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(selezionaVisibili, "Funz_Categorie_SelectVisible_Button");
        selezionaVisibili.Click += (_, _) => { foreach (var checkBox in checkBoxes.Values.Where(c => c.IsVisible)) checkBox.IsChecked = true; };
        var deselezionaVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerDeselezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(deselezionaVisibili, "Funz_Categorie_DeselectVisible_Button");
        deselezionaVisibili.Click += (_, _) => { foreach (var checkBox in checkBoxes.Values.Where(c => c.IsVisible)) checkBox.IsChecked = false; };
        azioni.Children.Add(selezionaVisibili);
        azioni.Children.Add(deselezionaVisibili);
        panel.Children.Add(azioni);
        var lista = new StackPanel { Spacing = 2 };
        foreach (var categoria in disponibili)
        {
            var checkBox = new CheckBox { Content = categoria, IsChecked = funzionalita.Categorie.Contains(categoria) };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                var selezionato = checkBox.IsChecked == true;
                if (selezionato && !funzionalita.Categorie.Contains(categoria)) funzionalita.Categorie.Add(categoria);
                else if (!selezionato) funzionalita.Categorie.Remove(categoria);
                button.Content = funzionalita.CategorieTesto;
            };
            checkBoxes[categoria] = checkBox;
            lista.Children.Add(checkBox);
        }
        if (ricerca != null)
            ricerca.TextChanged += (_, _) =>
            {
                var q = ricerca.Text ?? "";
                foreach (var (categoria, checkBox) in checkBoxes)
                    checkBox.IsVisible = string.IsNullOrWhiteSpace(q) || categoria.Contains(q, System.StringComparison.OrdinalIgnoreCase);
            };
        panel.Children.Add(new ScrollViewer { Content = lista, MaxHeight = 260 });

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>Visualizza i componenti attuali come caselle normali anche per le funzionalità storiche "Tutti i componenti".
    /// Finché l'utente non cambia la selezione, il flag dinamico resta intatto e include anche componente aggiunte in futuro.</summary>
    private void OnScegliEcuAssociate(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Funzionalita funzionalita) return;
        if (DataContext is not FunzionalitaViewModel vm) return;

        var disponibili = vm.EcuDisponibili;
        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        var checkBoxes = new Dictionary<string, CheckBox>();

        if (disponibili.Count == 0)
            panel.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("NessunaEcuCensita"), Classes = { "Muted" } });

        // Ricerca solo oltre soglia (feedback utente 2026-08-21), stessa soglia di ListaOltreSogliaConverter.
        TextBox? ricerca = null;
        if (disponibili.Count > 10)
        {
            ricerca = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
            panel.Children.Add(ricerca);
        }
        var azioniEcu = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
        var selezionaEcuVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerSelezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(selezionaEcuVisibili, "Funz_Componente_SelectVisible_Button");
        var deselezionaEcuVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerDeselezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
        Avalonia.Automation.AutomationProperties.SetAutomationId(deselezionaEcuVisibili, "Funz_Componente_DeselectVisible_Button");
        var lista = new StackPanel { Spacing = 2 };
        var righeEcu = new Dictionary<string, Control>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var componente in disponibili)
        {
            var checkBox = new CheckBox { Content = componente, IsChecked = funzionalita.TutteLeEcu || funzionalita.EcuAssociate.Contains(componente, System.StringComparer.OrdinalIgnoreCase) };
            // Riga componente: casella, riepilogo dei veicoli su cui la funzionalità vale (solo per componente spuntata con veicoli montati) e
            // "Veicoli…" che espande la checklist dei veicoli montati su quell'componente, nello stesso flyout.
            var riepilogoVeicoli = new TextBlock { Classes = { "Muted" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false };
            var pulsanteVeicoli = new Button
            {
                Content = LocalizationService.Instance.Traduci("FunzEcuVeicoliBtn"),
                Classes = { "Link" },
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                IsVisible = false,
            };
            ToolTip.SetTip(pulsanteVeicoli, LocalizationService.Instance.Traduci("FunzEcuVeicoliTip"));
            var dettaglioVeicoli = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(22, 0, 0, 2), IsVisible = false };
            var intestazione = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
            intestazione.Children.Add(checkBox);
            intestazione.Children.Add(riepilogoVeicoli);
            intestazione.Children.Add(pulsanteVeicoli);
            var riga = new StackPanel { Spacing = 2 };
            riga.Children.Add(intestazione);
            riga.Children.Add(dettaglioVeicoli);

            void AggiornaRigaVeicoli()
            {
                var testo = checkBox.IsChecked == true ? vm.TestoVeicoliEcu(funzionalita, componente) : "";
                riepilogoVeicoli.Text = testo;
                riepilogoVeicoli.IsVisible = testo.Length > 0;
                pulsanteVeicoli.IsVisible = testo.Length > 0;
                if (testo.Length == 0) dettaglioVeicoli.IsVisible = false;
            }

            void CostruisciDettaglioVeicoli()
            {
                var ecuAttiva = vm.EcuAttivaPerNome(componente);
                if (ecuAttiva is null) return;
                var ammessi = AssociazioniVeicoloService.VeicoliAmmessi(funzionalita, ecuAttiva);
                var caselleVeicolo = new Dictionary<string, CheckBox>(System.StringComparer.OrdinalIgnoreCase);
                var listaVeicoli = new StackPanel { Spacing = 2 };
                TextBox? ricercaVeicoli = null;
                var veicoliMontati = AssociazioniVeicoloService.VeicoliMontati(ecuAttiva);
                if (veicoliMontati.Count > 10)
                {
                    ricercaVeicoli = new TextBox { PlaceholderText = LocalizationService.Instance.Traduci("CercaPlaceholder"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
                    dettaglioVeicoli.Children.Add(ricercaVeicoli);
                }
                var azioniVeicoli = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
                var selezionaVeicoliVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerSelezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
                Avalonia.Automation.AutomationProperties.SetAutomationId(selezionaVeicoliVisibili, "Funz_EcuVeicoli_SelectVisible_Button");
                selezionaVeicoliVisibili.Click += (_, _) => { foreach (var c in caselleVeicolo.Values.Where(c => c.IsVisible)) c.IsChecked = true; };
                var deselezionaVeicoliVisibili = new Button { Content = LocalizationService.Instance.Traduci("PickerDeselezionaVisibili"), Classes = { "Link" }, Padding = new Avalonia.Thickness(0) };
                Avalonia.Automation.AutomationProperties.SetAutomationId(deselezionaVeicoliVisibili, "Funz_EcuVeicoli_DeselectVisible_Button");
                deselezionaVeicoliVisibili.Click += (_, _) => { foreach (var c in caselleVeicolo.Values.Where(c => c.IsVisible)) c.IsChecked = false; };
                azioniVeicoli.Children.Add(selezionaVeicoliVisibili);
                azioniVeicoli.Children.Add(deselezionaVeicoliVisibili);
                dettaglioVeicoli.Children.Add(azioniVeicoli);
                foreach (var veicolo in veicoliMontati)
                {
                    var casella = new CheckBox { Content = veicolo, Tag = veicolo, IsChecked = ammessi.Contains(veicolo, System.StringComparer.OrdinalIgnoreCase) };
                    casella.IsCheckedChanged += (_, _) =>
                    {
                        var scelti = caselleVeicolo.Values.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
                        vm.ImpostaVeicoliEcu(funzionalita, componente, scelti);
                        AggiornaRigaVeicoli();
                    };
                    caselleVeicolo[veicolo] = casella;
                    listaVeicoli.Children.Add(casella);
                }
                if (ricercaVeicoli != null)
                    ricercaVeicoli.TextChanged += (_, _) =>
                    {
                        var q = ricercaVeicoli.Text ?? "";
                        foreach (var (veicolo, casella) in caselleVeicolo)
                            casella.IsVisible = string.IsNullOrWhiteSpace(q) || veicolo.Contains(q, System.StringComparison.OrdinalIgnoreCase);
                    };
                dettaglioVeicoli.Children.Add(new ScrollViewer { Content = listaVeicoli, MaxHeight = 180 });
            }

            pulsanteVeicoli.Click += (_, _) =>
            {
                if (dettaglioVeicoli.Children.Count == 0) CostruisciDettaglioVeicoli();
                dettaglioVeicoli.IsVisible = !dettaglioVeicoli.IsVisible;
            };
            checkBox.IsCheckedChanged += (_, _) =>
            {
                var selezionate = checkBoxes.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList();
                var ecuNonVisibili = funzionalita.EcuAssociate.Where(nome => !checkBoxes.ContainsKey(nome)).ToList();
                var tutteSelezionate = checkBoxes.Count > 0 && selezionate.Count == checkBoxes.Count && ecuNonVisibili.Count == 0;
                funzionalita.TutteLeEcu = tutteSelezionate;
                funzionalita.EcuAssociate = tutteSelezionate ? new() : selezionate.Concat(ecuNonVisibili).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
                button.Content = funzionalita.EcuAssociateRiepilogo;
                button.SetValue(ToolTip.TipProperty, funzionalita.EcuAssociateRiepilogo);
                AggiornaRigaVeicoli();
            };
            checkBoxes[componente] = checkBox;
            righeEcu[componente] = riga;
            lista.Children.Add(riga);
            AggiornaRigaVeicoli();
        }

        selezionaEcuVisibili.Click += (_, _) => { foreach (var checkBox in checkBoxes.Where(kv => righeEcu[kv.Key].IsVisible).Select(kv => kv.Value).ToList()) checkBox.IsChecked = true; };
        deselezionaEcuVisibili.Click += (_, _) => { foreach (var checkBox in checkBoxes.Where(kv => righeEcu[kv.Key].IsVisible).Select(kv => kv.Value).ToList()) checkBox.IsChecked = false; };
        azioniEcu.Children.Add(selezionaEcuVisibili);
        azioniEcu.Children.Add(deselezionaEcuVisibili);
        panel.Children.Add(azioniEcu);

        if (ricerca != null)
            ricerca.TextChanged += (_, _) =>
            {
                var q = ricerca.Text ?? "";
                foreach (var (componente, riga) in righeEcu)
                    riga.IsVisible = string.IsNullOrWhiteSpace(q) || componente.Contains(q, System.StringComparison.OrdinalIgnoreCase);
            };
        panel.Children.Add(new ScrollViewer { Content = lista, MaxHeight = 260 });

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }
}
