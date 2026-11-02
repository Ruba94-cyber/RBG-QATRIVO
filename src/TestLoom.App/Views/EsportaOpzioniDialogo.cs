using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TestLoom.App.Services;

namespace TestLoom.App.Views;

/// <summary>
/// Dialogo unico dell'export per tutte le pagine: l'operatore sceglie le sezioni (viste, dati, riepiloghi, grafici, pivot, info) e, dove
/// la sezione e' una tabella, le colonne. La scelta si ricorda (ultima selezione) e si possono salvare fino a sei configurazioni con
/// nome per pagina. I filtri sono quelli a video, mostrati in sola lettura: valgono sempre per l'export.
/// </summary>
public sealed class EsportaOpzioniDialogo
{
    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);

    public Window Finestra { get; }
    /// <summary>Esito: la selezione confermata, oppure null se annullato o chiuso.</summary>
    public Task<ExportConfigurazione?> Risultato => _risultato.Task;

    private readonly TaskCompletionSource<ExportConfigurazione?> _risultato = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppState _state;
    private readonly ExportDescrittore _descrittore;
    private readonly Dictionary<string, CheckBox> _sezioni = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, CheckBox>> _colonne = new(StringComparer.OrdinalIgnoreCase);
    private readonly ComboBox _elenco = new() { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _nome = new() { MaxLength = ExportConfigurazioniService.LunghezzaMassimaNome };
    private readonly TextBlock _messaggio = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ComboBox _pivotFonte = new(), _pivotRighe = new(), _pivotColonne = new(), _pivotPercentuale = new();
    private readonly ComboBox _pivotMetrica = new(), _pivotCampoValore = new();
    private readonly CheckBox _pivotNumeri = new() { IsChecked = true }, _pivotPercentualiStandard = new() { IsChecked = true };
    private sealed record Scelta(string Id, string Etichetta) { public override string ToString() => Etichetta; }
    private bool _aggiornandoElenco;

    public static async Task<ExportConfigurazione?> Apri(Window owner, AppState state, ExportDescrittore descrittore)
    {
        var dialogo = new EsportaOpzioniDialogo(state, descrittore);
        await dialogo.Finestra.ShowDialog(owner);
        return await dialogo.Risultato;
    }

    public EsportaOpzioniDialogo(AppState state, ExportDescrittore descrittore)
    {
        _state = state;
        _descrittore = descrittore;
        Finestra = new Window
        {
            Title = descrittore.Titolo, Width = 640, Height = 720, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        AutomationProperties.SetAutomationId(Finestra, "EsportaOpzioni_Dialogo");

        var iniziale = ExportConfigurazioniService.CaricaUltimaSelezione(state, descrittore.Pagina) ?? ExportSelezione.Predefinita(descrittore);
        var sezioni = CostruisciSezioni(pivot: false);
        var pivot = CostruisciSezioni(pivot: true);
        Applica(ExportSelezione.Normalizza(iniziale, descrittore));

        AutomationProperties.SetAutomationId(_nome, "EsportaOpzioni_Nome");
        _nome.Text = iniziale.Nome;
        AggiornaElenco(iniziale.Nome);
        _elenco.SelectionChanged += (_, _) =>
        {
            if (_aggiornandoElenco || _elenco.SelectedItem is not string nome) return;
            var scelta = ExportConfigurazioniService.Carica(_state, _descrittore.Pagina).Configurazioni
                .FirstOrDefault(c => string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase));
            if (scelta == null) return;
            Applica(ExportSelezione.Normalizza(scelta, _descrittore));
            _nome.Text = scelta.Nome;
            Messaggio(null);
        };

        var salva = new Button { Content = T("ExpDlgSalva") };
        AutomationProperties.SetAutomationId(salva, "EsportaOpzioni_Salva");
        salva.Click += (_, _) => SalvaConfigurazione();
        var elimina = new Button { Content = T("ExpDlgElimina") };
        AutomationProperties.SetAutomationId(elimina, "EsportaOpzioni_Elimina");
        elimina.Click += (_, _) => EliminaConfigurazione();

        var riga = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto,Auto"), ColumnSpacing = 8 };
        riga.Children.Add(_elenco);
        Grid.SetColumn(_nome, 1);
        riga.Children.Add(_nome);
        Grid.SetColumn(salva, 2);
        riga.Children.Add(salva);
        Grid.SetColumn(elimina, 3);
        riga.Children.Add(elimina);

        var filtri = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(descrittore.RiepilogoFiltri) ? T("ExpDlgNessunFiltro") : descrittore.RiepilogoFiltri,
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
        };
        AutomationProperties.SetAutomationId(filtri, "EsportaOpzioni_Filtri");

        var esporta = new Button { Content = T("ExpDlgEsporta"), MinWidth = 100 };
        AutomationProperties.SetAutomationId(esporta, "EsportaOpzioni_Esporta");
        esporta.Click += (_, _) => Conferma();
        var annulla = new Button { Content = T("ExpDlgAnnulla"), MinWidth = 100 };
        AutomationProperties.SetAutomationId(annulla, "EsportaOpzioni_Annulla");
        annulla.Click += (_, _) => Finestra.Close();
        var pulsanti = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { esporta, annulla } };

        var layout = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var alto = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = T("ExpDlgConfigurazioni"), FontWeight = FontWeight.SemiBold }, riga, _messaggio,
            }
        };
        DockPanel.SetDock(alto, Dock.Top);
        DockPanel.SetDock(pulsanti, Dock.Bottom);
        pulsanti.Margin = new Thickness(0, 12, 0, 0);
        layout.Children.Add(alto);
        layout.Children.Add(pulsanti);
        var loc = LocalizationService.Instance;
        var filtriContenuto = new StackPanel { Spacing = 8 };
        filtriContenuto.Children.Add(new TextBlock
        {
            Text = loc.Scegli("I filtri partono dalla vista corrente. Le modifiche vengono applicate alla conferma.",
                "Filters start from the current view. Changes are applied on confirmation.",
                "Les filtres reprennent la vue actuelle. Les modifications sont appliquées à la confirmation."),
            TextWrapping = TextWrapping.Wrap
        });
        if (descrittore.Filtri is { Count: > 0 }) filtriContenuto.Children.Add(CostruisciFiltri(descrittore.Filtri));
        else filtriContenuto.Children.Add(filtri);
        var schede = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetAutomationId(schede, "EsportaOpzioni_Schede");
        schede.ItemsSource = new[]
        {
            new TabItem { Header = loc.Scegli("Filtri della vista", "View filters", "Filtres de la vue"), Content = new ScrollViewer { Content = filtriContenuto } },
            new TabItem { Header = loc.Scegli("Dati e colonne", "Data and columns", "Données et colonnes"), Content = new ScrollViewer { Content = sezioni } },
            new TabItem { Header = "Pivot", Content = new ScrollViewer { Content = pivot } },
        };
        layout.Children.Add(schede);
        Finestra.Content = layout;
        Finestra.Closed += (_, _) => _risultato.TrySetResult(null);
    }

    // Filtri modificabili: ogni voce sa leggere lo stato iniziale (quello a video), riportarvisi e scrivere la scelta sul filtro vero.
    private readonly List<(Action Ripristina, Action Applica)> _filtri = new();
    private const int SogliaRicercaVoci = 10;

    private Control CostruisciFiltri(IReadOnlyList<ExportFiltro> filtri)
    {
        var pannello = new StackPanel { Spacing = 6 };
        foreach (var f in filtri)
        {
            switch (f)
            {
                case ExportFiltroMultiplo m: pannello.Children.Add(FiltroMultiplo(m)); break;
                case ExportFiltroSingolo s: pannello.Children.Add(FiltroSingolo(s)); break;
                case ExportFiltroData d: pannello.Children.Add(FiltroData(d)); break;
                case ExportFiltroTesto t: pannello.Children.Add(FiltroTesto(t)); break;
                case ExportFiltroBooleano b: pannello.Children.Add(FiltroBooleano(b)); break;
            }
        }
        var ripristina = new Button { Content = T("ExpDlgRipristinaFiltri"), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(ripristina, "EsportaOpzioni_RipristinaFiltri");
        ripristina.Click += (_, _) => { foreach (var (r, _) in _filtri) r(); };
        pannello.Children.Add(ripristina);
        return pannello;
    }

    private Control FiltroMultiplo(ExportFiltroMultiplo m)
    {
        // Copie delle voci: la scelta resta nel dialogo finche' non si conferma.
        var voci = m.Filtro.Opzioni.Select(o => new ViewModels.FiltroOpzione { Valore = o.Valore, Etichetta = o.Etichetta, Selezionato = o.Selezionato }).ToList();
        var riepilogo = new TextBlock { Opacity = 0.7 };
        void AggiornaRiepilogo()
        {
            var n = voci.Count(v => v.Selezionato);
            riepilogo.Text = n == 0 ? T("FiltroTutteLeVoci") : n == 1 ? voci.First(v => v.Selezionato).Etichetta : string.Format(T("FiltroNVociSelezionateFmt"), n);
        }
        foreach (var v in voci) v.PropertyChanged += (_, _) => AggiornaRiepilogo();
        AggiornaRiepilogo();

        var lista = new ListBox { MaxHeight = 200, ItemsSource = voci, SelectionMode = SelectionMode.Single };
        lista.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ViewModels.FiltroOpzione>((voce, _) =>
        {
            var casella = new CheckBox { Content = voce?.Etichetta };
            if (voce != null)
                casella.Bind(CheckBox.IsCheckedProperty, new Avalonia.Data.Binding(nameof(ViewModels.FiltroOpzione.Selezionato)) { Mode = Avalonia.Data.BindingMode.TwoWay });
            return casella;
        });
        AutomationProperties.SetAutomationId(lista, "EsportaOpzioni_Filtro_" + m.Id);
        var contenuto = new StackPanel { Spacing = 4 };
        if (voci.Count > SogliaRicercaVoci)
        {
            var ricerca = new TextBox { PlaceholderText = T("ExpDlgCerca") };
            ricerca.TextChanged += (_, _) =>
            {
                var testo = (ricerca.Text ?? "").Trim();
                lista.ItemsSource = testo.Length == 0 ? voci : voci.Where(v => v.Etichetta.Contains(testo, StringComparison.CurrentCultureIgnoreCase)).ToList();
            };
            contenuto.Children.Add(ricerca);
        }
        var tutte = new Button { Content = T("ExpDlgNessunaColonna") };
        tutte.Click += (_, _) => { foreach (var v in voci) v.Selezionato = false; };
        contenuto.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tutte, riepilogo } });
        contenuto.Children.Add(lista);

        var iniziali = m.Filtro.Opzioni.Where(o => o.Selezionato).Select(o => o.Valore).ToHashSet(StringComparer.Ordinal);
        _filtri.Add((
            Ripristina: () => { foreach (var v in voci) v.Selezionato = iniziali.Contains(v.Valore); },
            Applica: () =>
            {
                // Solo le voci cambiate: ogni modifica del filtro vero fa ricalcolare la pagina.
                foreach (var (originale, copia) in m.Filtro.Opzioni.Zip(voci))
                    if (originale.Selezionato != copia.Selezionato) originale.Selezionato = copia.Selezionato;
            }));
        return new Expander { Header = m.Etichetta, Content = contenuto };
    }

    private Control FiltroSingolo(ExportFiltroSingolo s)
    {
        var tutti = new Scelta("", T("ExpDlgTutti"));
        var voci = new[] { tutti }.Concat(s.Valori.Select(v => new Scelta(v, s.EtichettaValore?.Invoke(v) ?? v))).ToList();
        var combo = new ComboBox { ItemsSource = voci, HorizontalAlignment = HorizontalAlignment.Stretch };
        var iniziale = s.Leggi();
        Scelta Iniziale() => string.IsNullOrEmpty(iniziale) ? tutti : voci.FirstOrDefault(v => v.Id == iniziale) ?? new Scelta(iniziale, iniziale);
        combo.SelectedItem = Iniziale();
        AutomationProperties.SetAutomationId(combo, "EsportaOpzioni_Filtro_" + s.Id);
        _filtri.Add((
            Ripristina: () => combo.SelectedItem = Iniziale(),
            Applica: () =>
            {
                var scelto = (combo.SelectedItem as Scelta)?.Id;
                var nuovo = string.IsNullOrEmpty(scelto) ? null : scelto;
                if (!string.Equals(nuovo, string.IsNullOrEmpty(iniziale) ? null : iniziale, StringComparison.Ordinal)) s.Imposta(nuovo);
            }));
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = s.Etichetta }, combo } };
    }

    private Control FiltroData(ExportFiltroData d)
    {
        var iniziale = d.Leggi();
        var selettore = new CalendarDatePicker
        {
            SelectedDateFormat = CalendarDatePickerFormat.Custom, CustomDateFormatString = "dd/MM/yyyy",
            SelectedDate = iniziale,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(selettore, "EsportaOpzioni_Filtro_" + d.Id);
        _filtri.Add((
            Ripristina: () => selettore.SelectedDate = iniziale,
            Applica: () =>
            {
                var nuovo = selettore.SelectedDate?.Date;
                if (nuovo != iniziale?.Date) d.Imposta(nuovo);
            }));
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = d.Etichetta }, selettore } };
    }

    private Control FiltroBooleano(ExportFiltroBooleano b)
    {
        var iniziale = b.Leggi();
        var casella = new CheckBox { Content = b.Etichetta, IsChecked = iniziale };
        AutomationProperties.SetAutomationId(casella, "EsportaOpzioni_Filtro_" + b.Id);
        _filtri.Add((
            Ripristina: () => casella.IsChecked = iniziale,
            Applica: () =>
            {
                var nuovo = casella.IsChecked == true;
                if (nuovo != iniziale) b.Imposta(nuovo);
            }));
        return casella;
    }

    private Control FiltroTesto(ExportFiltroTesto t)
    {
        var iniziale = t.Leggi() ?? "";
        var casella = new TextBox { Text = iniziale, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(casella, "EsportaOpzioni_Filtro_" + t.Id);
        _filtri.Add((
            Ripristina: () => casella.Text = iniziale,
            Applica: () =>
            {
                var nuovo = casella.Text ?? "";
                if (!string.Equals(nuovo, iniziale, StringComparison.Ordinal)) t.Imposta(nuovo);
            }));
        return new StackPanel { Spacing = 2, Children = { new TextBlock { Text = t.Etichetta }, casella } };
    }

    /// <summary>Scrive sui filtri veri della pagina le scelte fatte nel dialogo: la pagina si riallinea e l'export usa gli stessi.</summary>
    public void ApplicaFiltri() { foreach (var (_, applica) in _filtri) applica(); }

    private StackPanel CostruisciSezioni(bool pivot)
    {
        var pannello = new StackPanel { Spacing = 6 };
        foreach (var s in _descrittore.Sezioni.Where(s => string.Equals(s.Id, ReportSezioniExport.Pivot, StringComparison.OrdinalIgnoreCase) == pivot))
        {
            var casella = new CheckBox { Content = s.Etichetta };
            AutomationProperties.SetAutomationId(casella, "EsportaOpzioni_Sezione_" + s.Id);
            _sezioni[s.Id] = casella;
            if (s.Colonne == null || s.Colonne.Count == 0)
            {
                pannello.Children.Add(casella);
                continue;
            }
            var colonne = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            var elencoColonne = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var k in s.Colonne)
            {
                var c = new CheckBox { Content = k.Etichetta, Margin = new Thickness(0, 0, 14, 2), IsChecked = true };
                AutomationProperties.SetAutomationId(c, $"EsportaOpzioni_Colonna_{s.Id}_{k.Id}");
                colonne[k.Id] = c;
                elencoColonne.Children.Add(c);
            }
            _colonne[s.Id] = colonne;
            var tutte = new Button { Content = T("ExpDlgTutteColonne"), Margin = new Thickness(0, 0, 8, 6) };
            tutte.Click += (_, _) => { foreach (var c in colonne.Values) c.IsChecked = true; };
            var nessuna = new Button { Content = T("ExpDlgNessunaColonna"), Margin = new Thickness(0, 0, 8, 6) };
            nessuna.Click += (_, _) => { foreach (var c in colonne.Values) c.IsChecked = false; };
            var contenuto = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { tutte, nessuna } }, elencoColonne } };
            pannello.Children.Add(new Expander { Header = casella, Content = contenuto });
        }
        if (pivot && pannello.Children.Count == 0)
            pannello.Children.Add(new TextBlock { Text = LocalizationService.Instance.Scegli(
                "Nessuna tabella pivot disponibile per questa esportazione.",
                "No pivot tables are available for this export.",
                "Aucun tableau croisé dynamique disponible pour cette exportation."), TextWrapping = TextWrapping.Wrap });
        if (pivot && string.Equals(_descrittore.Pagina, "report", StringComparison.OrdinalIgnoreCase))
        {
            var loc = LocalizationService.Instance;
            _pivotNumeri.Content = loc.Scegli("Pivot standard: mostra i numeri", "Standard pivots: show counts", "Tableaux standard : afficher les nombres");
            _pivotPercentualiStandard.Content = loc.Scegli("Pivot standard: mostra le percentuali (sul totale della stessa combinazione, indicato nel foglio)",
                "Standard pivots: show percentages (of the total of the same combination, stated in the sheet)",
                "Tableaux standard : afficher les pourcentages (du total de la même combinaison, indiqué dans la feuille)");
            AutomationProperties.SetAutomationId(_pivotNumeri, "EsportaOpzioni_Pivot_MostraNumeri");
            AutomationProperties.SetAutomationId(_pivotPercentualiStandard, "EsportaOpzioni_Pivot_MostraPercentuali");
            pannello.Children.Insert(0, _pivotPercentualiStandard);
            pannello.Children.Insert(0, _pivotNumeri);
        }
        if (pivot && _descrittore.FontiPivot is { Count: > 0 } fonti)
        {
            var loc = LocalizationService.Instance;
            _pivotFonte.ItemsSource = fonti.Select(f => new Scelta(f.Id, f.Etichetta)).ToList();
            _pivotFonte.SelectionChanged += (_, _) => AggiornaCampiPivot();
            _pivotPercentuale.ItemsSource = new[]
            {
                new Scelta("nessuna", loc.Scegli("Solo numeri", "Counts only", "Nombres uniquement")),
                new Scelta("riga", loc.Scegli("Percentuale per riga", "Percent of row", "Pourcentage par ligne")),
                new Scelta("colonna", loc.Scegli("Percentuale per colonna", "Percent of column", "Pourcentage par colonne")),
                new Scelta("totale", loc.Scegli("Percentuale sul totale", "Percent of total", "Pourcentage du total"))
            };
            _pivotMetrica.ItemsSource = new[]
            {
                new Scelta("conteggio", loc.Scegli("Conteggio", "Count", "Nombre")),
                new Scelta("somma", loc.Scegli("Somma", "Sum", "Somme")),
                new Scelta("media", loc.Scegli("Media", "Average", "Moyenne"))
            };
            _pivotMetrica.SelectionChanged += (_, _) => _pivotCampoValore.IsEnabled = (_pivotMetrica.SelectedItem as Scelta)?.Id is "somma" or "media";
            void Aggiungi(string titolo, ComboBox combo, string id)
            {
                combo.HorizontalAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetAutomationId(combo, "EsportaOpzioni_Pivot_" + id);
                pannello.Children.Add(new TextBlock { Text = titolo, Margin = new Thickness(0, 8, 0, 0) });
                pannello.Children.Add(combo);
            }
            Aggiungi(loc.Scegli("Fonte", "Source", "Source"), _pivotFonte, "Fonte");
            Aggiungi(loc.Scegli("Righe", "Rows", "Lignes"), _pivotRighe, "Righe");
            Aggiungi(loc.Scegli("Colonne", "Columns", "Colonnes"), _pivotColonne, "Colonne");
            Aggiungi(loc.Scegli("Misura", "Measure", "Mesure"), _pivotMetrica, "Metrica");
            Aggiungi(loc.Scegli("Campo numerico", "Numeric field", "Champ numérique"), _pivotCampoValore, "CampoValore");
            pannello.Children.Add(new TextBlock { Text = loc.Scegli(
                "Somma e media richiedono valori numerici: i valori vuoti sono esclusi, un valore non numerico interrompe l'export con un errore esplicito.",
                "Sum and average require numeric values: empty values are excluded, a non-numeric value stops the export with an explicit error.",
                "La somme et la moyenne exigent des valeurs numériques : les valeurs vides sont exclues, une valeur non numérique interrompt l'export avec une erreur explicite."), TextWrapping = TextWrapping.Wrap });
            Aggiungi(loc.Scegli("Percentuali", "Percentages", "Pourcentages"), _pivotPercentuale, "Percentuale");
        }
        return pannello;
    }

    private static void Seleziona(ComboBox combo, string? id, int predefinito = 0)
    {
        var voci = (combo.ItemsSource as IEnumerable<Scelta>)?.ToList() ?? [];
        combo.SelectedItem = voci.FirstOrDefault(v => v.Id == id) ?? voci.ElementAtOrDefault(predefinito) ?? voci.FirstOrDefault();
    }

    private void AggiornaCampiPivot()
    {
        var fonte = _descrittore.FontiPivot?.FirstOrDefault(f => f.Id == (_pivotFonte.SelectedItem as Scelta)?.Id);
        var righe = (_pivotRighe.SelectedItem as Scelta)?.Id;
        var colonne = (_pivotColonne.SelectedItem as Scelta)?.Id;
        var valore = (_pivotCampoValore.SelectedItem as Scelta)?.Id;
        var campi = fonte?.Campi.Select(c => new Scelta(c.Id, c.Etichetta)).ToList() ?? [];
        _pivotRighe.ItemsSource = campi;
        _pivotColonne.ItemsSource = campi;
        Seleziona(_pivotRighe, righe);
        Seleziona(_pivotColonne, colonne, 1);
        _pivotCampoValore.ItemsSource = fonte?.Campi.Where(c => c.Numerica).Select(c => new Scelta(c.Id, c.Etichetta)).ToList() ?? [];
        Seleziona(_pivotCampoValore, valore);
    }

    /// <summary>Riporta una selezione sulle caselle: sezioni attive e, per le tabelle, le colonne (nessuna lista = tutte).</summary>
    private void Applica(ExportConfigurazione c)
    {
        Seleziona(_pivotFonte, c.PivotFonte);
        Seleziona(_pivotRighe, c.PivotRighe);
        Seleziona(_pivotColonne, c.PivotColonne, 1);
        Seleziona(_pivotPercentuale, c.PivotPercentuale);
        Seleziona(_pivotMetrica, c.PivotMetrica);
        Seleziona(_pivotCampoValore, c.PivotCampoValore);
        _pivotNumeri.IsChecked = c.PivotMostraNumeri;
        _pivotPercentualiStandard.IsChecked = c.PivotMostraPercentuali;
        foreach (var s in _descrittore.Sezioni)
        {
            _sezioni[s.Id].IsChecked = c.HaSezione(s.Id);
            if (!_colonne.TryGetValue(s.Id, out var colonne)) continue;
            var scelte = c.ColonneDi(s.Id);
            foreach (var (id, casella) in colonne)
                casella.IsChecked = scelte == null || scelte.Contains(id, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Selezione corrente delle caselle. Con tutte le colonne di una tabella spuntate non si salva alcuna lista ("tutte"), cosi'
    /// una colonna aggiunta in futuro alla pagina entra da sola.</summary>
    public ExportConfigurazione Corrente()
    {
        var c = new ExportConfigurazione { Nome = (_nome.Text ?? "").Trim(),
            PivotFonte = (_pivotFonte.SelectedItem as Scelta)?.Id, PivotRighe = (_pivotRighe.SelectedItem as Scelta)?.Id,
            PivotColonne = (_pivotColonne.SelectedItem as Scelta)?.Id, PivotPercentuale = (_pivotPercentuale.SelectedItem as Scelta)?.Id };
        c.PivotMetrica = (_pivotMetrica.SelectedItem as Scelta)?.Id;
        c.PivotCampoValore = (_pivotCampoValore.SelectedItem as Scelta)?.Id;
        c.PivotMostraNumeri = _pivotNumeri.IsChecked == true;
        c.PivotMostraPercentuali = _pivotPercentualiStandard.IsChecked == true;
        foreach (var s in _descrittore.Sezioni)
        {
            if (_sezioni[s.Id].IsChecked != true) continue;
            c.Sezioni.Add(s.Id);
            if (!_colonne.TryGetValue(s.Id, out var colonne)) continue;
            var scelte = s.Colonne!.Where(k => colonne[k.Id].IsChecked == true).Select(k => k.Id).ToList();
            if (scelte.Count > 0 && scelte.Count < s.Colonne!.Count) c.Colonne[s.Id] = scelte;
        }
        return c;
    }

    private void Messaggio(string? testo)
    {
        _messaggio.Text = testo;
        _messaggio.IsVisible = !string.IsNullOrEmpty(testo);
    }

    private void AggiornaElenco(string? selezionata)
    {
        _aggiornandoElenco = true;
        try
        {
            var nomi = ExportConfigurazioniService.Carica(_state, _descrittore.Pagina).Configurazioni.Select(c => c.Nome).ToList();
            _elenco.ItemsSource = nomi;
            _elenco.SelectedItem = nomi.FirstOrDefault(n => string.Equals(n, selezionata, StringComparison.OrdinalIgnoreCase));
        }
        finally { _aggiornandoElenco = false; }
    }

    private void SalvaConfigurazione()
    {
        if (!VerificaColonne() || !VerificaPivot()) return;
        var corrente = Corrente();
        switch (ExportConfigurazioniService.Salva(_state, _descrittore.Pagina, corrente))
        {
            case EsitoSalvataggioExport.Salvata:
                Messaggio(null);
                AggiornaElenco(corrente.Nome);
                break;
            case EsitoSalvataggioExport.LimiteRaggiunto:
                Messaggio(string.Format(T("ExpDlgLimiteFmt"), ExportConfigurazioniService.Massimo));
                break;
            default:
                Messaggio(string.Format(T("ExpDlgNomeNonValidoFmt"), ExportConfigurazioniService.LunghezzaMassimaNome));
                break;
        }
    }

    private void EliminaConfigurazione()
    {
        var nome = (_nome.Text ?? "").Trim();
        if (nome.Length == 0 || !ExportConfigurazioniService.Elimina(_state, _descrittore.Pagina, nome)) return;
        _nome.Text = "";
        Messaggio(null);
        AggiornaElenco(null);
    }

    private void Conferma()
    {
        var scelta = Corrente();
        if (scelta.Sezioni.Count == 0) { Messaggio(T("ExpDlgNessunaSezione")); return; }
        if (!VerificaColonne() || !VerificaPivot()) return;
        ExportConfigurazioniService.SalvaUltimaSelezione(_state, _descrittore.Pagina, scelta);
        ApplicaFiltri();
        _risultato.TrySetResult(scelta);
        Finestra.Close();
    }

    private bool VerificaColonne()
    {
        if (!_colonne.Any(c => _sezioni[c.Key].IsChecked == true && c.Value.Values.All(v => v.IsChecked != true))) return true;
        Messaggio(LocalizationService.Instance.Scegli("Seleziona almeno una colonna per ogni tabella inclusa.",
            "Select at least one column for each included table.", "Sélectionnez au moins une colonne pour chaque tableau inclus."));
        return false;
    }

    private bool VerificaPivot()
    {
        if (string.Equals(_descrittore.Pagina, "report", StringComparison.OrdinalIgnoreCase)
            && _pivotNumeri.IsChecked != true && _pivotPercentualiStandard.IsChecked != true)
        {
            Messaggio(LocalizationService.Instance.Scegli("Le pivot standard devono mostrare almeno i numeri o le percentuali.",
                "Standard pivots must show at least counts or percentages.", "Les tableaux standard doivent afficher au moins les nombres ou les pourcentages."));
            return false;
        }
        if (!_sezioni.TryGetValue(ReportSezioniExport.Pivot, out var sezione) || sezione.IsChecked != true
            || _descrittore.FontiPivot is not { Count: > 0 }) return true;
        if (_pivotRighe.SelectedItem is not Scelta righe || _pivotColonne.SelectedItem is not Scelta colonne || righe.Id == colonne.Id)
        {
            Messaggio(LocalizationService.Instance.Scegli("Scegli due campi diversi per righe e colonne della pivot.",
                "Choose different fields for pivot rows and columns.", "Choisissez deux champs différents pour les lignes et les colonnes du tableau croisé."));
            return false;
        }
        if (((_pivotMetrica.SelectedItem as Scelta)?.Id is "somma" or "media") && _pivotCampoValore.SelectedItem is not Scelta)
        {
            Messaggio(LocalizationService.Instance.Scegli("Scegli un campo numerico per somma o media.",
                "Choose a numeric field for sum or average.", "Choisissez un champ numérique pour la somme ou la moyenne."));
            return false;
        }
        return true;
    }
}
