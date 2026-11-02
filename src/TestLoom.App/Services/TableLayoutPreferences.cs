using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>Comportamento riutilizzabile per tutte le DataGrid. Click destro sulle intestazioni o sulla
/// tabella apre l'elenco colonne; trascinamento, ridimensionamento e ordinamento scelto su un'intestazione vengono
/// salvati automaticamente e riapplicati all'apertura della vista.</summary>
public static class TableLayoutPreferences
{
    private static AppState? _state;

    /// <summary>ConditionalWeakTable invece di Dictionary/HashSet chiavi-forti (Addendum: audit prestazioni
    /// 2026-08-25): una DataGrid è viva solo finché è nell'albero visuale; tenerla come chiave forte in una
    /// collezione statica (di durata pari all'intera sessione app) impediva al garbage collector di liberarla
    /// anche dopo che la pagina che la conteneva era stata abbandonata — ogni DataGrid mai creata restava in
    /// memoria con tutte le sue righe per tutta la sessione. Con chiavi deboli l'entry sparisce da sola
    /// quando la griglia non è più referenziata da nessun'altra parte, senza bisogno di un evento di cleanup.</summary>
    private static readonly ConditionalWeakTable<DataGrid, object> Registrate = new();
    private static readonly object Sentinella = new();

    /// <summary>DataGridRow riutilizza i contenitori durante la virtualizzazione: ogni caricamento reimposta l'altezza
    /// prima di applicare quella salvata per l'elemento corrente, così una riga ridimensionata non "contamina" la successiva.</summary>
    private static readonly ConditionalWeakTable<DataGridRow, object> RigheRegistrate = new();
    private static readonly ConditionalWeakTable<DataGridRow, StatoRidimensionamentoRiga> RigheInRidimensionamento = new();

    private sealed record StatoRidimensionamentoRiga(IPointer Puntatore, TopLevel TopLevel, double YIniziale, double AltezzaIniziale);

    /// <summary>Larghezze (Star o Pixel) con cui ogni colonna e' dichiarata in XAML, catturate a Inizializza prima
    /// che qualunque layout salvato o azione dell'utente le sovrascriva: servono a "Ripristina colonne".</summary>
    private static readonly ConditionalWeakTable<DataGrid, DataGridLength[]> LarghezzeOriginali = new();

    /// <summary>Griglie in cui il puntatore e' stato premuto sul bordo di un'intestazione (inizio di un
    /// ridimensionamento): solo al rilascio successivo le larghezze vanno salvate (un click di ordinamento no).</summary>
    private static readonly ConditionalWeakTable<DataGrid, StatoPressione> RidimensionamentoInCorso = new();

    /// <summary>Larghezze (pre-congelamento e post-congelamento) all'inizio di un possibile ridimensionamento:
    /// al rilascio si salva solo se almeno una larghezza e' davvero cambiata, altrimenti si ripristina l'originale.</summary>
    private sealed record StatoPressione(DataGridLength[] Prima, DataGridLength[] Congelate);

    /// <summary>Evita che i cambi programmatici di DisplayIndex in Applica/Ripristina producano un Salva.</summary>
    private static bool _applicazioneInCorso;

    /// <summary>Margine (px) dal bordo sinistro/destro dell'intestazione in cui il DataGrid avvia un ridimensionamento.</summary>
    private const double MargineRidimensionamento = 8;
    private const double MargineRidimensionamentoRiga = 5;
    private const double AltezzaRigaMinima = 28;
    private const double AltezzaRigaMassima = 320;
    // Avalonia crea i cursori tramite ICursorFactory, disponibile solo dopo aver inizializzato una piattaforma
    // grafica. La griglia può essere usata anche nei test headless: inizializzarlo al caricamento del tipo faceva
    // fallire ogni uso del servizio prima ancora di mostrare una finestra. Lo creiamo al primo hover reale.
    private static readonly Lazy<Cursor> CursoreRidimensionamentoRiga =
        new(() => new Cursor(StandardCursorType.SizeNorthSouth));

    /// <summary>Versione corrente delle larghezze salvate (6.4.6): fino alla 6.4.5 Salva catturava le ActualWidth a
    /// OGNI rilascio del puntatore sulla griglia, anche un semplice click su una riga, quindi su una finestra
    /// piccola (es. Panasonic G2, 1280x800 logici) le colonne schiacciate dalla mancanza di spazio diventavano una
    /// "preferenza" permanente (larghezze 40 px su Da/Archivia) e nascondevano anche le nuove larghezze di default.
    /// Le larghezze con versione precedente vengono ignorate (ordine e visibilita' restano).</summary>
    internal const int VersioneLayoutCorrente = 2;

    /// <summary>Vero se le larghezze salvate in <paramref name="layout"/> vanno riapplicate.</summary>
    internal static bool LarghezzeApplicabili(TableLayoutPreference layout) => layout.Versione >= VersioneLayoutCorrente;

    /// <summary>Vero se il puntatore e' stato rilasciato su un'intestazione di colonna (ridimensionamento o
    /// trascinamento): solo allora le larghezze riflettono una scelta dell'utente e vanno salvate.</summary>
    private static bool SuIntestazione(object? sorgente) =>
        sorgente is Visual v && v.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().Any();

    /// <summary>Larghezza da salvare per una colonna: 0 (= "non applicare") se <paramref name="autoOriginale"/> (era Auto
    /// in XAML e l'utente non l'ha ridimensionata: un resize la rende pixel, e allora se ne salva la larghezza); per una colonna
    /// nascosta si conserva la larghezza precedente (ActualWidth e' ~0/minimo) invece di un moncone di 40 px.</summary>
    internal static double LarghezzaPerSalvataggio(bool visibile, bool autoOriginale, double actual, double minWidth, double precedente)
    {
        if (autoOriginale) return 0;
        if (!visibile) return precedente >= 40 ? precedente : 0;
        return Math.Max(Math.Max(40, minWidth), actual);
    }

    /// <summary>Vero se la larghezza salvata va applicata come pixel alla colonna.</summary>
    internal static bool DevoApplicareLarghezza(bool larghezzeApplicabili, bool visibile, bool autoOriginale, double larghezza) =>
        larghezzeApplicabili && visibile && !autoOriginale && larghezza >= 40;

    /// <summary>Vero se almeno una larghezza differisce fra i due elenchi.</summary>
    internal static bool LarghezzeCambiate(IReadOnlyList<DataGridLength> a, IReadOnlyList<DataGridLength> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
            if (a[i].UnitType != b[i].UnitType || Math.Abs(a[i].Value - b[i].Value) > 0.5) return true;
        return false;
    }

    private static bool AutoOriginale(DataGrid grid, int indice) =>
        LarghezzeOriginali.TryGetValue(grid, out var o) && indice >= 0 && indice < o.Length && o[indice].IsAuto;

    public static readonly AttachedProperty<string?> TableIdProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, Control, string?>("TableId");

    /// <summary>Colonna non applicabile alla vista corrente (es. "Veicoli Extra" nella lista Requisiti Standard): resta
    /// sempre nascosta, non compare nel menu colonne e il layout salvato non la riporta in vista ne la sovrascrive.</summary>
    public static readonly AttachedProperty<bool> NonApplicabileProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, DataGridColumn, bool>("NonApplicabile");

    public static void SetNonApplicabile(DataGridColumn colonna, bool value) => colonna.SetValue(NonApplicabileProperty, value);
    public static bool GetNonApplicabile(DataGridColumn colonna) => colonna.GetValue(NonApplicabileProperty);

    /// <summary>Colonna a bassa priorita' (6.6.1): in Compatto e Minimo parte nascosta, ma SOLO finche' la tabella non ha una
    /// preferenza salvata (una scelta dell'utente vince sempre). Il menu colonne la riattiva in ogni modo.</summary>
    public static readonly AttachedProperty<bool> BassaPrioritaProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, DataGridColumn, bool>("BassaPriorita");

    /// <summary>Ordine comodo usato soltanto quando non esiste ancora una preferenza. Ogni colonna resta
    /// liberamente riordinabile e il nuovo ordine personale viene salvato.</summary>
    public static readonly AttachedProperty<int?> OrdineInizialeProperty =
        AvaloniaProperty.RegisterAttached<DataGridColumn, DataGridColumn, int?>("OrdineIniziale");

    public static void SetOrdineIniziale(DataGridColumn colonna, int? value) => colonna.SetValue(OrdineInizialeProperty, value);
    public static int? GetOrdineIniziale(DataGridColumn colonna) => colonna.GetValue(OrdineInizialeProperty);

    public static void SetBassaPriorita(DataGridColumn colonna, bool value) => colonna.SetValue(BassaPrioritaProperty, value);
    public static bool GetBassaPriorita(DataGridColumn colonna) => colonna.GetValue(BassaPrioritaProperty);

    /// <summary>Visibilita' predefinita di una colonna senza preferenza salvata: nascosta se e' a bassa priorita' e il modo e' Compatto o Minimo.</summary>
    internal static bool VisibilitaPredefinita(DataGridColumn colonna) =>
        !GetNonApplicabile(colonna) && !(GetBassaPriorita(colonna) && LayoutAdattivo.ModoCorrente <= ModoLayout.Compatto);

    static TableLayoutPreferences() =>
        TableIdProperty.Changed.AddClassHandler<DataGrid>((grid, _) => Registra(grid));

    public static void Configure(AppState state) => _state = state;
    public static void SetTableId(Control control, string? value) => control.SetValue(TableIdProperty, value);
    public static string? GetTableId(Control control) => control.GetValue(TableIdProperty);

    public static string Chiave(string operatore, RuoloOperatore ruolo, string tableId) =>
        $"{operatore.Trim()}|{ruolo}|{tableId.Trim()}";

    private static void Registra(DataGrid grid)
    {
        if (!Registrate.TryAdd(grid, Sentinella)) return;
        // Il TableId identifica ogni DataGrid come tabella personalizzabile: nessuna pagina deve ricordarsi di
        // abilitare a mano resize/riordino delle colonne o il ridimensionamento delle righe.
        grid.CanUserResizeColumns = true;
        grid.CanUserReorderColumns = true;
        grid.LoadingRow += (_, e) => PreparaRiga(grid, e.Row);
        grid.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Inizializza(grid));
        grid.ColumnReordered += (_, _) =>
        {
            if (_applicazioneInCorso) return;
            Salva(grid);
        };
        // Ordinamento: il salvataggio va rimandato a dopo l'ordinamento stesso (Sorting arriva prima che la griglia
        // ordini). Handled = ordinamento gestito dal ViewModel (Anomalie ordina sull'intero risultato e persiste da se'):
        // non e' un ordinamento della griglia, quindi non si salva qui.
        grid.Sorting += (_, e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!e.Handled) SalvaOrdinamento(grid); });
        // Tunnel: prima che l'intestazione inizi il ridimensionamento le colonne Star vanno "congelate" in pixel.
        // Una colonna Star riempie sempre lo spazio residuo, quindi trascinarne il bordo non ha alcun effetto
        // (con una sola Star e le altre fisse non puo' ne' allargarsi ne' stringersi) e la griglia non scorrerebbe
        // mai in orizzontale: in pixel, invece, allargare una colonna sposta le altre e produce lo scorrimento.
        grid.AddHandler(InputElement.PointerPressedEvent, (_, e) => InizioPressione(grid, e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!RidimensionamentoInCorso.TryGetValue(grid, out var stato)) return;
            RidimensionamentoInCorso.Remove(grid);
            if (!SuIntestazione(e.Source)) return;
            var attuali = grid.Columns.Select(c => c.Width).ToArray();
            if (!LarghezzeCambiate(stato.Congelate, attuali))
            {
                // Nessun ridimensionamento reale (es. click di ordinamento vicino al bordo): annulla il congelamento.
                if (stato.Prima.Length == grid.Columns.Count)
                    for (var i = 0; i < stato.Prima.Length; i++) grid.Columns[i].Width = stato.Prima[i];
                return;
            }
            Salva(grid);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void PreparaRiga(DataGrid grid, DataGridRow riga)
    {
        riga.Height = double.NaN; // i DataGridRow vengono riciclati: elimina sempre l'altezza del record precedente.
        riga.Cursor = null;
        if (RigheRegistrate.TryAdd(riga, Sentinella))
        {
            riga.AddHandler(InputElement.PointerPressedEvent, OnRigaPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            riga.AddHandler(InputElement.PointerMovedEvent, OnRigaPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            riga.AddHandler(InputElement.PointerReleasedEvent, OnRigaPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            riga.AddHandler(InputElement.PointerCaptureLostEvent, OnRigaPointerCaptureLost, RoutingStrategies.Direct, handledEventsToo: true);
            riga.AddHandler(InputElement.PointerExitedEvent, OnRigaPointerExited, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        var chiaveRiga = ChiaveRiga(grid, riga);
        if (chiaveRiga.Length == 0 || _state == null || string.IsNullOrWhiteSpace(GetTableId(grid))) return;
        var chiaveTabella = Chiave(_state.Config.OperatoreAttivo, _state.RuoloAttivo, GetTableId(grid)!);
        if (_state.Config.LayoutTabelle?.TryGetValue(chiaveTabella, out var layout) != true || layout?.AltezzeRighe == null) return;
        if (!layout.AltezzeRighe.TryGetValue(chiaveRiga, out var altezza)) return;
        riga.Height = LimitaAltezza(altezza, riga.MinHeight);
    }

    private static void OnRigaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGridRow riga || !e.GetCurrentPoint(riga).Properties.IsLeftButtonPressed
            || e.GetPosition(riga).Y < riga.Bounds.Height - MargineRidimensionamentoRiga) return;
        var griglia = riga.GetVisualAncestors().OfType<DataGrid>().FirstOrDefault();
        var topLevel = TopLevel.GetTopLevel(riga);
        if (griglia == null || topLevel == null) return;

        var altezza = double.IsNaN(riga.Height) ? riga.Bounds.Height : riga.Height;
        if (altezza <= 0) return;
        RigheInRidimensionamento.Remove(riga);
        RigheInRidimensionamento.Add(riga, new StatoRidimensionamentoRiga(e.Pointer, topLevel,
            e.GetPosition(topLevel).Y, altezza));
        e.Pointer.Capture(riga);
        e.Handled = true;
    }

    private static void OnRigaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not DataGridRow riga) return;
        if (RigheInRidimensionamento.TryGetValue(riga, out var stato) && ReferenceEquals(stato.Puntatore, e.Pointer))
        {
            var delta = e.GetPosition(stato.TopLevel).Y - stato.YIniziale;
            riga.Height = LimitaAltezza(stato.AltezzaIniziale + delta, riga.MinHeight);
            e.Handled = true;
            return;
        }

        riga.Cursor = e.GetPosition(riga).Y >= riga.Bounds.Height - MargineRidimensionamentoRiga
            ? CursoreRidimensionamentoRiga.Value : null;
    }

    private static void OnRigaPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not DataGridRow riga || !RigheInRidimensionamento.TryGetValue(riga, out var stato)
            || !ReferenceEquals(stato.Puntatore, e.Pointer)) return;
        RigheInRidimensionamento.Remove(riga);
        e.Pointer.Capture(null);
        if (riga.GetVisualAncestors().OfType<DataGrid>().FirstOrDefault() is { } griglia)
            SalvaAltezzaRiga(griglia, riga);
        e.Handled = true;
    }

    private static void OnRigaPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is not DataGridRow riga || !RigheInRidimensionamento.TryGetValue(riga, out var stato)
            || !ReferenceEquals(stato.Puntatore, e.Pointer)) return;
        RigheInRidimensionamento.Remove(riga);
        if (riga.GetVisualAncestors().OfType<DataGrid>().FirstOrDefault() is { } griglia)
            SalvaAltezzaRiga(griglia, riga);
    }

    private static void OnRigaPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is DataGridRow riga && !RigheInRidimensionamento.TryGetValue(riga, out _)) riga.Cursor = null;
    }

    internal static double LimitaAltezza(double altezza, double altezzaMinima)
    {
        var minimo = Math.Min(AltezzaRigaMassima, Math.Max(AltezzaRigaMinima, double.IsFinite(altezzaMinima) ? altezzaMinima : 0));
        return Math.Clamp(altezza, minimo, AltezzaRigaMassima);
    }

    private static string ChiaveRiga(DataGrid grid, DataGridRow riga) => ChiaveRiga(riga.DataContext, riga.Index);

    internal static string ChiaveRiga(object? elemento, int indice)
    {
        if (elemento == null) return string.Empty;
        var tipo = elemento.GetType().FullName ?? elemento.GetType().Name;
        var identita = LeggiIdentita(elemento);
        if (identita == null && elemento.GetType().GetProperty("Nc", BindingFlags.Instance | BindingFlags.Public)?.GetValue(elemento) is { } nc)
            identita = LeggiIdentita(nc) is { } idNc ? "Nc:" + idNc : null;
        if (identita != null) return tipo + "|" + identita;

        // Le tabelle di anteprima/aggregazione senza chiave di record restano ridimensionabili per posizione;
        // il loro contenuto e ordine sono generati dalla stessa query e non scrivono dati di dominio.
        return indice >= 0 ? tipo + "|riga:" + indice.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string? LeggiIdentita(object elemento)
    {
        foreach (var nome in new[] { "Uid", "Id", "Codice", "Key" })
        {
            var proprieta = elemento.GetType().GetProperty(nome, BindingFlags.Instance | BindingFlags.Public);
            if (proprieta == null || proprieta.GetIndexParameters().Length != 0) continue;
            object? valore;
            try { valore = proprieta.GetValue(elemento); }
            catch (TargetInvocationException) { continue; }
            if (valore is Guid { } guid && guid == Guid.Empty) continue;
            if (valore == null || valore is string testo && string.IsNullOrWhiteSpace(testo)) continue;
            if (valore is IFormattable formattabile)
                return nome.ToLowerInvariant() + ":" + formattabile.ToString(null, CultureInfo.InvariantCulture);
            return nome.ToLowerInvariant() + ":" + valore;
        }
        return null;
    }

    internal static void SalvaAltezzaRiga(DataGrid grid, DataGridRow riga)
    {
        lock (AppConfigService.ConfigSync)
        {
            if (_state == null || grid.Columns.Count == 0 || string.IsNullOrWhiteSpace(GetTableId(grid))) return;
            var chiaveRiga = ChiaveRiga(grid, riga);
            if (chiaveRiga.Length == 0 || !double.IsFinite(riga.Height)) return;
            var chiaveTabella = Chiave(_state.Config.OperatoreAttivo, _state.RuoloAttivo, GetTableId(grid)!);
            _state.Config.LayoutTabelle ??= new(StringComparer.OrdinalIgnoreCase);
            _state.Config.LayoutTabelle.TryGetValue(chiaveTabella, out var layout);
            // Salvare solo l'altezza di una riga non deve congelare le larghezze Star correnti:
            // la tabella deve restare responsive finché l'utente non personalizza le colonne.
            layout ??= new TableLayoutPreference { Versione = VersioneLayoutCorrente };
            layout.AltezzeRighe ??= new(StringComparer.OrdinalIgnoreCase);
            if (layout.AltezzeRighe.TryGetValue(chiaveRiga, out var precedente) && Math.Abs(precedente - riga.Height) < 0.5) return;
            layout.AltezzeRighe[chiaveRiga] = riga.Height;
            _state.Config.LayoutTabelle[chiaveTabella] = layout;
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Salvataggio altezza riga tabella", ex); }
        }
    }

    private static void InizioPressione(DataGrid grid, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual v || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
        var header = v.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault();
        if (header == null) return;
        var x = e.GetPosition(header).X;
        if (x > MargineRidimensionamento && x < header.Bounds.Width - MargineRidimensionamento) return;
        var prima = grid.Columns.Select(c => c.Width).ToArray();
        CongelaColonne(grid);
        RidimensionamentoInCorso.AddOrUpdate(grid, new StatoPressione(prima, grid.Columns.Select(c => c.Width).ToArray()));
    }

    /// <summary>Converte in pixel (larghezza attuale) le colonne Star visibili e gia' misurate.</summary>
    private static void CongelaColonne(DataGrid grid)
    {
        foreach (var c in grid.Columns)
            if (c.IsVisible && c.Width.IsStar && c.ActualWidth > 0) c.Width = new DataGridLength(c.ActualWidth);
    }

    private static void Inizializza(DataGrid grid)
    {
        if (_state == null || string.IsNullOrWhiteSpace(GetTableId(grid)) || grid.Columns.Count == 0) return;
        LarghezzeOriginali.TryAdd(grid, grid.Columns.Select(c => c.Width).ToArray());
        var chiave = Chiave(_state.Config.OperatoreAttivo, _state.RuoloAttivo, GetTableId(grid)!);
        _state.Config.LayoutTabelle ??= new(StringComparer.OrdinalIgnoreCase);
        _state.Config.LayoutTabelle.TryGetValue(chiave, out var layout);
        if (layout?.Colonne?.Count == grid.Columns.Count)
        {
            Applica(grid, layout);
        }
        else
        {
            // Nessuna preferenza salvata: le colonne a bassa priorita' partono dalla visibilita' predefinita del modo (6.6.1).
            foreach (var c in grid.Columns) if (GetBassaPriorita(c)) c.IsVisible = VisibilitaPredefinita(c);
            ApplicaOrdineIniziale(grid);
        }
        // L'ordinamento è indipendente dal layout delle colonne: può essere l'unica preferenza salvata,
        // oppure restare valido dopo l'aggiunta di una colonna in una nuova versione.
        AplicaOrdinamento(grid, layout?.Ordinamento);
        CreaMenu(grid);
    }

    private static void ApplicaOrdineIniziale(DataGrid grid)
    {
        var ordinate = grid.Columns.Where(c => GetOrdineIniziale(c).HasValue)
            .OrderBy(c => GetOrdineIniziale(c)!.Value).ToArray();
        if (ordinate.Length == 0) return;
        var precedente = _applicazioneInCorso;
        _applicazioneInCorso = true;
        try
        {
            for (var i = 0; i < ordinate.Length; i++)
            {
                var colonna = ordinate[i];
                colonna.IsVisible = true;
                colonna.DisplayIndex = Math.Clamp(GetOrdineIniziale(colonna)!.Value, 0, grid.Columns.Count - 1);
            }
        }
        finally { _applicazioneInCorso = precedente; }
    }

    private static TableLayoutPreference Cattura(DataGrid grid, TableLayoutPreference? esistente) => new()
    {
        Versione = VersioneLayoutCorrente,
        Ordinamento = esistente?.Ordinamento,
        AltezzeRighe = esistente?.AltezzeRighe == null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(esistente.AltezzeRighe, StringComparer.OrdinalIgnoreCase),
        Colonne = grid.Columns.Select((c, i) => new TableColumnPreference
        {
            Indice = i, Ordine = c.DisplayIndex, Visibile = c.IsVisible || GetNonApplicabile(c),
            Larghezza = LarghezzaPerSalvataggio(c.IsVisible, AutoOriginale(grid, i) && c.Width.IsAuto, c.ActualWidth, c.MinWidth,
                esistente != null && esistente.Versione >= VersioneLayoutCorrente
                    ? esistente.Colonne?.FirstOrDefault(x => x.Indice == i)?.Larghezza ?? 0 : 0)
        }).ToList()
    };

    /// <summary>Riapplica ordine/visibilita' e, se la versione lo consente, le larghezze come PIXEL per tutte le
    /// colonne: le larghezze salvate sono quelle scelte/viste dall'utente (le Star vengono congelate in pixel al
    /// primo ridimensionamento), e solo colonne in pixel permettono di allargarne una e scorrere in orizzontale
    /// invece di schiacciare le vicine.</summary>
    private static void Applica(DataGrid grid, TableLayoutPreference layout)
    {
        if (layout.Colonne == null) return;
        var applicaLarghezze = LarghezzeApplicabili(layout);
        _applicazioneInCorso = true;
        try
        {
            foreach (var p in layout.Colonne.OrderBy(x => x.Ordine))
            {
                if (p.Indice < 0 || p.Indice >= grid.Columns.Count) continue;
                var colonna = grid.Columns[p.Indice];
                colonna.IsVisible = p.Visibile && !GetNonApplicabile(colonna);
                // Una colonna Auto in XAML ha una larghezza salvata (>0) solo se l'utente l'ha ridimensionata davvero
                // (vedi Cattura): in quel caso va riapplicata in pixel; le Auto mai toccate salvano 0 e restano Auto.
                if (DevoApplicareLarghezza(applicaLarghezze, p.Visibile, false, p.Larghezza))
                    colonna.Width = new DataGridLength(Math.Max(p.Larghezza, colonna.MinWidth));
                colonna.DisplayIndex = Math.Clamp(p.Ordine, 0, grid.Columns.Count - 1);
            }
            if (!grid.Columns.Any(c => c.IsVisible) && grid.Columns.Count > 0)
                grid.Columns.First(c => !GetNonApplicabile(c)).IsVisible = true;
        }
        finally { _applicazioneInCorso = false; }
    }

    /// <summary>Torna al layout dichiarato in XAML (larghezze originali, ordine e visibilita') e cancella la
    /// preferenza salvata.</summary>
    private static void Ripristina(DataGrid grid)
    {
        _applicazioneInCorso = true;
        try { RipristinaColonne(grid); }
        finally { _applicazioneInCorso = false; }
        RimuoviPreferenza(grid);
        AzzeraOrdinamentoCorrente(grid);
    }

    private static void RipristinaColonne(DataGrid grid)
    {
        if (LarghezzeOriginali.TryGetValue(grid, out var originali))
            for (var i = 0; i < grid.Columns.Count && i < originali.Length; i++) grid.Columns[i].Width = originali[i];
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            grid.Columns[i].IsVisible = !GetNonApplicabile(grid.Columns[i]);
            grid.Columns[i].DisplayIndex = i;
        }
        ApplicaOrdineIniziale(grid);
        // Il ripristino torna alla visibilita' predefinita del modo: le colonne a bassa priorita' tornano nascoste in Compatto.
        foreach (var c in grid.Columns) if (GetBassaPriorita(c)) c.IsVisible = VisibilitaPredefinita(c);
        foreach (var riga in grid.GetVisualDescendants().OfType<DataGridRow>()) riga.Height = double.NaN;
    }

    private static void RimuoviPreferenza(DataGrid grid)
    {
        lock (AppConfigService.ConfigSync)
        {
            if (_state == null || string.IsNullOrWhiteSpace(GetTableId(grid))) return;
            var chiave = Chiave(_state.Config.OperatoreAttivo, _state.RuoloAttivo, GetTableId(grid)!);
            if (_state.Config.LayoutTabelle?.Remove(chiave) != true) return;
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Ripristino disposizione tabella", ex); }
        }
    }

    private static void Salva(DataGrid grid)
    {
        lock (AppConfigService.ConfigSync)
        {
            if (_state == null || string.IsNullOrWhiteSpace(GetTableId(grid)) || grid.Columns.Count == 0) return;
            var chiave = Chiave(_state.Config.OperatoreAttivo, _state.RuoloAttivo, GetTableId(grid)!);
            _state.Config.LayoutTabelle ??= new(StringComparer.OrdinalIgnoreCase);
            CongelaColonne(grid);
            _state.Config.LayoutTabelle.TryGetValue(chiave, out var esistente);
            var nuovo = Cattura(grid, esistente);

            // Ogni PointerReleased sulla griglia (anche un semplice click su una riga, non solo un
            // trascinamento colonna) chiamava Salva incondizionatamente: riscriveva l'intero config.json,
            // comprese le preferenze di TUTTE le altre tabelle (feedback utente 2026-08-25, audit prestazioni).
            // Se il layout catturato ora è identico all'ultimo salvato, non c'è nulla da persistere.
            if (esistente != null && SonoUguali(esistente, nuovo)) return;

            _state.Config.LayoutTabelle[chiave] = nuovo;
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Salvataggio disposizione tabella", ex); }
        }
    }

    private static bool SonoUguali(TableLayoutPreference a, TableLayoutPreference b)
    {
        if (a.Versione != b.Versione) return false;
        var altezzeA = a.AltezzeRighe ?? new(StringComparer.OrdinalIgnoreCase);
        var altezzeB = b.AltezzeRighe ?? new(StringComparer.OrdinalIgnoreCase);
        if (altezzeA.Count != altezzeB.Count || altezzeA.Any(x => !altezzeB.TryGetValue(x.Key, out var valore) || Math.Abs(x.Value - valore) > 0.5)) return false;
        if (a.Colonne == null || b.Colonne == null) return a.Colonne == b.Colonne;
        if (a.Colonne.Count != b.Colonne.Count) return false;
        for (var i = 0; i < a.Colonne.Count; i++)
        {
            var (x, y) = (a.Colonne[i], b.Colonne[i]);
            if (x.Indice != y.Indice || x.Ordine != y.Ordine || x.Visibile != y.Visibile || x.Larghezza != y.Larghezza) return false;
        }
        return true;
    }

    // === Ordinamento per colonna ===================================================================================
    // Avalonia 12.1.2 non espone una proprieta' SortDirection pubblica: lo stato sta in
    // DataGrid.CollectionView.SortDescriptions (pubblico) e si applica con DataGridColumn.Sort(direction), che passa
    // dallo stesso percorso di un clic sull'intestazione (evento Sorting e indicatori di colonna compresi).

    /// <summary>Descrizioni di ordinamento correnti della griglia, come (percorso, direzione).</summary>
    private static IEnumerable<(string? Percorso, ListSortDirection Direzione)> DescrizioniOrdinamento(DataGrid grid) =>
        grid.CollectionView?.SortDescriptions.Select(d => (d.HasPropertyPath ? d.PropertyPath : null, d.Direction))
        ?? Enumerable.Empty<(string? Percorso, ListSortDirection Direzione)>();

    /// <summary>Ordinamento da salvare: la prima descrizione (con Maiusc+clic il primario e' la prima). Nessun
    /// ordinamento, o un ordinamento senza percorso (comparer personalizzato, non ripristinabile per nome), e' null.</summary>
    internal static TableSortPreference? CatturaOrdinamento(IEnumerable<(string? Percorso, ListSortDirection Direzione)> descrizioni)
    {
        var (percorso, direzione) = descrizioni.FirstOrDefault();
        return string.IsNullOrEmpty(percorso) ? null : new TableSortPreference { Percorso = percorso, Direzione = direzione };
    }

    /// <summary>Indice della prima colonna ordinabile con quel percorso, -1 se non c'e' (colonna non piu' presente o non
    /// ordinabile).</summary>
    internal static int TrovaColonnaOrdinabile(IReadOnlyList<(string? Percorso, bool Ordinabile)> colonne, string percorso)
    {
        for (var i = 0; i < colonne.Count; i++)
            if (colonne[i].Ordinabile && colonne[i].Percorso == percorso) return i;
        return -1;
    }

    /// <summary>Vero se l'ordinamento salvato va riapplicato: serve un ordinamento con percorso e la griglia deve
    /// consentire di ordinare (TestCaseList resta nell'ordine fisso con CanUserSortColumns = false).</summary>
    internal static bool DevoApplicareOrdinamento(bool ordinamentoConsentito, TableSortPreference? ordinamento) =>
        ordinamentoConsentito && !string.IsNullOrEmpty(ordinamento?.Percorso);

    /// <summary>Vero se due ordinamenti coincidono: stessa colonna e stessa direzione (due assenze coincidono).</summary>
    internal static bool SonoUgualiOrdinamento(TableSortPreference? a, TableSortPreference? b) =>
        a == null || b == null ? a == b : a.Percorso == b.Percorso && a.Direzione == b.Direzione;

    /// <summary>Proprieta' su cui Avalonia ordina una colonna: SortMemberPath, altrimenti il path del Binding (stesso
    /// criterio di DataGridColumn.GetSortPropertyName, che in Avalonia e' interno).</summary>
    internal static string? PercorsoOrdinamento(DataGridColumn colonna)
    {
        if (!string.IsNullOrEmpty(colonna.SortMemberPath)) return colonna.SortMemberPath;
        if (colonna is not DataGridBoundColumn { Binding: { } binding }) return null;
        return binding switch
        {
            CompiledBinding compilato => compilato.Path?.ToString(),
            Binding legato => legato.Path,
            ReflectionBinding riflessione => riflessione.Path,
            _ => null,
        };
    }

    /// <summary>Riapplica l'ordinamento salvato come un clic sull'intestazione (DataGridColumn.Sort): stesso evento
    /// Sorting e stessi indicatori. Non tocca un ordinamento gia' attivo (l'utente ne ha scelto uno nel frattempo).</summary>
    private static void AplicaOrdinamento(DataGrid grid, TableSortPreference? ordinamento)
    {
        if (ordinamento == null || !DevoApplicareOrdinamento(grid.CanUserSortColumns, ordinamento)) return;
        if (grid.CollectionView?.SortDescriptions.Count > 0) return;
        var colonne = grid.Columns.Select(c => (Percorso: PercorsoOrdinamento(c), Ordinabile: c.CanUserSort)).ToList();
        var indice = TrovaColonnaOrdinabile(colonne, ordinamento.Percorso);
        if (indice >= 0) grid.Columns[indice].Sort(ordinamento.Direzione);
    }

    /// <summary>Salva l'ordinamento corrente della griglia. Non riscrive il layout di colonne e non crea una preferenza
    /// se non c'e' nulla da ricordare (nessun ordinamento e nessuna preferenza gia' salvata).</summary>
    private static void SalvaOrdinamento(DataGrid grid)
    {
        lock (AppConfigService.ConfigSync)
        {
            if (_state == null || string.IsNullOrWhiteSpace(GetTableId(grid))) return;
            var chiave = Chiave(_state.Config.OperatoreAttivo, _state.RuoloAttivo, GetTableId(grid)!);
            var nuovo = CatturaOrdinamento(DescrizioniOrdinamento(grid));
            _state.Config.LayoutTabelle ??= new(StringComparer.OrdinalIgnoreCase);
            _state.Config.LayoutTabelle.TryGetValue(chiave, out var esistente);
            if (SonoUgualiOrdinamento(esistente?.Ordinamento, nuovo)) return;

            // Preferenza creata solo per l'ordinamento: senza colonne, larghezze e ordine restano quelli XAML finche'
            // l'utente non ritocca il layout (allora Salva cattura le colonne come sempre).
            esistente ??= new TableLayoutPreference();
            esistente.Ordinamento = nuovo;
            _state.Config.LayoutTabelle[chiave] = esistente;
            try { _state.ConfigService.Save(_state.Config); }
            catch (Exception ex) { CrashLogService.Registra("Salvataggio ordinamento tabella", ex); }
        }
    }

    /// <summary>Ripristina colonne riporta anche l'ordinamento, perche' il layout XAML non ne ha uno. Una griglia ordinata
    /// dal ViewModel (Anomalie) non ha SortDescriptions e non viene toccata. ClearSort agisce su una sola colonna e
    /// l'ordinamento attivo puo' essere su una qualsiasi: si azzerano tutte le colonne ordinabili.</summary>
    private static void AzzeraOrdinamentoCorrente(DataGrid grid)
    {
        if (grid.CollectionView?.SortDescriptions.Count > 0)
            foreach (var colonna in grid.Columns.Where(c => c.CanUserSort)) colonna.ClearSort();
    }

    private static void CreaMenu(DataGrid grid)
    {
        var voci = new List<object>();
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var indice = i;
            var colonna = grid.Columns[i];
            if (GetNonApplicabile(colonna)) continue;
            var titolo = colonna.Header?.ToString();
            if (string.IsNullOrWhiteSpace(titolo)) titolo = $"{LocalizationService.Instance.Traduci("ColonnaAzioni")} {i + 1}";
            var voce = new MenuItem { Header = titolo, ToggleType = MenuItemToggleType.CheckBox, IsChecked = colonna.IsVisible };
            voce.Click += (_, _) =>
            {
                // L'ultima colonna visibile non può essere nascosta: una tabella completamente vuota non è recuperabile.
                if (colonna.IsVisible && grid.Columns.Count(c => c.IsVisible) <= 1) { voce.IsChecked = true; return; }
                colonna.IsVisible = voce.IsChecked;
                Salva(grid);
            };
            voci.Add(voce);
        }
        voci.Add(new Separator());
        var reset = new MenuItem { Header = LocalizationService.Instance.Traduci("RipristinaDisposizioneTabella") };
        reset.Click += (_, _) =>
        {
            Ripristina(grid);
            CreaMenu(grid);
        };
        voci.Add(reset);
        grid.ContextMenu = new ContextMenu { ItemsSource = voci };
        // Niente ToolTip.Tip qui (feedback utente 2026-08-25, punto residuo dell'audit prestazioni): un
        // tooltip attivo sulla DataGrid copre anche l'header al suo interno e riapre lo stesso blocco
        // dell'hit-test del resize/reorder gripper già risolto togliendo il tooltip dallo Style
        // DataGridColumnHeader in Styles.axaml — impediva di ridimensionare/riordinare le colonne mentre
        // il tooltip era a schermo. "Click destro per scegliere le colonne; trascina per riordinare" resta
        // comunque scopribile (click destro apre subito il menu), non serve un hint costante sopra la tabella.
    }
}
