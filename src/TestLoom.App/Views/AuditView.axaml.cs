using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Matrice Requisito×componente (6.6.1): una sola ItemsControl con VirtualizingStackPanel, una riga per requisito, costruita
/// in code-behind con un FuncDataTemplate. Il numero di colonne (una per componente del sottoveicolo scelto) cambia a ogni selezione,
/// cosa che un DataGrid a colonne fisse dichiarate in XAML non può rappresentare (Addendum: grill-me 2026-08-18, follow-up, punto 5).
///
/// La virtualizzazione è possibile perché la matrice è l'unico ScrollViewer verticale della pagina: la pagina non scorre più come
/// blocco (il ScrollViewer di pagina annidato con quello della matrice faceva fermare le righe a ST99). Le righe sono altezza fissa
/// (AltezzaRigaMatrice) e l'intestazione componente scorre in orizzontale con il corpo (AllineaIntestazione). Misura headless
/// (AuditMatriceLayoutHeadlessTests, 1700 requisiti x 20 componente, finestra 1280x672): prima 71.444 controlli e RicaricaMatrice con
/// mediana 8142 ms; dopo 238-326 controlli (solo le righe a schermo) e mediana circa 320 ms, vista compresa. Il VM non è cambiato.
///
/// Tasto destro su una cella (solo Master, feedback utente 2026-08-26, punto 2) apre
/// ApriInserimentoManualeFlyout per inserire/correggere un valore a mano, scegliendo la fonte più recente
/// sull'esito calcolato dai Test — vedi AuditViewModel.SalvaValutazioneManuale/RimuoviValutazioneManuale e
/// AuditMatriceService.Matrice.</summary>
public partial class AuditView : UserControl
{
    private const double LarghezzaColonnaRequisito = 150;
    private const double LarghezzaColonnaEcu = 92;
    private const double LarghezzaRequisitoMinima = 120;
    private const double LarghezzaRequisitoMassima = 480;
    private const double LarghezzaEcuMinima = 64;
    private const double LarghezzaEcuMassima = 320;
    private const double AltezzaRigaMinima = 26;
    private const double AltezzaRigaMassima = 240;
    private const double SpessoreAreaRidimensionamento = 7;
    private const string ChiavePreferenzeMatrice = "Audit.Matrice.Layout";
    private const string TagColonnaRequisito = "AUDIT:COLONNA:REQUISITO";
    private const string TagRiga = "AUDIT:RIGA:";
    private const string TagContenutoRiga = "AUDIT:RIGA:CONTENUTO";
    private const string TagCellaEcu = "AUDIT:CELLA:componente:";

    private enum TipoOperazioneMatrice { LarghezzaRequisito, LarghezzaEcu, AltezzaRiga, RiordinoEcu }

    private sealed class PreferenzeMatriceAudit
    {
        public int Versione { get; set; } = 1;
        public double? LarghezzaRequisito { get; set; }
        public Dictionary<string, double> LarghezzeEcu { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> OrdineEcu { get; set; } = new();
        public Dictionary<string, double> AltezzeRighe { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record IdentitaManigliaMatrice(TipoOperazioneMatrice Tipo, string Chiave);
    private sealed record StatoManigliaMatrice(IPointer Puntatore, Control Superficie, IdentitaManigliaMatrice Identita,
        Point PosizioneIniziale, double ValoreIniziale, int IndiceIniziale);

    private PreferenzeMatriceAudit _preferenzeMatrice = new();
    private IReadOnlyList<string> _colonneVisualizzate = Array.Empty<string>();
    private StatoManigliaMatrice? _ridimensionamentoMatrice;

    /// <summary>CellaMatriceAuditVisual.Colore pesca da una tavolozza chiusa di 8 esadecimali: prima
    /// RicostruisciGriglia faceva un Color.Parse + new SolidColorBrush per OGNI cella (Addendum: audit
    /// prestazioni 2026-08-25) — su una matrice Requisiti×componente di dimensioni reali sono migliaia di parse ed
    /// allocazioni per un risultato sempre uno degli stessi 8 pennelli, riusabili senza rischio: i Brush di
    /// Avalonia sono immutabili una volta creati.</summary>
    private static readonly Dictionary<string, IBrush> PennelliCella = new();

    private static IBrush PennelloPer(string colore)
    {
        if (!PennelliCella.TryGetValue(colore, out var pennello))
            PennelliCella[colore] = pennello = new SolidColorBrush(Color.Parse(colore));
        return pennello;
    }

    // I dialoghi Audit sono generati in code-behind e non beneficiano dei
    // DynamicResource delle View AXAML. I colori storici dei messaggi di errore
    // (#B91C1C/#DC2626) non superano 4.5:1 su superfici scure.
    private static bool TemaScuro =>
        Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
    private static IBrush PennelloErrore => PennelloPer(TemaScuro ? "#F17D82" : "#B91C1C");
    private static IBrush PennelloAvviso => PennelloPer(TemaScuro ? "#E2AD54" : "#C2410C");

    // Sottoscrizioni tenute a mano e simmetriche su Attached/DetachedFromVisualTree (Addendum: audit
    // prestazioni 2026-08-25; corretto ulteriormente 2026-09-25 per il riuso View del ViewLocator). Prima
    // LocalizationService.Instance.PropertyChanged veniva agganciata nel costruttore e staccata solo su
    // Detached: con la View creata una volta sola per pagina (Activator.CreateInstance ad ogni visita) andava
    // bene, ma con il ViewLocator che ora RIUSA la stessa istanza di View per la stessa istanza di ViewModel,
    // il costruttore non viene più richiamato alla rivisita — dopo la prima uscita dalla pagina Audit la
    // sottoscrizione allo statico LocalizationService non si riaggancia mai più (cambio lingua silenzioso).
    // Stesso discorso per vm.MatriceAggiornata: era ricollegata solo su DataContextChanged, evento che con la
    // View riusata (stesso ViewModel di pagina) non si ripete ad ogni rivisita. Ora entrambe le sottoscrizioni
    // seguono il ciclo di vita visuale (Attached aggancia, Detached sgancia), oltre al DataContextChanged per
    // il caso raro di cambio di ViewModel a View invariata.
    private AuditViewModel? _vmSottoscritto;
    private Action? _handlerMatriceAggiornata;

    public AuditView()
    {
        InitializeComponent();
        MatriceScroll.ScrollChanged += (_, _) => AllineaIntestazione();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        // Le celle sono generate in code-behind: i DynamicResource XAML non
        // aggiornano automaticamente i colori dei Border appena costruiti.
        ActualThemeVariantChanged += (_, _) =>
        {
            if (DataContext is AuditViewModel vm) RicostruisciGriglia(vm);
        };
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.PropertyChanged += OnLinguaCambiata;
        if (DataContext is AuditViewModel vm && !ReferenceEquals(_vmSottoscritto, vm))
            CollegaViewModel(vm);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LocalizationService.Instance.PropertyChanged -= OnLinguaCambiata;
        ScollegaViewModel();
    }

    private void OnLinguaCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LocalizationService.Lingua) or nameof(LocalizationService.EInglese)
            && DataContext is AuditViewModel vm)
        {
            vm.AggiornaEtichetteFiltroStati();
            RicostruisciGriglia(vm);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        ScollegaViewModel();
        if (DataContext is AuditViewModel vm) CollegaViewModel(vm);
    }

    private void CollegaViewModel(AuditViewModel vm)
    {
        _vmSottoscritto = vm;
        _handlerMatriceAggiornata = () => RicostruisciGriglia(vm);
        vm.MatriceAggiornata += _handlerMatriceAggiornata;
        RicostruisciGriglia(vm);
    }

    private void ScollegaViewModel()
    {
        if (_vmSottoscritto != null && _handlerMatriceAggiornata != null)
            _vmSottoscritto.MatriceAggiornata -= _handlerMatriceAggiornata;
        _vmSottoscritto = null;
        _handlerMatriceAggiornata = null;
    }

    /// <summary>Voce di sezione della matrice (Standard o Extra): titolo con il conteggio.</summary>
    private sealed record VoceSezioneMatrice(string Testo, string AutomationId);

    /// <summary>Riepilogo guasto sotto il titolo della sezione Extra.</summary>
    private sealed record VoceRiepilogoD4d(string Testo);

    /// <summary>Altezza fissa di una riga della matrice: con altezza nota la virtualizzazione calcola le posizioni senza misurare
    /// ogni riga (la matrice ha centinaia di righe e decine di colonne).</summary>
    private const double AltezzaRigaMatrice = 26;

    private static TextBlock TitoloSezione(string testo, string automationId) =>
        new TextBlock { Text = testo, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 4) }.Con(automationId);

    /// <summary>6.5.0: due sezioni SEPARATE, Standard e Extra, con il proprio titolo (e il proprio riepilogo). 6.6.1: nello stesso
    /// scorrimento, una sola ItemsControl virtualizzata per righe sotto un'intestazione componente fissa. Il filtro per tipo
    /// (vm.FiltroTipoRequisito) decide quali sezioni mostrare.</summary>
    private void RicostruisciGriglia(AuditViewModel vm)
    {
        CaricaPreferenzeMatrice();
        MatriceIntestazione.Children.Clear();
        MatriceRighe.ItemsSource = null;
        _colonneVisualizzate = Array.Empty<string>();
        if (vm.MatriceVuota) return;

        // Come il filtro stati Report: nasconde simultaneamente le righe prive degli stati
        // selezionati e le colonne componente che non hanno alcuna corrispondenza. Extra N.A.
        // scompare anche dall'ingombro, non solo dal testo delle celle.
        var righeStandard = vm.MostraSezioneStandard
            ? vm.Righe.Where(r => !vm.HaFiltroStatiMatrice || r.Celle.Values.Any(vm.CellaVisibilePerStato)).ToList()
            : new List<RigaMatriceAudit>();
        var righeD4d = vm.MostraSezioneD4d
            ? vm.RigheD4d.Where(r => !vm.HaFiltroStatiMatrice || r.Celle.Values.Any(vm.CellaVisibilePerStato)).ToList()
            : new List<RigaMatriceAudit>();
        var righeVisibili = righeStandard.Concat(righeD4d).ToList();
        _colonneVisualizzate = OrdineEcuComplete(vm)
            .Where(componente => !vm.HaFiltroStatiMatrice ||
                righeVisibili.Any(r => r.Celle.TryGetValue(componente, out var c) && vm.CellaVisibilePerStato(c)))
            .ToList();

        var angolo = IntestazioneCella(LocalizationService.Instance.Traduci("AuditRequisito"));
        angolo.Tag = TagColonnaRequisito;
        angolo.Width = LarghezzaRequisitoCorrente;
        ConfiguraIntestazione(angolo, TagColonnaRequisito, null);
        MatriceIntestazione.Children.Add(angolo);
        foreach (var componente in OrdineEcu(vm))
        {
            var intestazione = IntestazioneEcuCella(vm, componente);
            intestazione.Tag = TagPerColonnaEcu(componente);
            intestazione.Width = LarghezzaEcuCorrente(componente);
            ConfiguraIntestazione(intestazione, TagPerColonnaEcu(componente), componente);
            ToolTip.SetTip(intestazione, componente);
            MatriceIntestazione.Children.Add(intestazione);
        }

        var voci = new List<object>();
        if (righeStandard.Count > 0)
        {
            voci.Add(new VoceSezioneMatrice(vm.HaFiltroStatiMatrice
                ? $"Standard · {righeStandard.Count}/{vm.Righe.Count}" : vm.TitoloSezioneStandard, "Audit_Sezione_Standard_Text"));
            voci.AddRange(righeStandard);
        }
        if (righeD4d.Count > 0)
        {
            voci.Add(new VoceSezioneMatrice(vm.HaFiltroStatiMatrice
                ? $"Extra · {righeD4d.Count}/{vm.RigheD4d.Count}" : vm.TitoloSezioneD4d, "Audit_Sezione_Extra_Text"));
            if (!string.IsNullOrEmpty(vm.RiepilogoD4d)) voci.Add(new VoceRiepilogoD4d(vm.RiepilogoD4d));
            voci.AddRange(righeD4d);
        }
        if (voci.Count == 0 && vm.HaFiltroStatiMatrice)
            voci.Add(new VoceRiepilogoD4d(LocalizationService.Instance.Scegli(
                "Nessuna corrispondenza per gli stati selezionati. Azzera il filtro per mostrare tutta la matrice.",
                "No cells match the selected states. Clear the filter to show the full matrix.",
                "Aucune cellule ne correspond aux états sélectionnés. Effacez le filtre pour afficher toute la matrice.")));
        // Il template costruisce la voce solo quando la virtualizzazione la porta a schermo: il costo non dipende dal numero di righe.
        MatriceRighe.ItemTemplate = new FuncDataTemplate<object>((voce, _) => CostruisciVoce(vm, voce));
        MatriceRighe.ItemsSource = voci;
    }

    private void OnApriFiltroStatiMatrice(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is AuditViewModel vm)
            FiltroMultiSelectFlyoutHelper.Apri(button, vm.FiltroStatiMatrice);
    }

    private void OnAzzeraFiltroStatiMatrice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AuditViewModel vm) vm.AzzeraFiltroStatiMatrice();
    }

    /// <summary>L'intestazione componente scorre in orizzontale insieme al corpo della matrice (che e' l'unico ScrollViewer della pagina).</summary>
    private void AllineaIntestazione() =>
        MatriceIntestazione.RenderTransform = new TranslateTransform(-MatriceScroll.Offset.X, 0);

    private Control CostruisciVoce(AuditViewModel vm, object? voce) => voce switch
    {
        VoceSezioneMatrice sezione => TitoloSezione(sezione.Testo, sezione.AutomationId),
        VoceRiepilogoD4d riepilogo => new TextBlock { Text = riepilogo.Testo, Classes = { "Muted" }, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) }.Con("Audit_RiepilogoD4d_Text"),
        RigaMatriceAudit riga => CostruisciRiga(vm, riga),
        _ => new Border(),
    };

    /// <summary>Una riga: cella del requisito e una cella per componente, con larghezze e altezza fisse uguali all'intestazione.</summary>
    private Control CostruisciRiga(AuditViewModel vm, RigaMatriceAudit riga)
    {
        var altezza = AltezzaRigaCorrente(riga.RequisitoId);
        var pannello = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = altezza,
            Tag = TagContenutoRiga,
        }.Con("Audit_Matrice_RigaContenuto_" + Uri.EscapeDataString(riga.RequisitoId));
        pannello.Children.Add(CostruisciCellaRequisito(vm, riga, altezza));
        foreach (var ecuColonna in OrdineEcu(vm))
            pannello.Children.Add(vm.HaFiltroStatiMatrice &&
                (!riga.Celle.TryGetValue(ecuColonna, out var c) || !vm.CellaVisibilePerStato(c))
                ? new Border { Width = LarghezzaEcuCorrente(ecuColonna), Height = altezza,
                    BorderBrush = new SolidColorBrush(Color.Parse("#475569")), BorderThickness = new Thickness(0, 0, 1, 1) }
                : CostruisciCellaEcu(vm, riga, ecuColonna, altezza));

        var contenitore = new Grid { Height = altezza, Tag = TagRiga + riga.RequisitoId };
        AutomationProperties.SetAutomationId(contenitore, IdRiga(riga.RequisitoId));
        contenitore.Children.Add(pannello);
        ConfiguraRigaRidimensionabile(contenitore, riga.RequisitoId);
        return contenitore;
    }

    private void ConfiguraRigaRidimensionabile(Grid riga, string requisitoId)
    {
        riga.Focusable = true;
        AutomationProperties.SetName(riga, string.Format(LocalizationService.Instance.Traduci("AuditResizeRigaNomeFmt"), requisitoId));
        ToolTip.SetTip(riga, LocalizationService.Instance.Traduci("AuditResizeRigaTip"));
        riga.PointerPressed += OnSuperficieMatricePointerPressed;
        riga.PointerMoved += OnSuperficieMatricePointerMoved;
        riga.PointerReleased += OnSuperficieMatricePointerReleased;
        riga.PointerCaptureLost += OnSuperficieMatricePointerCaptureLost;
        riga.KeyDown += OnSuperficieMatriceKeyDown;
    }

    private Control CostruisciCellaRequisito(AuditViewModel vm, RigaMatriceAudit riga, double altezza)
    {
        Control testoRequisito;
        if (!string.IsNullOrEmpty(riga.DtcTesto))
        {
            // Requisito Extra con guasto: drill-down al dettaglio guasto (stato, passo mancante, ultimo test).
            var requisitoId = riga.RequisitoId;
            var apri = new Button { Classes = { "Link" }, Content = $"{riga.RequisitoId} · {riga.DtcTesto}", FontSize = 11 };
            apri.Click += async (_, _) => await vm.CaricaDettaglioDtcAsync(requisitoId);
            testoRequisito = apri.Con($"Audit_Extra_Req_{riga.RequisitoId}");
        }
        else
        {
            testoRequisito = new TextBlock
            {
                Text = riga.RequisitoId, FontWeight = FontWeight.SemiBold, FontSize = 11,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        // Testo del requisito nel tooltip, non solo l'Id (feedback utente 2026-08-26): altrimenti capire
        // di cosa tratti un requisito richiede di andare a cercarlo a parte in Impostazioni.
        ToolTip.SetTip(testoRequisito, string.IsNullOrWhiteSpace(riga.TestoRequisito) ? riga.RequisitoId : $"{riga.RequisitoId} — {riga.TestoRequisito}");
        var contenuto = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 2 };
        contenuto.Children.Add(testoRequisito);
        if (vm.EMaster)
        {
            var applicaRiga = new Button
            {
                Content = "✎", Classes = { "Link" }, Padding = new Thickness(2, 0),
                FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(applicaRiga, LocalizationService.Instance.Scegli("Applica un valore manuale alla riga", "Apply a manual value to this row", "Appliquer une valeur manuelle à la ligne"));
            AutomationProperties.SetAutomationId(applicaRiga, $"Audit_ManualeRiga_Button_{riga.RequisitoId}");
            applicaRiga.Click += (_, _) => ApriApplicazioneManualeMassivaFlyout(applicaRiga, vm, null, riga.RequisitoId);
            Grid.SetColumn(applicaRiga, 1);
            contenuto.Children.Add(applicaRiga);

            if (riga.Celle.Values.Any(c => c.Stato == StatoCellaAudit.PropagabileNonConfermato))
            {
                var propagaRiga = new Button
                {
                    Content = "⇄", Classes = { "Link" }, Padding = new Thickness(2, 0),
                    FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(propagaRiga, LocalizationService.Instance.Scegli(
                    "Propaga i valori confermabili di questo requisito a tutti i componenti del veicolo selezionato",
                    "Propagate the confirmable values for this requirement to every component on the selected vehicle",
                    "Propager les valeurs confirmables de cette exigence à tous les componente du véhicule sélectionné"));
                AutomationProperties.SetAutomationId(propagaRiga, $"Audit_PropagaRiga_Button_{riga.RequisitoId}");
                propagaRiga.Click += (_, _) => ApriPropagaMassivaFlyout(propagaRiga, vm, null, riga.RequisitoId);
                Grid.SetColumn(propagaRiga, 2);
                contenuto.Children.Add(propagaRiga);
            }

            if (riga.NumeroValutazioniRimuovibili > 0)
            {
                var rimuoviRiga = new Button
                {
                    Content = "×", Classes = { "Link" }, Padding = new Thickness(2, 0),
                    FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = PennelloErrore,
                };
                ToolTip.SetTip(rimuoviRiga, LocalizationService.Instance.Scegli(
                    $"Rimuovi {riga.NumeroValutazioniRimuovibili} valutazioni manuali o propagate da questo requisito sul veicolo selezionato",
                    $"Remove {riga.NumeroValutazioniRimuovibili} manual or propagated assessments for this requirement on the selected vehicle",
                    $"Supprimer {riga.NumeroValutazioniRimuovibili} évaluations manuelles ou propagées de cette exigence sur le véhicule sélectionné"));
                AutomationProperties.SetAutomationId(rimuoviRiga, $"Audit_RimuoviRiga_Button_{riga.RequisitoId}");
                rimuoviRiga.Click += (_, _) => ApriRimozioneValutazioniRigaFlyout(rimuoviRiga, vm, riga.RequisitoId);
                Grid.SetColumn(rimuoviRiga, 3);
                contenuto.Children.Add(rimuoviRiga);
            }
        }
        return new Border
        {
            Tag = TagColonnaRequisito,
            Width = LarghezzaRequisitoCorrente,
            Height = altezza,
            // Il testo del requisito eredita PrimaryTextBrush; non usare mai un fondino
            // chiaro fisso nel tema scuro (testo chiaro su bianco diventava invisibile).
            Background = PennelloPer(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#141B23" : "#F8FAFC"),
            BorderBrush = PennelloPer(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#576B7C" : "#E2E8F0"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(6, 2),
            Child = contenuto,
        };
    }

    private Control CostruisciCellaEcu(AuditViewModel vm, RigaMatriceAudit riga, string ecuColonna, double altezza)
    {
        var cella = riga.Celle.GetValueOrDefault(ecuColonna);
        Control cellaVisuale;
        if (cella is { InConflitto: true } && vm.EMaster)
            cellaVisuale = CellaConflittoRisolvibile(vm, cella);
        // "!" (Piano 6.3.1 §1 UI, punto 2): cliccabile solo per un Master, stessa regola di permesso
        // delle altre azioni di scrittura della matrice — un Viewer vede comunque lo stato e il
        // tooltip, ma non può decidere la propagazione.
        else if (cella is { Stato: StatoCellaAudit.PropagabileNonConfermato } && vm.EMaster)
            cellaVisuale = CellaPropagabileCliccabile(vm, cella, vm.SottoveicoloSelezionato!);
        // Il punteggio Manuale legacy è condiviso da tutti i veicoli e non passa dal flyout per le nuove
        // valutazioni veicolo-specifiche. Lo rendiamo cliccabile allo stesso modo, così la sua azione
        // esistente di rimozione non richiede di scoprire il tasto destro.
        else if (cella is { Stato: StatoCellaAudit.Valutato, InserimentoManuale: true } && vm.EMaster)
            cellaVisuale = CellaValutazioneLegacyCliccabile(vm, cella);
        // Cella con valore Manuale/Propagato veicolo-specifico (Piano 6.3.1 §1 UI, punto 4): cliccabile
        // per un Master per rivedere/rimuovere la valutazione salvata, marcatura visiva già distinta
        // via CellaMatriceAuditVisual.Testo ("✎"/"↳").
        else if (cella is { Stato: StatoCellaAudit.Valutato, InserimentoManuale: false, Origine: OrigineCellaRequisitoEcuVeicolo.Manuale or OrigineCellaRequisitoEcuVeicolo.Propagata } && vm.EMaster)
            cellaVisuale = CellaValutazioneVeicoloCliccabile(vm, cella, vm.SottoveicoloSelezionato!);
        else
        {
            var testoCella = new TextBlock
            {
                Text = cella?.Testo ?? "",
                Foreground = new SolidColorBrush(Color.Parse("#1E293B")),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // UIA espone il TextBlock figlio, non il Border decorativo: l'ID deve stare sul nodo accessibile
            // perché i test e le tecnologie assistive possano individuare la cella componente×requisito.
            AutomationProperties.SetAutomationId(testoCella, $"Audit_Cella_{ecuColonna}_{riga.RequisitoId}_Element");
            cellaVisuale = new Border
            {
                Background = cella != null ? PennelloPer(cella.Colore) : Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Child = testoCella,
            };
        }
        if (cella != null)
        {
            var tooltip = cella.ToolTip;
            if (vm.EMaster && (cella.InserimentoManuale ||
                cella.Origine is OrigineCellaRequisitoEcuVeicolo.Manuale or OrigineCellaRequisitoEcuVeicolo.Propagata))
            {
                var suggerimento = LocalizationService.Instance.Scegli(
                    "Clicca per vedere i dettagli o rimuovere questa valutazione manuale/propagata.",
                    "Click to view details or remove this manual/propagated assessment.",
                    "Cliquez pour afficher les détails ou supprimer cette évaluation manuelle/propagée.");
                tooltip = string.IsNullOrWhiteSpace(tooltip) ? suggerimento : $"{tooltip}\n{suggerimento}";
            }
            ToolTip.SetTip(cellaVisuale, tooltip);
        }
        cellaVisuale.Tag = TagCellaEcu + ecuColonna;
        cellaVisuale.Width = LarghezzaEcuCorrente(ecuColonna);
        cellaVisuale.Height = altezza;
        // Tasto destro su QUALSIASI cella, non solo quelle in conflitto (feedback utente 2026-08-26,
        // punto 2): un Master deve poter inserire/correggere a mano un valore anche su una cella
        // ancora da testare o già valutata dai Test — vedi AuditViewModel.SalvaValutazioneManuale.
        if (vm.EMaster && cella is not { NonApplicabileEcu: true })
        {
            var requisitoId = riga.RequisitoId;
            cellaVisuale.ContextRequested += (_, _) => ApriInserimentoManualeFlyout(cellaVisuale, vm, ecuColonna, requisitoId, cella);
        }
        return cellaVisuale;
    }

    /// <summary>Cella in conflitto cliccabile solo per un Master (Addendum: grill-me 2026-08-18, seconda
    /// follow-up — "un Master può marcare quale Test è quello valido"): apre un flyout con un Test in
    /// conflitto per riga (radio) e una motivazione obbligatoria, poi richiama
    /// AuditViewModel.RisolviConflitto e lascia che l'evento MatriceAggiornata ricostruisca la griglia.</summary>
    private Button CellaConflittoRisolvibile(AuditViewModel vm, CellaMatriceAuditVisual cella)
    {
        var bottone = new Button
        {
            Background = PennelloPer(cella.Colore),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Content = new TextBlock { Text = cella.Testo, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#1E293B")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        bottone.Click += (_, _) => ApriRisolviConflittoFlyout(bottone, vm, cella);
        return bottone;
    }

    /// <summary>Cella "!" cliccabile (Piano 6.3.1 §1 UI, punto 2): apre il dialogo "Propagare?" con la
    /// sorgente proposta e l'elenco dei veicoli propagabili — vedi ApriPropagaFlyout.</summary>
    private Button CellaPropagabileCliccabile(AuditViewModel vm, CellaMatriceAuditVisual cella, string veicoloCliccato)
    {
        var bottone = new Button
        {
            Background = PennelloPer(cella.Colore),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Content = new TextBlock { Text = cella.Testo, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#1E293B")), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetAutomationId(bottone, $"Audit_Propagabile_{cella.Componente}_{cella.RequisitoId}_Button");
        bottone.Click += (_, _) => ApriPropagaFlyout(bottone, vm, cella, veicoloCliccato);
        return bottone;
    }

    /// <summary>Cella con valore Manuale/Propagato veicolo-specifico, cliccabile per rivedere/rimuovere la
    /// valutazione salvata (Piano 6.3.1 §1 UI, punto 4).</summary>
    private Button CellaValutazioneVeicoloCliccabile(AuditViewModel vm, CellaMatriceAuditVisual cella, string veicolo)
    {
        var bottone = new Button
        {
            Background = PennelloPer(cella.Colore),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Content = new TextBlock { Text = cella.Testo, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#1E293B")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetAutomationId(bottone, $"Audit_ValutazioneSalvata_{cella.Componente}_{cella.RequisitoId}_Button");
        bottone.Click += (_, _) => ApriValutazioneSalvataFlyout(bottone, vm, cella, veicolo);
        return bottone;
    }

    private Button CellaValutazioneLegacyCliccabile(AuditViewModel vm, CellaMatriceAuditVisual cella)
    {
        var bottone = new Button
        {
            Background = PennelloPer(cella.Colore),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Content = new TextBlock { Text = cella.Testo, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#1E293B")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetAutomationId(bottone, $"Audit_ManualeLegacy_{cella.Componente}_{cella.RequisitoId}_Button");
        bottone.Click += (_, _) => ApriInserimentoManualeFlyout(bottone, vm, cella.Componente, cella.RequisitoId, cella);
        return bottone;
    }

    /// <summary>Dialogo "Propagare?" (Piano 6.3.1 §1 UI, punto 2): mostra valore e sorgente proposti (il
    /// candidato migliore, o una scelta tra più sorgenti se il componente ha più di un veicolo con un valore
    /// diverso), l'elenco dei veicoli propagabili con checkbox (Seleziona tutti/nessuno; il veicolo cliccato
    /// preselezionato) e una nota obbligatoria. Conferma → AuditViewModel.Propaga.</summary>
    private static void ApriPropagaFlyout(Control anchor, AuditViewModel vm, CellaMatriceAuditVisual cella, string veicoloCliccato)
    {
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        panel.Children.Add(new TextBlock { Text = string.Format(T("AuditPropagaTitoloFmt"), cella.Componente, cella.RequisitoId, veicoloCliccato), FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        // Più di una sorgente possibile (più veicoli della stessa componente con un valore diverso): l'operatore
        // sceglie quale usare come sorgente proposta, altrimenti l'unica disponibile è preselezionata.
        panel.Children.Add(new TextBlock { Text = T("AuditPropagaSorgenteLabel"), Classes = { "Muted" }, FontSize = 11 });
        var sorgenti = cella.CandidatiPropagazione.Values.OrderByDescending(c => c.DataSorgente).ToList();
        var scelteSorgente = new List<RadioButton>();
        foreach (var sorgente in sorgenti)
        {
            var punteggioTesto = sorgente.PunteggioSorgente == PunteggioConformita.NonApplicabile ? "N/A" : ((int)sorgente.PunteggioSorgente).ToString();
            var radio = new RadioButton
            {
                GroupName = "PropagaSorgente",
                Content = string.Format(T("AuditPropagaCandidatoFmt"), sorgente.VeicoloSorgente, punteggioTesto, sorgente.DataSorgente),
                Tag = sorgente,
                IsChecked = sorgenti.Count == 1,
            };
            scelteSorgente.Add(radio);
            panel.Children.Add(radio);
        }

        panel.Children.Add(new TextBlock { Text = T("AuditPropagaVeicoliLabel"), Classes = { "Muted" }, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        var veicoliPanel = new StackPanel { Spacing = 2 };
        panel.Children.Add(veicoliPanel);

        var selezionaTutti = new Button { Content = T("AuditSelezionaTutti"), Classes = { "Link" }, Padding = new Thickness(0), FontSize = 11 };
        var selezionaNessuno = new Button { Content = T("AuditSelezionaNessuno"), Classes = { "Link" }, Padding = new Thickness(0), FontSize = 11, Margin = new Thickness(12, 0, 0, 0) };
        AutomationProperties.SetAutomationId(selezionaTutti, "Audit_Propaga_SelezionaTutti_Button");
        AutomationProperties.SetAutomationId(selezionaNessuno, "Audit_Propaga_SelezionaNessuno_Button");
        var selezioneBottoni = new StackPanel { Orientation = Orientation.Horizontal };
        selezioneBottoni.Children.Add(selezionaTutti);
        selezioneBottoni.Children.Add(selezionaNessuno);
        panel.Children.Add(selezioneBottoni);

        var motivazioneBox = new TextBox { PlaceholderText = T("AuditPropagaNotaPlaceholder"), AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(motivazioneBox, "Audit_Propaga_Nota_TextBox");
        panel.Children.Add(motivazioneBox);

        var erroreText = new TextBlock { Foreground = PennelloErrore, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(erroreText);

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("AnnullaSemplice"), Classes = { "Secondary" } };
        var conferma = new Button { Content = T("ConfermaSemplice"), Classes = { "Primary" } };
        AutomationProperties.SetAutomationId(annulla, "Audit_Propaga_Annulla_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_Propaga_Conferma_Button");
        bottoni.Children.Add(annulla); bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };

        // I checkbox dei veicoli propagabili dipendono dalla sorgente scelta (ricalcolati ad ogni cambio
        // radio): un altro veicolo va incluso solo se propagherebbe la STESSA sorgente/valore.
        var checkboxCorrenti = new List<CheckBox>();
        void RicostruisciCheckboxVeicoli()
        {
            veicoliPanel.Children.Clear();
            checkboxCorrenti.Clear();
            var sorgenteScelta = scelteSorgente.FirstOrDefault(r => r.IsChecked == true)?.Tag as CandidatoPropagazioneRequisito;
            if (sorgenteScelta == null) return;

            var propagabili = vm.VeicoliPropagabiliPer(cella.Componente, cella.RequisitoId, sorgenteScelta);
            foreach (var voce in propagabili)
            {
                var check = new CheckBox { Content = voce.Veicolo, Tag = voce, IsChecked = voce.Veicolo.Equals(veicoloCliccato, StringComparison.OrdinalIgnoreCase) };
                AutomationProperties.SetAutomationId(check, $"Audit_Propaga_Veicolo_{voce.Veicolo}_CheckBox");
                checkboxCorrenti.Add(check);
                veicoliPanel.Children.Add(check);
            }
        }
        foreach (var radio in scelteSorgente) radio.IsCheckedChanged += (_, _) => RicostruisciCheckboxVeicoli();
        RicostruisciCheckboxVeicoli();

        selezionaTutti.Click += (_, _) => { foreach (var c in checkboxCorrenti) c.IsChecked = true; };
        selezionaNessuno.Click += (_, _) => { foreach (var c in checkboxCorrenti) c.IsChecked = false; };

        annulla.Click += (_, _) => flyout.Hide();
        conferma.Click += (_, _) =>
        {
            var motivazione = motivazioneBox.Text?.Trim() ?? "";
            var scelti = checkboxCorrenti.Where(c => c.IsChecked == true).Select(c => (VeicoloPropagabileVoce)c.Tag!).ToList();
            if (scelti.Count == 0) { erroreText.Text = T("AuditPropagaNessunVeicoloErrore"); erroreText.IsVisible = true; return; }
            if (motivazione.Length == 0) { erroreText.Text = T("AuditMotivoObbligatorio"); erroreText.IsVisible = true; return; }

            vm.Propaga(cella.Componente, cella.RequisitoId, scelti, motivazione);
            flyout.Hide();
        };

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Dialogo sulla cella con valore Manuale/Propagato veicolo-specifico già salvato (Piano 6.3.1
    /// §1 UI, punto 4): mostra il tooltip con l'origine completa e permette di rimuovere la valutazione (la
    /// cella torna al calcolo automatico). "Modificare" un valore passa da qui: Rimuovi, poi re-inserirlo con
    /// il dialogo di propagazione/inserimento manuale. Da qui è disponibile anche lo storico per cella.</summary>
    private static void ApriValutazioneSalvataFlyout(Control anchor, AuditViewModel vm, CellaMatriceAuditVisual cella, string veicolo)
    {
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        var panel = new StackPanel { Spacing = 8, MinWidth = 280 };
        var badge = cella.Origine == OrigineCellaRequisitoEcuVeicolo.Propagata ? T("AuditOriginePropagataBadge") : T("AuditOrigineManualeBadge");
        panel.Children.Add(new TextBlock { Text = $"{badge} — {cella.Componente} × {cella.RequisitoId} × {veicolo}", FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = cella.ToolTip, Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });

        var storico = new Button
        {
            Content = LocalizationService.Instance.Scegli("Cronologia valori", "Value history", "Historique des valeurs"),
            Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(storico, "Audit_ValutazioneSalvata_Storico_Button");
        panel.Children.Add(storico);

        var rimuovi = new Button { Content = T("AuditRimuoviValutazioneVeicolo"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(rimuovi, "Audit_ValutazioneSalvata_Rimuovi_Button");
        panel.Children.Add(rimuovi);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        rimuovi.Click += (_, _) => { vm.RimuoviValutazioneVeicolo(cella.Componente, cella.RequisitoId, veicolo); flyout.Hide(); };
        storico.Click += (_, _) =>
        {
            flyout.Hide();
            ApriStoricoValutazioneFlyout(anchor, vm, cella.Componente, cella.RequisitoId, veicolo, cella);
        };

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    private static void ApriStoricoValutazioneFlyout(
        Control anchor,
        AuditViewModel vm,
        string componente,
        string requisitoId,
        string veicolo,
        CellaMatriceAuditVisual? cellaCorrente)
    {
        var t = LocalizationService.Instance;
        string S(string italiano, string inglese, string francese) => t.Scegli(italiano, inglese, francese);
        var anteprimaRipristino = vm.PreparaApplicazioneManualeAudit(componente, requisitoId);
        var panel = new StackPanel { Spacing = 8, MinWidth = 360, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(S("Storico Audit — {0} × {1} × {2}", "Audit history — {0} × {1} × {2}", "Historique Audit — {0} × {1} × {2}"), componente, requisitoId, veicolo),
            FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = S("Le REV senza snapshot componente/veicolo non sono attribuibili a questa cella. Il ripristino crea una nuova valutazione manuale per il veicolo selezionato.",
                "REV entries without a component/vehicle snapshot cannot be assigned to this cell. Restore creates a new manual assessment for the selected vehicle.",
                "Les REV sans instantané componente/véhicule ne peuvent pas être attribuées à cette cellule. Le rétablissement crée une nouvelle évaluation manuelle pour le véhicule sélectionné."),
            Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });

        var eventi = vm.LeggiStoricoValutazioneCella(componente, requisitoId, veicolo);
        var scelte = new List<RadioButton>();
        var elenco = new StackPanel { Spacing = 3 };
        var gruppoScelte = $"AuditStorico_{Guid.NewGuid():N}";
        foreach (var evento in eventi)
        {
            var origine = evento.Origine switch
            {
                OrigineStoricoValutazioneAudit.Test => "Test",
                OrigineStoricoValutazioneAudit.Manuale => S("Manuale", "Manual", "Manuel"),
                OrigineStoricoValutazioneAudit.Propagata => S("Propagata", "Propagated", "Propagée"),
                _ => "Legacy",
            };
            var punteggio = evento.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)evento.Punteggio).ToString();
            var meta = $"{origine} · {evento.Data.ToLocalTime():g} · {punteggio}";
            if (!string.IsNullOrWhiteSpace(evento.Operatore)) meta += $" · {evento.Operatore}";
            if (!string.IsNullOrWhiteSpace(evento.TestCaseId))
                meta += evento.IterazioneTest.HasValue ? $" · {evento.TestCaseId} REV {evento.IterazioneTest}" : $" · {evento.TestCaseId}";
            if (!string.IsNullOrWhiteSpace(evento.VeicoloSorgente)) meta += $" · {evento.VeicoloSorgente}";
            if (!string.IsNullOrWhiteSpace(evento.BaselineSw)) meta += $" · SW {evento.BaselineSw}";
            if (!string.IsNullOrWhiteSpace(evento.Nota)) meta += $"\n{evento.Nota}";
            var scelta = new RadioButton
            {
                GroupName = gruppoScelte,
                Content = new TextBlock { Text = meta, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                Tag = evento,
            };
            scelte.Add(scelta);
            elenco.Children.Add(scelta);
        }
        var scroll = new ScrollViewer { Content = elenco, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(scroll);
        if (eventi.Count == 0)
            panel.Children.Add(new TextBlock
            {
                Text = S("Non ci sono valori storici recuperabili per questa cella.", "No recoverable historical values are available for this cell.", "Aucune valeur historique récupérable n'est disponible pour cette cellule."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });

        var nota = new TextBox
        {
            PlaceholderText = S("Nota per il nuovo inserimento manuale (obbligatoria)", "Note for the new manual entry (required)", "Note pour la nouvelle saisie manuelle (obligatoire)"),
            AcceptsReturn = true, Height = 58, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(nota, "Audit_Storico_Nota_TextBox");
        panel.Children.Add(nota);
        var avviso = new TextBlock { Foreground = PennelloAvviso, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(avviso);
        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = t.Traduci("AnnullaSemplice"), Classes = { "Secondary" } };
        var ripristina = new Button
        {
            Content = S("Usa come nuovo valore", "Use as new value", "Utiliser comme nouvelle valeur"),
            Classes = { "Primary" }, IsEnabled = eventi.Count > 0,
        };
        var conferma = new Button
        {
            Content = S("Conferma sovrascrittura", "Confirm replacement", "Confirmer le remplacement"),
            Classes = { "Primary" }, IsVisible = false,
        };
        AutomationProperties.SetAutomationId(ripristina, "Audit_Storico_Ripristina_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_Storico_ConfermaSovrascrittura_Button");
        bottoni.Children.Add(annulla);
        bottoni.Children.Add(ripristina);
        bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        var sovrascrive = cellaCorrente is { Stato: StatoCellaAudit.Valutato or StatoCellaAudit.Conflitto };
        bool Ripristina(bool confermato)
        {
            var scelto = scelte.FirstOrDefault(r => r.IsChecked == true)?.Tag as SnapshotValutazioneAudit;
            if (scelto == null)
            {
                avviso.Text = S("Seleziona un valore storico.", "Select a historical value.", "Sélectionnez une valeur historique.");
                avviso.IsVisible = true;
                return false;
            }
            if (string.IsNullOrWhiteSpace(nota.Text))
            {
                avviso.Text = S("Inserisci una nota.", "Enter a note.", "Saisissez une note.");
                avviso.IsVisible = true;
                return false;
            }
            var ok = vm.RipristinaValutazioneStorica(componente, requisitoId, veicolo, scelto.EventId,
                nota.Text.Trim(), sovrascrive, confermato, anteprimaRipristino);
            if (ok) { flyout.Hide(); return true; }
            if (!string.IsNullOrWhiteSpace(vm.MessaggioErrore))
            {
                avviso.Text = vm.MessaggioErrore;
                avviso.IsVisible = true;
            }
            return false;
        }
        ripristina.Click += (_, _) =>
        {
            if (sovrascrive)
            {
                avviso.Text = S("La cella contiene già un valore. Conferma per sostituirlo con una nuova valutazione manuale.",
                    "The cell already has a value. Confirm to replace it with a new manual assessment.",
                    "La cellule contient déjà une valeur. Confirmez pour la remplacer par une nouvelle évaluation manuelle.");
                avviso.IsVisible = true;
                ripristina.IsVisible = false;
                conferma.IsVisible = true;
                return;
            }
            Ripristina(confermato: false);
        };
        conferma.Click += (_, _) => Ripristina(confermato: true);
        annulla.Click += (_, _) => flyout.Hide();
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    private static void ApriRisolviConflittoFlyout(Control anchor, AuditViewModel vm, CellaMatriceAuditVisual cella)
    {
        if (cella.Candidati.Count == 0)
        {
            // Pareggio fra fonti temporali diverse: il vecchio selettore Test non è applicabile.
            // Un nuovo Manuale per il veicolo selezionato produce il timestamp che chiude il pareggio.
            ApriValutazioneVeicoloFlyout(anchor, vm, cella.Componente, cella.RequisitoId, vm.SottoveicoloSelezionato!, cella);
            return;
        }
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        var panel = new StackPanel { Spacing = 8, MinWidth = 280 };
        panel.Children.Add(new TextBlock { Text = string.Format(T("AuditConflittoSuFmt"), cella.Componente, cella.RequisitoId), FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = T("AuditQualeTestValido"), Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });

        var scelte = new List<RadioButton>();
        foreach (var candidato in cella.Candidati)
        {
            var punteggioTesto = candidato.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)candidato.Punteggio).ToString();
            var radio = new RadioButton { GroupName = "RisoluzioneConflitto", Content = string.Format(T("AuditPunteggioCandidatoFmt"), candidato.TestCaseId, punteggioTesto, candidato.Data), Tag = candidato.TestCaseId };
            scelte.Add(radio);
            panel.Children.Add(radio);
        }

        var motivazioneBox = new TextBox { PlaceholderText = T("AuditMotivoPlaceholder"), AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(motivazioneBox);

        var erroreText = new TextBlock { Foreground = PennelloErrore, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(erroreText);

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("AnnullaSemplice"), Classes = { "Secondary" } };
        var conferma = new Button { Content = T("ConfermaSemplice"), Classes = { "Primary" } };
        bottoni.Children.Add(annulla); bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        annulla.Click += (_, _) => flyout.Hide();
        conferma.Click += (_, _) =>
        {
            var scelto = scelte.FirstOrDefault(r => r.IsChecked == true);
            var motivazione = motivazioneBox.Text?.Trim() ?? "";
            if (scelto == null) { erroreText.Text = T("AuditScegliTestErrore"); erroreText.IsVisible = true; return; }
            if (motivazione.Length == 0) { erroreText.Text = T("AuditMotivoObbligatorio"); erroreText.IsVisible = true; return; }

            vm.RisolviConflitto(cella.Componente, cella.RequisitoId, (string)scelto.Tag!, motivazione);
            flyout.Hide();
        };

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    private sealed record VoceValutazione(PunteggioConformita Valore, string Etichetta)
    {
        public override string ToString() => Etichetta;
    }

    /// <summary>Inserimento/correzione manuale di una cella, aperto con tasto destro da un Master su
    /// QUALSIASI cella della matrice (feedback utente 2026-08-26, punto 2) — non solo quelle in conflitto:
    /// stesso schema di ApriRisolviConflittoFlyout (Flyout, motivazione obbligatoria, conferma/annulla), con
    /// una ComboBox al posto della scelta tra Test candidati e un link "Rimuovi" quando la cella ha già un
    /// inserimento manuale attivo. Vedi AuditViewModel.SalvaValutazioneManuale/RimuoviValutazioneManuale.</summary>
    private static void ApriInserimentoManualeFlyout(Control anchor, AuditViewModel vm, string componente, string requisitoId, CellaMatriceAuditVisual? cella)
    {
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        if (string.IsNullOrWhiteSpace(vm.SottoveicoloSelezionato)) return;
        if (cella is not { InserimentoManuale: true })
        {
            ApriValutazioneVeicoloFlyout(anchor, vm, componente, requisitoId, vm.SottoveicoloSelezionato, cella);
            return;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 280 };
        var storico = new Button
        {
            Content = LocalizationService.Instance.Scegli("Cronologia valori", "Value history", "Historique des valeurs"),
            Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(storico);
        panel.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance.Scegli(
                $"Valore manuale legacy per {componente} × {requisitoId}: resta visibile su tutti i veicoli finché una fonte più recente lo supera. Puoi inserire un nuovo valore solo per {vm.SottoveicoloSelezionato}; la rimozione legacy lo elimina invece globalmente.",
                $"Legacy manual value for {componente} × {requisitoId}: it remains visible on all vehicles until a newer source supersedes it. You can enter a new value only for {vm.SottoveicoloSelezionato}; removing the legacy value removes it globally.",
                $"Ancienne valeur manuelle pour {componente} × {requisitoId} : elle reste visible sur tous les véhicules jusqu'à ce qu'une source plus récente la remplace. Vous pouvez saisir une nouvelle valeur uniquement pour {vm.SottoveicoloSelezionato} ; la suppression de l'ancienne valeur la retire globalement."),
            Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        var soloVeicolo = new Button
        {
            Content = LocalizationService.Instance.Scegli(
                $"Inserisci valore solo per {vm.SottoveicoloSelezionato}",
                $"Enter a value for {vm.SottoveicoloSelezionato} only",
                $"Saisir une valeur uniquement pour {vm.SottoveicoloSelezionato}"),
            Classes = { "Primary" }, HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(soloVeicolo, "Audit_ManualeLegacy_SoloVeicolo_Button");
        panel.Children.Add(soloVeicolo);
        var rimuovi = new Button { Content = T("AuditRimuoviInserimentoManuale"), Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(rimuovi, "Audit_ManualeLegacy_Rimuovi_Button");
        panel.Children.Add(rimuovi);
        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("AnnullaSemplice"), Classes = { "Secondary" } };
        bottoni.Children.Add(annulla);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        storico.Click += (_, _) =>
        {
            flyout.Hide();
            ApriStoricoValutazioneFlyout(anchor, vm, componente, requisitoId, vm.SottoveicoloSelezionato, cella);
        };
        soloVeicolo.Click += (_, _) =>
        {
            flyout.Hide();
            ApriValutazioneVeicoloFlyout(anchor, vm, componente, requisitoId, vm.SottoveicoloSelezionato);
        };
        rimuovi.Click += (_, _) => { vm.RimuoviValutazioneManuale(componente, requisitoId); flyout.Hide(); };
        annulla.Click += (_, _) => flyout.Hide();
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Inserimento manuale con dimensione veicolo: salva solo sul sottoveicolo selezionato. La
    /// propagazione verso altri veicoli rimane un flusso distinto, esplicito e basato sulle celle "!".</summary>
    private static void ApriValutazioneVeicoloFlyout(Control anchor, AuditViewModel vm, string componente, string requisitoId, string veicoloPreselezionato,
        CellaMatriceAuditVisual? cella = null)
    {
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        var anteprimaManuale = vm.PreparaApplicazioneManualeAudit(componente, requisitoId);
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        var titolo = string.Format(T("AuditValutazioneVeicoloTitoloCoppiaFmt"), componente, requisitoId, veicoloPreselezionato);
        panel.Children.Add(new TextBlock { Text = titolo, FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var storico = new Button
        {
            Content = LocalizationService.Instance.Scegli("Cronologia valori", "Value history", "Historique des valeurs"),
            Classes = { "Secondary" }, HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(storico);

        if (cella is { Stato: StatoCellaAudit.Valutato })
            panel.Children.Add(new TextBlock
            {
                Text = LocalizationService.Instance.Scegli(
                    "Il punteggio visibile è quello con data più recente tra Test, valutazione manuale e propagata. Questo inserimento sarà datato ora e vale solo per il veicolo selezionato; un Test eseguito dopo potrà prevalere.",
                    "The visible score is the newest dated value among Tests, manual assessments, and propagated assessments. This entry is dated now and applies only to the selected vehicle; a later Test can take precedence.",
                    "Le score affiché est la valeur datée la plus récente parmi les Tests, évaluations manuelles et propagées. Cette saisie est datée maintenant et ne concerne que le véhicule sélectionné ; un Test ultérieur peut prévaloir."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });

        panel.Children.Add(new TextBlock { Text = T("AuditValutazioneVeicoloScegliValoreLabel"), Classes = { "Muted" }, FontSize = 11 });
        var voci = Enum.GetValues<PunteggioConformita>().Select(p => new VoceValutazione(p, p.Label())).ToList();
        var combo = new ComboBox { ItemsSource = voci, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(combo, "Audit_ValutazioneVeicolo_Punteggio_ComboBox");
        panel.Children.Add(combo);

        var baselineBox = new TextBox { PlaceholderText = T("AuditValutazioneVeicoloBaselineSwPlaceholder") };
        AutomationProperties.SetAutomationId(baselineBox, "Audit_ValutazioneVeicolo_BaselineSw_TextBox");
        panel.Children.Add(baselineBox);

        var motivazioneBox = new TextBox { PlaceholderText = T("AuditMotivoPlaceholder"), AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(motivazioneBox, "Audit_ValutazioneVeicolo_Nota_TextBox");
        panel.Children.Add(motivazioneBox);

        var erroreText = new TextBlock { Foreground = PennelloErrore, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(erroreText);

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("AnnullaSemplice"), Classes = { "Secondary" } };
        var conferma = new Button { Content = T("ConfermaSemplice"), Classes = { "Primary" } };
        AutomationProperties.SetAutomationId(annulla, "Audit_ValutazioneVeicolo_Annulla_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_ValutazioneVeicolo_Conferma_Button");
        bottoni.Children.Add(annulla); bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        storico.Click += (_, _) =>
        {
            flyout.Hide();
            ApriStoricoValutazioneFlyout(anchor, vm, componente, requisitoId, veicoloPreselezionato, cella);
        };
        annulla.Click += (_, _) => flyout.Hide();
        conferma.Click += (_, _) =>
        {
            var scelto = combo.SelectedItem as VoceValutazione;
            var motivazione = motivazioneBox.Text?.Trim() ?? "";
            if (scelto == null) { erroreText.Text = T("AuditScegliValoreErrore"); erroreText.IsVisible = true; return; }
            if (motivazione.Length == 0) { erroreText.Text = T("AuditMotivoObbligatorio"); erroreText.IsVisible = true; return; }

            var baselineSw = string.IsNullOrWhiteSpace(baselineBox.Text) ? null : baselineBox.Text.Trim();
            vm.SalvaValutazioneManualeVeicoloSelezionato(componente, requisitoId, scelto.Valore, motivazione, baselineSw, anteprimaManuale);
            flyout.Hide();
        };

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Applica un punteggio manuale alla riga requisito o alla colonna componente correntemente visibile.
    /// Le celle valorizzate, incluse quelle da Test e dal legacy globale, mostrano un secondo passaggio di
    /// conferma. Il nuovo Manuale viene scritto solo sul veicolo selezionato; il valore visibile resta quello
    /// con timestamp più recente.</summary>
    private static void ApriApplicazioneManualeMassivaFlyout(Control anchor, AuditViewModel vm, string? ecuNome, string? requisitoId)
    {
        string S(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        var anteprima = vm.PreparaApplicazioneManualeAudit(ecuNome, requisitoId);
        var panel = new StackPanel { Spacing = 8, MinWidth = 320, MaxWidth = 480 };
        var titolo = requisitoId != null
            ? S($"Valuta la riga {requisitoId} su {vm.SottoveicoloSelezionato}", $"Assess row {requisitoId} on {vm.SottoveicoloSelezionato}", $"Évaluer la ligne {requisitoId} sur {vm.SottoveicoloSelezionato}")
            : S($"Valuta la colonna {ecuNome} su {vm.SottoveicoloSelezionato}", $"Assess column {ecuNome} on {vm.SottoveicoloSelezionato}", $"Évaluer la colonne {ecuNome} sur {vm.SottoveicoloSelezionato}");
        panel.Children.Add(new TextBlock { Text = titolo, FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var riepilogo = new TextBlock
        {
            Text = S(
                $"{anteprima.CelleDaCompilare} celle senza punteggio da compilare · {anteprima.CelleDaSovrascrivere} celle valorizzate da sovrascrivere. Il valore mostrato sarà quello con timestamp più recente.",
                $"{anteprima.CelleDaCompilare} cells without a score to fill · {anteprima.CelleDaSovrascrivere} valued cells to overwrite. The visible value will be the one with the newest timestamp.",
                $"{anteprima.CelleDaCompilare} cellules sans score à remplir · {anteprima.CelleDaSovrascrivere} cellules évaluées à remplacer. La valeur affichée sera celle dont l'horodatage est le plus récent."),
            Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(riepilogo);
        if (anteprima.CelleNonModificabili > 0)
            panel.Children.Add(new TextBlock
            {
                Text = S(
                    $"{anteprima.CelleNonModificabili} celle non applicabili non verranno cambiate.",
                    $"{anteprima.CelleNonModificabili} non-applicable cells will remain unchanged.",
                    $"{anteprima.CelleNonModificabili} cellules non applicables resteront inchangées."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });

        var contenitoreInput = new StackPanel { Spacing = 8 };
        var voci = Enum.GetValues<PunteggioConformita>().Select(p => new VoceValutazione(p, p.Label())).ToList();
        var combo = new ComboBox { ItemsSource = voci, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(combo, "Audit_ManualeBatch_Punteggio_ComboBox");
        contenitoreInput.Children.Add(new TextBlock { Text = T("AuditValutazioneVeicoloScegliValoreLabel"), Classes = { "Muted" }, FontSize = 11 });
        contenitoreInput.Children.Add(combo);
        var baseline = new TextBox { PlaceholderText = T("AuditValutazioneVeicoloBaselineSwPlaceholder") };
        AutomationProperties.SetAutomationId(baseline, "Audit_ManualeBatch_BaselineSw_TextBox");
        contenitoreInput.Children.Add(baseline);
        var nota = new TextBox { PlaceholderText = T("AuditMotivoPlaceholder"), AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(nota, "Audit_ManualeBatch_Nota_TextBox");
        contenitoreInput.Children.Add(nota);
        panel.Children.Add(contenitoreInput);

        var confermaSovrascrittura = new StackPanel { Spacing = 8, IsVisible = false };
        confermaSovrascrittura.Children.Add(new TextBlock
        {
            Text = S(
                $"Confermi di applicare il nuovo valore manuale su {anteprima.CelleDaSovrascrivere} celle già valorizzate del solo veicolo selezionato? L'operazione ha data attuale; un Test eseguito successivamente potrà prevalere. Annullando non verrà scritto nulla.",
                $"Confirm applying the new manual value to {anteprima.CelleDaSovrascrivere} already-valued cells on the selected vehicle only? The entry is dated now; a later Test can take precedence. Cancel writes nothing.",
                $"Confirmez-vous l'application de la nouvelle valeur manuelle aux {anteprima.CelleDaSovrascrivere} cellules déjà évaluées du seul véhicule sélectionné ? La saisie est datée maintenant ; un Test ultérieur peut prévaloir. L'annulation n'écrit rien."),
            FontWeight = FontWeight.SemiBold, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        });
        var errore = new TextBlock { Foreground = PennelloErrore, FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(errore);
        panel.Children.Add(confermaSovrascrittura);

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("AnnullaSemplice"), Classes = { "Secondary" } };
        var applica = new Button
        {
            Content = anteprima.CelleDaSovrascrivere > 0
                ? S("Rivedi sovrascritture", "Review overwrites", "Vérifier les remplacements")
                : T("ConfermaSemplice"),
            Classes = { "Primary" },
        };
        var conferma = new Button
        {
            Content = S("Conferma sovrascrittura", "Confirm overwrite", "Confirmer le remplacement"),
            Classes = { "Primary" }, IsVisible = false,
        };
        AutomationProperties.SetAutomationId(annulla, "Audit_ManualeBatch_Annulla_Button");
        AutomationProperties.SetAutomationId(applica, "Audit_ManualeBatch_Applica_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_ManualeBatch_ConfermaSovrascrittura_Button");
        bottoni.Children.Add(annulla); bottoni.Children.Add(applica); bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        void Salva(bool confermaOverwrite)
        {
            var scelto = combo.SelectedItem as VoceValutazione;
            var motivazione = nota.Text?.Trim() ?? "";
            if (scelto == null) { errore.Text = T("AuditScegliValoreErrore"); errore.IsVisible = true; return; }
            if (motivazione.Length == 0) { errore.Text = T("AuditMotivoObbligatorio"); errore.IsVisible = true; return; }
            var baselineSw = string.IsNullOrWhiteSpace(baseline.Text) ? null : baseline.Text.Trim();
            if (vm.ApplicaManualeMassivaAudit(anteprima, scelto.Valore, motivazione, baselineSw, confermaOverwrite))
                flyout.Hide();
            else
            {
                errore.Text = vm.MessaggioErrore ?? S("Nessuna cella è stata modificata.", "No cell was changed.", "Aucune cellule n'a été modifiée.");
                errore.IsVisible = true;
            }
        }

        annulla.Click += (_, _) => flyout.Hide();
        applica.Click += (_, _) =>
        {
            if (anteprima.Celle.Count == 0)
            {
                errore.Text = S("Non ci sono celle modificabili nel perimetro visibile.", "There are no editable cells in the visible scope.", "Aucune cellule modifiable dans le périmètre visible.");
                errore.IsVisible = true;
                return;
            }
            if (anteprima.CelleDaSovrascrivere > 0)
            {
                contenitoreInput.IsVisible = false;
                confermaSovrascrittura.IsVisible = true;
                applica.IsVisible = false;
                conferma.IsVisible = true;
                return;
            }
            Salva(confermaOverwrite: false);
        };
        conferma.Click += (_, _) => Salva(confermaOverwrite: true);

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    private void CaricaPreferenzeMatrice()
    {
        var testo = ViewStateService.LeggiPerOperatore(ChiavePreferenzeMatrice);
        if (string.IsNullOrWhiteSpace(testo))
        {
            _preferenzeMatrice = new PreferenzeMatriceAudit();
            return;
        }

        try
        {
            _preferenzeMatrice = JsonSerializer.Deserialize<PreferenzeMatriceAudit>(testo) ?? new PreferenzeMatriceAudit();
        }
        catch (JsonException)
        {
            _preferenzeMatrice = new PreferenzeMatriceAudit();
        }

        var larghezze = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (componente, larghezza) in _preferenzeMatrice.LarghezzeEcu ?? new())
            if (!string.IsNullOrWhiteSpace(componente) && double.IsFinite(larghezza))
                larghezze[componente] = Math.Clamp(larghezza, LarghezzaEcuMinima, LarghezzaEcuMassima);
        _preferenzeMatrice.LarghezzeEcu = larghezze;

        var altezze = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (requisitoId, altezza) in _preferenzeMatrice.AltezzeRighe ?? new())
            if (!string.IsNullOrWhiteSpace(requisitoId) && double.IsFinite(altezza))
                altezze[requisitoId] = Math.Clamp(altezza, AltezzaRigaMinima, AltezzaRigaMassima);
        _preferenzeMatrice.AltezzeRighe = altezze;
        _preferenzeMatrice.OrdineEcu = (_preferenzeMatrice.OrdineEcu ?? new())
            .Where(componente => !string.IsNullOrWhiteSpace(componente)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_preferenzeMatrice.LarghezzaRequisito is { } larghezzaRequisito)
            _preferenzeMatrice.LarghezzaRequisito = double.IsFinite(larghezzaRequisito)
                ? Math.Clamp(larghezzaRequisito, LarghezzaRequisitoMinima, LarghezzaRequisitoMassima)
                : null;
        _preferenzeMatrice.Versione = 1;
    }

    private double LarghezzaRequisitoCorrente => _preferenzeMatrice.LarghezzaRequisito is { } larghezza
        ? Math.Clamp(larghezza, LarghezzaRequisitoMinima, LarghezzaRequisitoMassima)
        : LarghezzaColonnaRequisito;

    private double LarghezzaEcuCorrente(string componente) => Trova(_preferenzeMatrice.LarghezzeEcu, componente, out var larghezza)
        ? Math.Clamp(larghezza, LarghezzaEcuMinima, LarghezzaEcuMassima)
        : LarghezzaColonnaEcu;

    private double AltezzaRigaCorrente(string requisitoId) => Trova(_preferenzeMatrice.AltezzeRighe, requisitoId, out var altezza)
        ? Math.Clamp(altezza, AltezzaRigaMinima, AltezzaRigaMassima)
        : AltezzaRigaMatrice;

    private static bool Trova(Dictionary<string, double> valori, string chiave, out double valore)
    {
        if (valori.TryGetValue(chiave, out valore)) return true;
        valore = 0;
        return false;
    }

    private static string TagPerColonnaEcu(string componente) => "AUDIT:COLONNA:componente:" + componente;
    private static string IdRiga(string requisitoId) => "Audit_Matrice_Riga_" + Uri.EscapeDataString(requisitoId);

    private IReadOnlyList<string> OrdineEcu(AuditViewModel vm) =>
        vm.HaFiltroStatiMatrice ? _colonneVisualizzate : OrdineEcuComplete(vm);

    private IReadOnlyList<string> OrdineEcuComplete(AuditViewModel vm)
    {
        var colonne = vm.EcuColonne;
        if (colonne.Count < 2) return colonne;
        var ordine = new List<string>(colonne.Count);
        foreach (var ecuSalvata in _preferenzeMatrice.OrdineEcu)
        {
            var corrente = colonne.FirstOrDefault(componente => componente.Equals(ecuSalvata, StringComparison.OrdinalIgnoreCase));
            if (corrente != null && !ordine.Contains(corrente, StringComparer.OrdinalIgnoreCase)) ordine.Add(corrente);
        }
        foreach (var componente in colonne)
            if (!ordine.Contains(componente, StringComparer.OrdinalIgnoreCase)) ordine.Add(componente);
        return ordine;
    }

    private void ConfiguraIntestazione(Border intestazione, string tag, string? ecuRiordinabile)
    {
        intestazione.Tag = tag;
        intestazione.Focusable = true;
        AutomationProperties.SetName(intestazione, ecuRiordinabile == null
            ? LocalizationService.Instance.Traduci("AuditResizeColonnaRequisitoNome")
            : string.Format(LocalizationService.Instance.Traduci("AuditResizeColonnaEcuNomeFmt"), ecuRiordinabile));
        ToolTip.SetTip(intestazione, LocalizationService.Instance.Traduci(ecuRiordinabile == null
            ? "AuditResizeColonnaRequisitoTip" : "AuditResizeColonnaEcuTip"));
        intestazione.PointerPressed += OnSuperficieMatricePointerPressed;
        intestazione.PointerMoved += OnSuperficieMatricePointerMoved;
        intestazione.PointerReleased += OnSuperficieMatricePointerReleased;
        intestazione.PointerCaptureLost += OnSuperficieMatricePointerCaptureLost;
        intestazione.KeyDown += OnSuperficieMatriceKeyDown;
    }

    private void OnSuperficieMatricePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control superficie || !e.GetCurrentPoint(superficie).Properties.IsLeftButtonPressed) return;
        var posizioneSuperficie = e.GetPosition(superficie);
        IdentitaManigliaMatrice? identita = null;
        if (superficie is Border && superficie.Tag is string tagColonna && IdentitaColonna(tagColonna) is { } identitaColonna)
        {
            if (VicinoAlBordo(posizioneSuperficie.X, superficie.Bounds.Width)) identita = identitaColonna;
            else if (identitaColonna.Tipo == TipoOperazioneMatrice.LarghezzaEcu && !FonteInterattiva(e.Source))
                identita = new(TipoOperazioneMatrice.RiordinoEcu, identitaColonna.Chiave);
        }
        else if (superficie is Grid { Tag: string tagRiga } && tagRiga.StartsWith(TagRiga, StringComparison.Ordinal)
            && VicinoAlBordo(posizioneSuperficie.Y, superficie.Bounds.Height))
            identita = new(TipoOperazioneMatrice.AltezzaRiga, tagRiga[TagRiga.Length..]);
        if (identita == null) return;

        var valore = identita.Tipo switch
        {
            TipoOperazioneMatrice.LarghezzaRequisito => LarghezzaRequisitoCorrente,
            TipoOperazioneMatrice.LarghezzaEcu => LarghezzaEcuCorrente(identita.Chiave),
            TipoOperazioneMatrice.AltezzaRiga => AltezzaRigaCorrente(identita.Chiave),
            _ => 0,
        };
        var indice = identita.Tipo == TipoOperazioneMatrice.RiordinoEcu && DataContext is AuditViewModel vm
            ? OrdineEcu(vm).ToList().FindIndex(componente => componente.Equals(identita.Chiave, StringComparison.OrdinalIgnoreCase))
            : -1;
        _ridimensionamentoMatrice = new(e.Pointer, superficie, identita, e.GetPosition(this), valore, indice);
        e.Pointer.Capture(superficie);
        e.Handled = true;
    }

    private void OnSuperficieMatricePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_ridimensionamentoMatrice is { } stato && ReferenceEquals(stato.Puntatore, e.Pointer))
        {
            AggiornaTrascinamentoMatrice(stato, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (sender is Control superficie) AggiornaCursoreRidimensionamento(superficie, e.GetPosition(superficie));
    }

    private void OnSuperficieMatricePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_ridimensionamentoMatrice is not { } stato || !ReferenceEquals(stato.Puntatore, e.Pointer)) return;
        AggiornaTrascinamentoMatrice(stato, e.GetPosition(this));
        var cambiata = DimensioneVariataDopoTrascinamento(stato);
        if (stato.Identita.Tipo == TipoOperazioneMatrice.RiordinoEcu)
            cambiata = DistanzaTrascinamento(stato, e.GetPosition(this)) >= 4;
        _ridimensionamentoMatrice = null;
        e.Pointer.Capture(null);
        if (stato.Identita.Tipo == TipoOperazioneMatrice.RiordinoEcu && cambiata && DataContext is AuditViewModel vm)
            cambiata = RilasciaRiordinoColonna(vm, stato, e.GetPosition(MatriceIntestazioneBordo)) || cambiata;
        if (cambiata) SalvaPreferenzeMatrice();
        e.Handled = true;
    }

    private void OnSuperficieMatricePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_ridimensionamentoMatrice is not { } stato || !ReferenceEquals(stato.Puntatore, e.Pointer)) return;
        var cambiata = DimensioneVariataDopoTrascinamento(stato);
        _ridimensionamentoMatrice = null;
        // Il riordino componente viene applicato solo al rilascio: con cattura persa resta l'ordine precedente.
        // Le dimensioni invece sono già aggiornate durante il trascinamento e vanno conservate.
        if (stato.Identita.Tipo != TipoOperazioneMatrice.RiordinoEcu && cambiata)
            SalvaPreferenzeMatrice();
    }

    private static bool VicinoAlBordo(double posizione, double estensione) =>
        posizione <= SpessoreAreaRidimensionamento || estensione - posizione <= SpessoreAreaRidimensionamento;

    private static bool FonteInterattiva(object? source) => source is Button
        || source is Visual visual && visual.GetVisualAncestors().OfType<Button>().Any();

    private static double DistanzaTrascinamento(StatoManigliaMatrice stato, Point posizione) =>
        Math.Sqrt(Math.Pow(posizione.X - stato.PosizioneIniziale.X, 2) + Math.Pow(posizione.Y - stato.PosizioneIniziale.Y, 2));

    private static IdentitaManigliaMatrice? IdentitaColonna(string tag) => tag switch
    {
        TagColonnaRequisito => new(TipoOperazioneMatrice.LarghezzaRequisito, ""),
        _ when tag.StartsWith("AUDIT:COLONNA:componente:", StringComparison.Ordinal) =>
            new(TipoOperazioneMatrice.LarghezzaEcu, tag["AUDIT:COLONNA:componente:".Length..]),
        _ => null,
    };

    private void AggiornaCursoreRidimensionamento(Control superficie, Point posizione)
    {
        var ridimensionabile = superficie.Tag is string tag && IdentitaColonna(tag) != null
            ? VicinoAlBordo(posizione.X, superficie.Bounds.Width)
            : superficie is Grid { Tag: string riga } && riga.StartsWith(TagRiga, StringComparison.Ordinal)
                && superficie.Bounds.Height - posizione.Y <= SpessoreAreaRidimensionamento;
        superficie.Cursor = ridimensionabile
            ? new Cursor(superficie is Grid ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast)
            : null;
    }

    private bool DimensioneVariataDopoTrascinamento(StatoManigliaMatrice stato) => stato.Identita.Tipo switch
    {
        TipoOperazioneMatrice.LarghezzaRequisito => Math.Abs(LarghezzaRequisitoCorrente - stato.ValoreIniziale) > 0.5,
        TipoOperazioneMatrice.LarghezzaEcu => Math.Abs(LarghezzaEcuCorrente(stato.Identita.Chiave) - stato.ValoreIniziale) > 0.5,
        TipoOperazioneMatrice.AltezzaRiga => Math.Abs(AltezzaRigaCorrente(stato.Identita.Chiave) - stato.ValoreIniziale) > 0.5,
        _ => false,
    };

    private bool AggiornaTrascinamentoMatrice(StatoManigliaMatrice stato, Point posizione)
    {
        var delta = stato.Identita.Tipo switch
        {
            TipoOperazioneMatrice.LarghezzaRequisito or TipoOperazioneMatrice.LarghezzaEcu => posizione.X - stato.PosizioneIniziale.X,
            TipoOperazioneMatrice.AltezzaRiga => posizione.Y - stato.PosizioneIniziale.Y,
            _ => 0,
        };
        return stato.Identita.Tipo switch
        {
            TipoOperazioneMatrice.LarghezzaRequisito => ImpostaLarghezzaRequisito(stato.ValoreIniziale + delta),
            TipoOperazioneMatrice.LarghezzaEcu => ImpostaLarghezzaEcu(stato.Identita.Chiave, stato.ValoreIniziale + delta),
            TipoOperazioneMatrice.AltezzaRiga => ImpostaAltezzaRiga(stato.Identita.Chiave, stato.ValoreIniziale + delta),
            _ => false,
        };
    }

    private bool ImpostaLarghezzaRequisito(double larghezza)
    {
        larghezza = Math.Clamp(larghezza, LarghezzaRequisitoMinima, LarghezzaRequisitoMassima);
        var cambiata = Math.Abs(LarghezzaRequisitoCorrente - larghezza) > 0.5;
        _preferenzeMatrice.LarghezzaRequisito = larghezza;
        ApplicaLarghezzeVisibili();
        return cambiata;
    }

    private bool ImpostaLarghezzaEcu(string componente, double larghezza)
    {
        larghezza = Math.Clamp(larghezza, LarghezzaEcuMinima, LarghezzaEcuMassima);
        var cambiata = Math.Abs(LarghezzaEcuCorrente(componente) - larghezza) > 0.5;
        _preferenzeMatrice.LarghezzeEcu[componente] = larghezza;
        ApplicaLarghezzeVisibili();
        return cambiata;
    }

    private bool ImpostaAltezzaRiga(string requisitoId, double altezza)
    {
        altezza = Math.Clamp(altezza, AltezzaRigaMinima, AltezzaRigaMassima);
        var cambiata = Math.Abs(AltezzaRigaCorrente(requisitoId) - altezza) > 0.5;
        if (Math.Abs(altezza - AltezzaRigaMatrice) < 0.5) _preferenzeMatrice.AltezzeRighe.Remove(requisitoId);
        else _preferenzeMatrice.AltezzeRighe[requisitoId] = altezza;
        ApplicaAltezzaRigaVisibile(requisitoId, altezza);
        return cambiata;
    }

    private void ApplicaLarghezzeVisibili()
    {
        foreach (var intestazione in MatriceIntestazione.Children.OfType<Border>())
            if (intestazione.Tag is string tag)
            {
                if (tag == TagColonnaRequisito) intestazione.Width = LarghezzaRequisitoCorrente;
                else if (tag.StartsWith("AUDIT:COLONNA:componente:", StringComparison.Ordinal))
                    intestazione.Width = LarghezzaEcuCorrente(tag["AUDIT:COLONNA:componente:".Length..]);
            }
        foreach (var cella in MatriceRighe.GetVisualDescendants().OfType<Control>())
            if (cella.Tag is string tag)
            {
                if (tag == TagColonnaRequisito) cella.Width = LarghezzaRequisitoCorrente;
                else if (tag.StartsWith(TagCellaEcu, StringComparison.Ordinal))
                    cella.Width = LarghezzaEcuCorrente(tag[TagCellaEcu.Length..]);
            }
        MatriceIntestazione.InvalidateMeasure();
        MatriceRighe.InvalidateMeasure();
        MatriceScroll.InvalidateMeasure();
    }

    private void ApplicaAltezzaRigaVisibile(string requisitoId, double altezza)
    {
        var id = IdRiga(requisitoId);
        var riga = MatriceRighe.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(g => AutomationProperties.GetAutomationId(g) == id);
        if (riga == null) return;
        riga.Height = altezza;
        var contenuto = riga.Children.OfType<StackPanel>().FirstOrDefault(p => Equals(p.Tag, TagContenutoRiga));
        if (contenuto == null) return;
        contenuto.Height = altezza;
        foreach (var cella in contenuto.Children.OfType<Control>()) cella.Height = altezza;
        riga.InvalidateMeasure();
        MatriceRighe.InvalidateMeasure();
        MatriceScroll.InvalidateMeasure();
    }

    private bool RilasciaRiordinoColonna(AuditViewModel vm, StatoManigliaMatrice stato, Point posizioneViewport)
    {
        if (stato.IndiceIniziale < 0) return false;
        var ordine = OrdineEcu(vm).ToList();
        if (stato.IndiceIniziale >= ordine.Count) return false;
        var x = posizioneViewport.X + MatriceScroll.Offset.X;
        var destinazione = ordine.Count;
        for (var i = 0; i < ordine.Count; i++)
        {
            var intestazione = MatriceIntestazione.Children.OfType<Border>()
                .FirstOrDefault(b => Equals(b.Tag, TagPerColonnaEcu(ordine[i])));
            if (intestazione != null && x < intestazione.Bounds.X + intestazione.Bounds.Width / 2)
            {
                destinazione = i;
                break;
            }
        }
        if (destinazione > stato.IndiceIniziale) destinazione--;
        destinazione = Math.Clamp(destinazione, 0, ordine.Count - 1);
        if (destinazione == stato.IndiceIniziale) return false;
        var elemento = ordine[stato.IndiceIniziale];
        ordine.RemoveAt(stato.IndiceIniziale);
        ordine.Insert(destinazione, elemento);
        _preferenzeMatrice.OrdineEcu = ordine;
        ApplicaOrdineColonne(vm, ordine);
        return true;
    }

    private void ApplicaOrdineColonne(AuditViewModel vm, IReadOnlyList<string> ordine)
    {
        var intestazioni = MatriceIntestazione.Children.OfType<Border>()
            .Where(b => b.Tag is string tag && tag.StartsWith("AUDIT:COLONNA:componente:", StringComparison.Ordinal))
            .ToDictionary(b => (string)b.Tag!, StringComparer.OrdinalIgnoreCase);
        var angolo = MatriceIntestazione.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, TagColonnaRequisito));
        MatriceIntestazione.Children.Clear();
        if (angolo != null) MatriceIntestazione.Children.Add(angolo);
        foreach (var componente in ordine)
            if (intestazioni.TryGetValue(TagPerColonnaEcu(componente), out var intestazione)) MatriceIntestazione.Children.Add(intestazione);

        foreach (var pannello in MatriceRighe.GetVisualDescendants().OfType<StackPanel>().Where(p => Equals(p.Tag, TagContenutoRiga)))
        {
            var celle = pannello.Children.OfType<Control>().ToDictionary(c => c.Tag as string ?? "", StringComparer.OrdinalIgnoreCase);
            if (!celle.TryGetValue(TagColonnaRequisito, out var requisito)) continue;
            pannello.Children.Clear();
            pannello.Children.Add(requisito);
            foreach (var componente in ordine)
                if (celle.TryGetValue(TagPerColonnaEcu(componente), out var cella)) pannello.Children.Add(cella);
        }
        MatriceIntestazione.InvalidateMeasure();
        MatriceRighe.InvalidateMeasure();
        MatriceScroll.InvalidateMeasure();
    }

    private void SalvaPreferenzeMatrice()
    {
        _preferenzeMatrice.Versione = 1;
        try
        {
            ViewStateService.ScriviPerOperatore(ChiavePreferenzeMatrice, JsonSerializer.Serialize(_preferenzeMatrice));
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Salvataggio preferenze matrice Audit", ex);
        }
    }

    private void OnSuperficieMatriceKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { Tag: string tag } superficie || DataContext is not AuditViewModel vm
            || !ReferenceEquals(e.Source, superficie)) return;
        IdentitaManigliaMatrice? identita = superficie is Border ? IdentitaColonna(tag)
            : superficie is Grid && tag.StartsWith(TagRiga, StringComparison.Ordinal)
                ? new(TipoOperazioneMatrice.AltezzaRiga, tag[TagRiga.Length..]) : null;
        if (identita == null) return;
        if (identita.Tipo == TipoOperazioneMatrice.LarghezzaEcu && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && e.Key is Key.Left or Key.Right)
        {
            if (SpostaColonnaDaTastiera(vm, identita.Chiave, e.Key == Key.Left ? -1 : 1)) SalvaPreferenzeMatrice();
            e.Handled = true;
            return;
        }
        var delta = e.Key switch
        {
            Key.Left => -8d,
            Key.Right => 8d,
            Key.Up => -8d,
            Key.Down => 8d,
            _ => 0,
        };
        if (delta == 0) return;
        var cambiata = identita.Tipo switch
        {
            TipoOperazioneMatrice.LarghezzaRequisito when e.Key is Key.Left or Key.Right => ImpostaLarghezzaRequisito(LarghezzaRequisitoCorrente + delta),
            TipoOperazioneMatrice.LarghezzaEcu when e.Key is Key.Left or Key.Right => ImpostaLarghezzaEcu(identita.Chiave, LarghezzaEcuCorrente(identita.Chiave) + delta),
            TipoOperazioneMatrice.AltezzaRiga when e.Key is Key.Up or Key.Down => ImpostaAltezzaRiga(identita.Chiave, AltezzaRigaCorrente(identita.Chiave) + delta),
            _ => false,
        };
        if (cambiata) SalvaPreferenzeMatrice();
        e.Handled = true;
    }

    private bool SpostaColonnaDaTastiera(AuditViewModel vm, string componente, int direzione)
    {
        var ordine = OrdineEcu(vm).ToList();
        var indice = ordine.FindIndex(e => e.Equals(componente, StringComparison.OrdinalIgnoreCase));
        var destinazione = indice + direzione;
        if (indice < 0 || destinazione < 0 || destinazione >= ordine.Count) return false;
        (ordine[indice], ordine[destinazione]) = (ordine[destinazione], ordine[indice]);
        _preferenzeMatrice.OrdineEcu = ordine;
        ApplicaOrdineColonne(vm, ordine);
        return true;
    }

    /// <summary>Intestazione di colonna componente con, sotto il nome, il pulsante "Propaga tutti i '!' di questa
    /// componente" quando visibile (Piano 6.4.0, punto 1: solo Master, solo se la colonna ha almeno una cella "!").
    /// Stesso Border/stile di IntestazioneCella per le altre intestazioni; qui il contenuto è uno StackPanel
    /// invece di un solo TextBlock quando il pulsante è presente.</summary>
    private static Border IntestazioneEcuCella(AuditViewModel vm, string ecuNome)
    {
        var testo = new TextBlock
        {
            Text = PolarionTicketTitle.SiglaEcu(ecuNome) is { Length: > 0 } sigla ? sigla : ecuNome, FontWeight = FontWeight.SemiBold, FontSize = 11,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(testo, ecuNome); // sigla in colonna, nome completo nel tooltip (due componente con la stessa sigla restano distinguibili)
        Control contenuto = testo;
        if (vm.EMaster)
        {
            var azioni = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, Children = { testo } };
            var applicaColonna = new Button { Content = "✎", Classes = { "Link" }, Padding = new Thickness(2, 0), FontSize = 10 };
            AutomationProperties.SetAutomationId(applicaColonna, $"Audit_ManualeColonna_Button_{ecuNome}");
            ToolTip.SetTip(applicaColonna, LocalizationService.Instance.Scegli("Applica un valore manuale alla colonna", "Apply a manual value to this column", "Appliquer une valeur manuelle à la colonne"));
            applicaColonna.Click += (_, _) => ApriApplicazioneManualeMassivaFlyout(applicaColonna, vm, ecuNome, null);
            azioni.Children.Add(applicaColonna);
            if (vm.EcuHaCellePropagabili(ecuNome))
            {
                var bottone = new Button
                {
                    Content = LocalizationService.Instance.Traduci("AuditPropagaEcuBtn"),
                    Classes = { "Link" }, Padding = new Thickness(0), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center,
                };
                AutomationProperties.SetAutomationId(bottone, $"Audit_PropagaEcu_Button_{ecuNome}");
                bottone.Click += (_, _) => ApriPropagaMassivaFlyout(bottone, vm, ecuNome);
                azioni.Children.Add(bottone);
            }
            var numeroRimuovibili = vm.NumeroValutazioniRimuovibiliColonna(ecuNome);
            if (numeroRimuovibili > 0)
            {
                var rimuoviColonna = new Button
                {
                    Content = "×", Classes = { "Link" }, Padding = new Thickness(2, 0), FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = PennelloErrore,
                };
                var etichettaRimuovi = LocalizationService.Instance.Scegli(
                    $"Rimuovi {numeroRimuovibili} valutazioni manuali o propagate di questa componente sul veicolo selezionato",
                    $"Remove {numeroRimuovibili} manual or propagated assessments for this componente on the selected vehicle",
                    $"Supprimer {numeroRimuovibili} évaluations manuelles ou propagées de cet componente sur le véhicule sélectionné");
                AutomationProperties.SetAutomationId(rimuoviColonna, $"Audit_RimuoviColonna_Button_{ecuNome}");
                AutomationProperties.SetName(rimuoviColonna, etichettaRimuovi);
                ToolTip.SetTip(rimuoviColonna, etichettaRimuovi);
                rimuoviColonna.Click += (_, _) => ApriRimozioneValutazioniColonnaFlyout(rimuoviColonna, vm, ecuNome);
                azioni.Children.Add(rimuoviColonna);
            }
            contenuto = azioni;
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E2E8F0")),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(6, 4),
            Child = contenuto,
        };
    }

    /// <summary>Click sul pulsante "Propaga tutti i '!' di tutti i componenti" nell'intestazione della card matrice
    /// (Piano 6.4.0, punto 1).</summary>
    private void OnAzzeraFiltroTipo(object? sender, RoutedEventArgs e) => (DataContext as AuditViewModel)?.AzzeraFiltroTipo();

    private void OnAzzeraFiltriDtc(object? sender, RoutedEventArgs e) => (DataContext as AuditViewModel)?.AzzeraFiltriDtc();
    private void OnChiudiDettaglioDtc(object? sender, RoutedEventArgs e) => (DataContext as AuditViewModel)?.ChiudiDettaglioDtc();
    private void OnApriFiltroDtcEcu(object? sender, RoutedEventArgs e)
    { if (sender is Button b && DataContext is AuditViewModel vm) FiltroMultiSelectFlyoutHelper.Apri(b, vm.FiltroDtcEcu); }
    private void OnApriFiltroDtcStato(object? sender, RoutedEventArgs e)
    { if (sender is Button b && DataContext is AuditViewModel vm) FiltroMultiSelectFlyoutHelper.Apri(b, vm.FiltroDtcStato); }

    private async void OnEsportaDettaglioDtcCsv(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AuditViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Dettaglio guasto Extra (CSV)", SuggestedFileName = $"TestLoom_Audit_guasto_Extra_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "csv", FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } }
            });
            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(percorso)) await vm.EsportaDettaglioDtcCsv(percorso);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione dettaglio guasto Extra", ex); }
    }

    private void OnApriFiltroTipoRequisito(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && DataContext is AuditViewModel vm)
            FiltroMultiSelectFlyoutHelper.Apri(b, vm.FiltroTipoRequisito);
    }

    private void OnPropagaVeicolo(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AuditViewModel vm && sender is Control anchor) ApriPropagaMassivaFlyout(anchor, vm, null);
    }

    /// <summary>Dialogo di anteprima/conferma della Propagazione massiva (Piano 6.4.0, punto 4):
    /// <paramref name="ecuNome"/> null propaga su tutti i componenti del sottoveicolo selezionato, altrimenti solo
    /// su quella componente. Mostra N celle da propagare (requisito → valore ← veicolo sorgente) e M conflitti
    /// lasciati "!", con una nota obbligatoria unica applicata a tutte le celle propagate — il pulsante di
    /// conferma resta disabilitato finché la nota è vuota. Annullare non scrive nulla.</summary>
    private static void ApriPropagaMassivaFlyout(Control anchor, AuditViewModel vm, string? ecuNome, string? requisitoId = null)
    {
        string T(string chiave) => LocalizationService.Instance.Traduci(chiave);
        var anteprima = vm.AnteprimaPropagazioneMassiva(ecuNome, requisitoId);

        var panel = new StackPanel { Spacing = 8, MinWidth = 340, MaxWidth = 420 };
        var titolo = requisitoId != null
            ? string.Format(LocalizationService.Instance.Scegli(
                "Propaga il requisito {0} su tutti i componenti di {1}",
                "Propagate requirement {0} across every componente on {1}",
                "Propager l'exigence {0} sur tous les componente de {1}"), requisitoId, vm.SottoveicoloSelezionato)
            : ecuNome != null
                ? string.Format(T("AuditPropagaMassivaTitoloEcuFmt"), ecuNome, vm.SottoveicoloSelezionato)
                : string.Format(T("AuditPropagaMassivaTitoloVeicoloFmt"), vm.SottoveicoloSelezionato);
        panel.Children.Add(new TextBlock { Text = titolo, FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        if (anteprima.DaPropagare.Count == 0 && anteprima.Conflitti.Count == 0)
            panel.Children.Add(new TextBlock { Text = T("AuditPropagaMassivaNessunaCella"), Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap });

        if (anteprima.DaPropagare.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = string.Format(T("AuditPropagaMassivaDaPropagareFmt"), anteprima.DaPropagare.Count), FontSize = 11, FontWeight = FontWeight.SemiBold });
            var elencoBox = new ScrollViewer { MaxHeight = 160, Content = new ItemsControl { ItemsSource = anteprima.DaPropagare.Select(v => (object)new TextBlock
            {
                Text = string.Format(T("AuditPropagaMassivaVoceFmt"), v.Componente, v.RequisitoId,
                    v.Sorgente.PunteggioSorgente == PunteggioConformita.NonApplicabile ? "N/A" : ((int)v.Sorgente.PunteggioSorgente).ToString(),
                    v.Sorgente.VeicoloSorgente),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            }).ToList() } };
            panel.Children.Add(elencoBox);
        }

        if (anteprima.Conflitti.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = string.Format(T("AuditPropagaMassivaConflittiFmt"), anteprima.Conflitti.Count), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = PennelloAvviso });
            var conflittiBox = new ScrollViewer { MaxHeight = 100, Content = new ItemsControl { ItemsSource = anteprima.Conflitti.Select(c => (object)new TextBlock
            {
                Text = string.Format(T("AuditPropagaMassivaConflittoVoceFmt"), c.Componente, c.RequisitoId),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            }).ToList() } };
            panel.Children.Add(conflittiBox);
        }

        var motivazioneBox = new TextBox { PlaceholderText = T("AuditPropagaMassivaNotaPlaceholder"), AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(motivazioneBox, "Audit_PropagaMassiva_Nota_TextBox");
        panel.Children.Add(motivazioneBox);

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = T("AnnullaSemplice"), Classes = { "Secondary" } };
        var conferma = new Button { Content = T("ConfermaSemplice"), Classes = { "Primary" }, IsEnabled = false };
        AutomationProperties.SetAutomationId(annulla, "Audit_PropagaMassiva_Annulla_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_PropagaMassiva_Conferma_Button");
        bottoni.Children.Add(annulla); bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        // Il pulsante di conferma resta disabilitato finché la nota è vuota (Piano 6.4.0, punto 4) — e finché
        // non c'è nessuna cella da propagare, non c'è nulla da confermare.
        motivazioneBox.TextChanged += (_, _) => conferma.IsEnabled = anteprima.DaPropagare.Count > 0 && !string.IsNullOrWhiteSpace(motivazioneBox.Text);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        annulla.Click += (_, _) => flyout.Hide(); // Annulla = nessuna scrittura.
        conferma.Click += (_, _) =>
        {
            var nota = motivazioneBox.Text?.Trim() ?? "";
            if (nota.Length == 0 || anteprima.DaPropagare.Count == 0) return;
            // SOLO le celle mostrate come "da propagare" (review indipendente 2026-09-26, CRITICO): i
            // conflitti mostrati nell'anteprima non hanno un'unica sorgente/punteggio da confermare, quindi
            // non vanno mai passati come "attesi" — ConfermaPropagazioneMassiva/PropagaMassiva li
            // ricalcolano comunque da zero sui dati freschi al momento del salvataggio, ma solo per le celle
            // che l'operatore ha effettivamente visto qui come proposta concreta.
            vm.ConfermaPropagazioneMassiva(ecuNome, anteprima.DaPropagare, nota);
            flyout.Hide();
        };

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Anteprima e conferma esplicita della rimozione a livello requisito. Mostra il numero e ogni
    /// componente interessata; l'azione archivia solo le valutazioni Manuali/Propagate del veicolo selezionato.</summary>
    private static void ApriRimozioneValutazioniRigaFlyout(Control anchor, AuditViewModel vm, string requisitoId)
    {
        string S(string italiano, string inglese, string francese) =>
            LocalizationService.Instance.Scegli(italiano, inglese, francese);
        var anteprima = vm.AnteprimaRimozioneValutazioniRiga(requisitoId);
        var veicolo = string.IsNullOrWhiteSpace(anteprima.Veicolo) ? vm.SottoveicoloSelezionato ?? "" : anteprima.Veicolo;
        var panel = new StackPanel { Spacing = 8, MinWidth = 340, MaxWidth = 440 };
        var titolo = new TextBlock
        {
            Text = string.Format(S(
                "Rimuovere {0} valutazioni manuali o propagate di {1} dal veicolo {2}?",
                "Remove {0} manual or propagated assessments for {1} on vehicle {2}?",
                "Supprimer {0} évaluations manuelles ou propagées de {1} sur le véhicule {2} ?"),
                anteprima.NumeroValutazioni, requisitoId, veicolo),
            FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(titolo, "Audit_RimuoviRiga_Titolo_Text");
        panel.Children.Add(titolo);

        if (anteprima.HaValutazioni)
        {
            var elementi = anteprima.Valutazioni.Select(v => (object)new TextBlock
            {
                Text = $"{v.Componente} · {(v.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)v.Punteggio).ToString())} · {v.Origine}"
                    + (string.IsNullOrWhiteSpace(v.Nota) ? "" : $" · {v.Nota}"),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            }).ToList();
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 180,
                Content = new ItemsControl { ItemsSource = elementi },
            });
            panel.Children.Add(new TextBlock
            {
                Text = S(
                    "Le valutazioni saranno archiviate con lo storico. I Test e i valori legacy non verranno modificati.",
                    "The assessments will be archived with their history. Tests and legacy values will not be changed.",
                    "Les évaluations seront archivées avec leur historique. Les Tests et valeurs héritées ne seront pas modifiés."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = vm.MessaggioErrore ?? S("Non ci sono valutazioni manuali o propagate da rimuovere.",
                    "There are no manual or propagated assessments to remove.",
                    "Aucune évaluation manuelle ou propagée à supprimer."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
        }

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = S("Annulla", "Cancel", "Annuler"), Classes = { "Secondary" } };
        var conferma = new Button { Content = S("Rimuovi valutazioni", "Remove assessments", "Supprimer les évaluations"), Classes = { "Primary" }, IsEnabled = anteprima.HaValutazioni };
        AutomationProperties.SetAutomationId(annulla, "Audit_RimuoviRiga_Annulla_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_RimuoviRiga_Conferma_Button");
        bottoni.Children.Add(annulla);
        bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        annulla.Click += (_, _) => flyout.Hide();
        conferma.Click += (_, _) =>
        {
            if (anteprima.HaValutazioni) vm.RimuoviValutazioniRiga(anteprima);
            flyout.Hide();
        };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Conferma esplicita della rimozione di tutte le valutazioni Manuali/Propagate per un componente e il
    /// veicolo corrente. Mostra il conteggio e i requisiti inclusi nel batch atomico.</summary>
    private static void ApriRimozioneValutazioniColonnaFlyout(Control anchor, AuditViewModel vm, string componente)
    {
        string S(string italiano, string inglese, string francese) =>
            LocalizationService.Instance.Scegli(italiano, inglese, francese);
        var anteprima = vm.AnteprimaRimozioneValutazioniColonna(componente);
        var veicolo = string.IsNullOrWhiteSpace(anteprima.Veicolo) ? vm.SottoveicoloSelezionato ?? "" : anteprima.Veicolo;
        var panel = new StackPanel { Spacing = 8, MinWidth = 340, MaxWidth = 440 };
        var titolo = new TextBlock
        {
            Text = string.Format(S(
                "Rimuovere {0} valutazioni manuali o propagate del componente {1} dal veicolo {2}?",
                "Remove {0} manual or propagated assessments for component {1} on vehicle {2}?",
                "Supprimer {0} évaluations manuelles ou propagées de il componente {1} sur le véhicule {2} ?"),
                anteprima.NumeroValutazioni, componente, veicolo),
            FontWeight = FontWeight.SemiBold, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(titolo, "Audit_RimuoviColonna_Titolo_Text");
        panel.Children.Add(titolo);

        if (anteprima.HaValutazioni)
        {
            var elementi = anteprima.Valutazioni.Select(v => (object)new TextBlock
            {
                Text = $"{v.RequisitoId} · {(v.Punteggio == PunteggioConformita.NonApplicabile ? "N/A" : ((int)v.Punteggio).ToString())} · {v.Origine}"
                    + (string.IsNullOrWhiteSpace(v.Nota) ? "" : $" · {v.Nota}"),
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            }).ToList();
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 180,
                Content = new ItemsControl { ItemsSource = elementi },
            });
            panel.Children.Add(new TextBlock
            {
                Text = S(
                    "Le valutazioni saranno archiviate con lo storico. I Test e i valori legacy non verranno modificati.",
                    "The assessments will be archived with their history. Tests and legacy values will not be changed.",
                    "Les évaluations seront archivées avec leur historique. Les Tests et valeurs héritées ne seront pas modifiés."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = vm.MessaggioErrore ?? S("Non ci sono valutazioni manuali o propagate da rimuovere.",
                    "There are no manual or propagated assessments to remove.",
                    "Aucune évaluation manuelle ou propagée à supprimer."),
                Classes = { "Muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
        }

        var bottoni = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var annulla = new Button { Content = S("Annulla", "Cancel", "Annuler"), Classes = { "Secondary" } };
        var conferma = new Button { Content = S("Rimuovi valutazioni", "Remove assessments", "Supprimer les évaluations"), Classes = { "Primary" }, IsEnabled = anteprima.HaValutazioni };
        AutomationProperties.SetAutomationId(annulla, "Audit_RimuoviColonna_Annulla_Button");
        AutomationProperties.SetAutomationId(conferma, "Audit_RimuoviColonna_Conferma_Button");
        bottoni.Children.Add(annulla);
        bottoni.Children.Add(conferma);
        panel.Children.Add(bottoni);

        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        annulla.Click += (_, _) => flyout.Hide();
        conferma.Click += (_, _) =>
        {
            if (anteprima.HaValutazioni) vm.RimuoviValutazioniColonna(anteprima);
            flyout.Hide();
        };
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
    }

    private Border IntestazioneCella(string testo) => new()
    {
        Background = new SolidColorBrush(Color.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#293540" : "#F8FAFC")),
        BorderBrush = new SolidColorBrush(Color.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#576B7C" : "#E2E8F0")),
        BorderThickness = new Thickness(0, 0, 1, 1),
        Padding = new Thickness(6, 4),
        Child = new TextBlock
        {
            Text = testo, FontWeight = FontWeight.SemiBold, FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#EDF2F5" : "#1E293B")),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>Stesso schema di ReportView.OnEsportaExcel (Addendum: grill-me 2026-08-18, follow-up):
    /// picker nativo di salvataggio, mai un percorso fisso automatico.</summary>
    private async void OnEsportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AuditViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            // Dialogo unico di export: sezioni e filtri (che partono da quelli a video e, confermando, restano applicati alla pagina).
            if (topLevel is not Window finestraProprietaria) return;
            var opzioni = await EsportaOpzioniDialogo.Apri(finestraProprietaria, vm.StatoPerExport, vm.CreaDescrittoreExport());
            if (opzioni == null) return;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Scegli("Salva matrice Audit Excel", "Save Audit Excel matrix", "Enregistrer la matrice Audit Excel"),
                SuggestedFileName = $"TestLoom_Audit_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "xlsx",
                FileTypeChoices = new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } }
            });

            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(percorso)) await vm.EsportaExcelVerso(percorso, opzioni);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione matrice Audit", ex); }
    }
}

internal static class AuditViewControlExtensions
{
    public static T Con<T>(this T control, string automationId) where T : Avalonia.Controls.Control
    {
        Avalonia.Automation.AutomationProperties.SetAutomationId(control, automationId);
        return control;
    }
}
