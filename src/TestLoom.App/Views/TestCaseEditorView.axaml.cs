using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Perf 6.4.0 (apertura Test esistente misurata 2,5-2,8s a caldo/18535 visuali, richiesta utente
/// 2026-09-25): i gruppi di campi strumento (GruppiTestCostanti/GruppiRevisioneCostanti/GruppiVisual di
/// ogni Condizione) vivono dietro un Expander SEMPRE collassato all'apertura (IsExpanded="False" nel
/// markup), ma il ItemsControl con il template "pesante" per campo (~15-19 controlli per singolo
/// CampoStrumentoValore: un ramo per ogni Tipo possibile, tutti materializzati e nascosti con IsVisible)
/// veniva comunque costruito subito perché il Content di un Expander non è mai lazy in Avalonia — costo
/// pagato anche se l'utente non apre mai quella tendina. Questo MultiValueConverter, abbinato al binding
/// "{Binding #GruppoCampiExpander.IsExpanded}" sull'ItemsSource dell'ItemsControl annidato (invece del
/// solito "{Binding Campi}" diretto), restituisce null finché l'Expander resta chiuso: l'ItemsControl non
/// genera alcun container finché l'utente non lo espande la prima volta. Stessi dati/comandi/converter una
/// volta espanso (nessun cambio di comportamento), solo il MOMENTO della costruzione cambia. Nessun test
/// FlaUI referenzia questi gruppi per AutomationId (grep in TestLoom.App.UITests), quindi nessun test da
/// adattare.</summary>
public sealed class CampiSeEspansoConverter : IMultiValueConverter
{
    public static readonly CampiSeEspansoConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[1] is not bool espanso || !espanso) return null;
        return values[0];
    }
}

public partial class TestCaseEditorView : UserControl
{
    // Il caricamento iniziale (valori esistenti che si legano ai controlli) e i comandi che clonano dati
    // (nuova iterazione, duplica condizione...) generano gli stessi eventi TextChanged/SelectionChanged di
    // una modifica reale dell'utente, perché il binding non distingue "valore impostato dal caricamento"
    // da "valore digitato/scelto ora". Per i TextBox IsFocused basta (impostare Text da binding non dà
    // focus); per i ComboBox invece IsFocused resta false anche durante una scelta reale dell'utente (la
    // selezione avviene nel popup, che non sposta il focus da tastiera sul ComboBox stesso) — non è quindi
    // un segnale utilizzabile qui. Si rimanda "pronto" al ciclo UI successivo alla costruzione: tutto il
    // legame iniziale dei dati avviene in modo sincrono in questo stesso ciclo, mentre un'interazione reale
    // dell'utente può avvenire solo dopo che la pagina è già visibile.
    private bool _prontoPerModifiche;

    public TestCaseEditorView()
    {
        InitializeComponent();
        // Rapida: tasti 1-4/0 impostano l'esito dell'operazione con il fuoco, Ctrl+V incolla uno screenshot su di essa.
        AddHandler(KeyDownEvent, OnTastoRigaRapida, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnRigaRapidaDragOver);
        AddHandler(DragDrop.DropEvent, OnRigaRapidaDrop);

        // Layout dell'editor (6.6.1): la larghezza utile della pagina decide se le card a tre colonne vanno a una colonna e se i
        // link di sezione diventano il menu "Vai a" (sotto 1100 DIP). Si aggiorna a ogni cambio di dimensione e di ViewModel.
        EditorScroll.SizeChanged += (_, _) => AggiornaLarghezzaEditor();
        DataContextChanged += (_, _) => AggiornaLarghezzaEditor();

        // La maggior parte dei campi testuali del form (Descrizione, matricola, baseline, tutti i "campi
        // specifici" per tipo test, note, deviazioni...) sono legati direttamente a proprietà del modello
        // (TestCase.Descrizione, RevisioneCorrente.CampiSpecifici.*), non a proprietà wrapper del ViewModel:
        // scriverci dentro non faceva mai scattare ModificheNonSalvate. Il guardrail "modifiche non salvate"
        // aggiunto altrove (navigazione, logout, chiusura finestra) restava perciò cieco praticamente a tutto
        // il contenuto reale del test, lasciando che venisse perso in silenzio. Un solo handler in bubbling
        // sulla radice intercetta ogni TextBox del form senza dover riscrivere ciascun campo come proprietà
        // wrapper; IsFocused distingue un'digitazione reale dell'utente da un valore impostato dal binding
        // in fase di caricamento o da un comando (es. copia baseline in una nuova iterazione).
        AddHandler(TextBox.TextChangedEvent, OnCampoTestoModificato, Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(SelectingItemsControl.SelectionChangedEvent, OnCampoSelezioneModificato, Avalonia.Interactivity.RoutingStrategies.Bubble);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _prontoPerModifiche = true, Avalonia.Threading.DispatcherPriority.Background);

        // Popup "campi obbligatori mancanti" (feedback utente 2026-08-20, punto 15): apre CampiMancantiDialog
        // invece di lasciare solo il banner di testo generico già mostrato da ErroriValidazione.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TestCaseEditorViewModel vm)
                vm.RichiestaMostraCampiMancanti += () =>
                {
                    if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
                    CampiMancantiDialog.Mostra(proprietario, vm.CampiObbligatoriMancanti, VaiAlCampo);
                };
            InstradaComandoSalvaSicuro();
        };
        AttachedToVisualTree += (_, _) => InstradaComandoSalvaSicuro();
    }

    /// <summary>
    /// Il gigantesco XAML storico lega il pulsante principale al SalvaCommand originale. Per non duplicare
    /// 194 KB di markup soltanto per cambiare quel binding, appena la view è materializzata sostituiamo il
    /// comando di qualunque Button che punti ESATTAMENTE al vecchio SalvaCommand con SalvaSicuroCommand.
    /// GetVisualDescendants è l'API Avalonia prevista per attraversare il visual tree; il Post garantisce che
    /// i binding XAML siano già stati applicati prima della sostituzione del valore locale Command.
    /// </summary>
    private void InstradaComandoSalvaSicuro()
    {
        if (DataContext is not TestCaseEditorViewModel vm) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var button in this.GetVisualDescendants().OfType<Button>())
            {
                if (ReferenceEquals(button.Command, vm.SalvaCommand))
                    button.Command = vm.SalvaSicuroCommand;
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Porta in vista la sezione (Pianificazione, Dettagli o Prove) che contiene il campo con questa
    /// chiave CampiSistema e lo evidenzia (Spotlight, stesso meccanismo del tour guidato) — riusa OnVaiSezione
    /// invece di duplicarne la logica di visibilità/BringIntoView.</summary>
    private void VaiAlCampo(string chiave)
    {
        // In creazione (wizard a schede) la sezione target potrebbe non essere quella corrente: senza questo
        // passo BringIntoView non farebbe nulla (sezione non visibile) e il popup sembrerebbe non portare da
        // nessuna parte, tornando solo in cima alla pagina (feedback utente).
        if (DataContext is TestCaseEditorViewModel vmRapido && vmRapido.ModoEditorRapido)
        {
            vmRapido.ApriParametriPerValidazione();
            var nome = chiave switch
            {
                CampiSistema.StatoLavoro or CampiSistema.Scadenza or CampiSistema.Assegnatario => "Pianificazione",
                CampiSistema.OperazioneCondizione => "Prove",
                _ => "Dettagli"
            };
            vmRapido.MostraSezioneRapida(nome);
        }
        if (DataContext is TestCaseEditorViewModel { IsNew: true, ModoEditorRapido: false } vm)
        {
            var schedaTarget = chiave switch
            {
                CampiSistema.StatoLavoro or CampiSistema.Scadenza or CampiSistema.Assegnatario => 0,
                CampiSistema.OperazioneCondizione => 2,
                _ => 1,
            };
            if (vm.SchedaCorrente != schedaTarget) vm.SchedaCorrente = schedaTarget;
        }

        var sezione = chiave switch
        {
            CampiSistema.StatoLavoro or CampiSistema.Scadenza or CampiSistema.Assegnatario => Pianificazione,
            CampiSistema.OperazioneCondizione => Prove,
            _ => Dettagli,
        };
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (sezione.IsVisible) sezione.BringIntoView();
            Spotlight.TargetId = "campo-" + chiave;
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>In un handler in bubbling, "sender" è sempre il punto di aggancio dell'handler (questo
    /// UserControl), non l'elemento che ha davvero sollevato l'evento — quello è "e.Source". Un errore
    /// classico che qui avrebbe reso il fix silenziosamente inerte (sender non è mai un TextBox).</summary>
    private void OnCampoTestoModificato(object? sender, TextChangedEventArgs e)
    {
        if (e.Source is TextBox { IsFocused: true } && DataContext is TestCaseEditorViewModel { PuoModificareContenuto: true } vm)
            vm.ModificheNonSalvate = true;
    }

    /// <summary>Gli eventi SelectionChanged possono essere generati da Avalonia anche durante il semplice
    /// riallineamento di ItemsSource/SelectedItem. La navigazione REV è read-only e non deve mai sporcare il
    /// Test; una Condizione può essere mutata solo quando si sta davvero visualizzando la revisione corrente.</summary>
    private async void OnCampoSelezioneModificato(object? sender, SelectionChangedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciCampoSelezioneModificato(sender, e), "Modifica di un campo (editor Test)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciCampoSelezioneModificato(object? sender, SelectionChangedEventArgs e)
    {
        if (!_prontoPerModifiche || e.Source is not SelectingItemsControl source || DataContext is not TestCaseEditorViewModel vm)
            return;

        // Il refresh periodico sostituisce gli ItemsSource mentre il form può essere aperto. Le ComboBox
        // emettono SelectionChanged anche durante questo riallineamento; sono transizioni di caricamento,
        // non una scelta dell'utente.
        if (vm.RiallineamentoListeMasterInCorso) return;

        // Il ComboBox REV usa il ViewModel come DataContext e ha una Esecuzione come SelectedItem. Cambiare
        // revisione è pura navigazione: nessun dirty flag e soprattutto nessun side-effect di workflow.
        if (source is ComboBox { SelectedItem: Esecuzione } && ReferenceEquals(source.DataContext, vm))
            return;

        // Tutti i controlli tecnici sono editabili esclusivamente sulla REV corrente. Questo guard protegge
        // anche dagli eventi programmati emessi dal framework mentre una REV storica viene ricostruita.
        if (!vm.PuoModificareContenuto)
            return;

        // Il blocco guasto Injection prepara una bozza che viene copiata nel Test solo dal comando Aggiungi.
        // Ricalcolare ItemsSource o applicare i default personali può cambiare SelectedItem senza intervento
        // dell'utente; marcarlo dirty impedirebbe di annullare o navigare via da un Test appena aperto.
        if (source is ComboBox && (AutomationProperties.GetAutomationId(source) ?? string.Empty)
                .StartsWith("Editor_DtcInjection_", StringComparison.Ordinal))
            return;

        // I pulsanti esito Rapido aggiornano il POCO via command, quindi il ComboBox tradizionale
        // emette anche SelectionChanged per il nuovo valore ricevuto dal binding. Questa eco NON
        // è una seconda scelta dell'operatore e non deve riaprire il dialogo/ricreare NC due volte.
        if (source is ComboBox combo && combo.DataContext is Condizione sceltaRapida &&
            (AutomationProperties.GetAutomationId(combo) ?? string.Empty)
                .EndsWith("_Esito_Combo", StringComparison.Ordinal) &&
            (vm.ConsumaEcoEsitoRapido(sceltaRapida) ||
             vm.ECambioEsitoRapidoInCorso(sceltaRapida)))
            return;

        vm.ModificheNonSalvate = true;
        // Il ViewModel ricostruisce il pannello Anomalie sia subito sia dopo l'eventuale dialogo "Cambio esito" (vedi lì).
        if (source.DataContext is Condizione condizione)
            await vm.GestisciCambioEsitoCondizione(condizione);
    }

    /// <summary>La Funzionalita dell'operazione usa intenzionalmente un binding SelectedItem OneWay.
    /// Avalonia puo portare SelectedItem a null mentre inizializza ItemsSource (in particolare quando il
    /// form e disabilitato in sola lettura); con il binding bidirezionale quel null veniva scritto nel
    /// modello e cancellava a video la fotografia storica appena caricata. Solo una selezione concreta
    /// dell'utente viene quindi copiata esplicitamente nella Condizione.</summary>
    private void OnFunzionalitaOperazioneModificata(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string funzionalita, DataContext: Condizione condizione }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (condizione.Funzionalita.Equals(funzionalita, StringComparison.Ordinal)) return;

        condizione.Funzionalita = funzionalita;
        vm.ModificheNonSalvate = true;
        vm.SincronizzaEsitoCondizioni();
    }

    /// <summary>Selettore guasto dell'operazione (6.5.0, punto 7): stesso criterio OneWay della Funzionalita.</summary>
    private void OnDtcOperazioneModificata(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string etichetta, DataContext: Condizione condizione }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (etichetta.Equals(condizione.DtcEtichetta, StringComparison.OrdinalIgnoreCase)) return;
        vm.ImpostaDtcOperazione(condizione, etichetta);
        vm.SincronizzaEsitoCondizioni();
    }

    /// <summary>Codice guasto digitato (strumento guasto Injection): conferma a perdita di focus o Invio; formato non valido = ripristino.</summary>
    private void OnDtcCodiceLiberoCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: Condizione condizione } tb) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (vm.ImpostaDtcCodiceLibero(condizione, tb.Text)) vm.SincronizzaEsitoCondizioni();
        else
        {
            condizione.DtcAvviso = string.Format(Services.LocalizationService.Instance.Traduci("DtcCodiceNonValidoFmt"), tb.Text);
            tb.Text = condizione.Guasto;
        }
    }

    private void OnDtcCodiceLiberoKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) OnDtcCodiceLiberoCommit(sender, new RoutedEventArgs());
    }

    private void OnPassoDtcModificato(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: PassoDtc passo, DataContext: Condizione condizione }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (condizione.DtcPasso == passo) return;
        condizione.DtcPasso = passo;
        vm.ModificheNonSalvate = true;
    }

    private void OnRimuoviDtcOperazione(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Condizione condizione }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        vm.RimuoviDtcOperazione(condizione);
        vm.SincronizzaEsitoCondizioni();
    }

    // ----- Blocco "Aggiungi guasto" (6.5.1, fase 4): modifiche per riga. SelectedItem e OneWay: solo una scelta concreta dell'utente viene scritta. -----
    private void OnDtcRigaEcuModificata(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string componente, DataContext: ViewModels.DtcInseritoRiga riga }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (componente.Equals(riga.Componente, StringComparison.OrdinalIgnoreCase)) return;
        if (vm.CambiaEcuDtcInserito(riga, componente) != null) vm.RiallineaDtcInseriti();
    }

    private void OnDtcRigaRequisitoModificato(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string requisito, DataContext: ViewModels.DtcInseritoRiga riga }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (requisito.Equals(riga.RequisitoEtichetta, StringComparison.OrdinalIgnoreCase)) return;
        if (vm.ImpostaRequisitoDtcInserito(riga, requisito) != null) vm.RiallineaDtcInseriti();
    }

    private void OnDtcRigaCodiceCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ViewModels.DtcInseritoRiga riga } tb) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if ((tb.Text ?? "").Trim().Equals(riga.Codice, StringComparison.OrdinalIgnoreCase)) return;
        if (vm.CambiaCodiceDtcInserito(riga, tb.Text) != null) vm.RiallineaDtcInseriti();
    }

    private void OnDtcRigaCodiceKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) OnDtcRigaCodiceCommit(sender, new RoutedEventArgs());
    }

    private async void OnDtcRigaRimuovi(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciDtcRigaRimuovi(sender, e), "Rimozione riga guasto (editor Test)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciDtcRigaRimuovi(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ViewModels.DtcInseritoRiga riga }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        await vm.RimuoviDtcInseritoConfermaAsync(riga);
    }

    private void OnSelezionaGamma(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string gamma } combo || DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        vm.SelezionaGammaCommand.Execute(gamma);
        combo.SelectedItem = null;
    }

    /// <summary>Menu contestuale "Evidenzia" sui 4 testi liberi di una Condizione (feedback utente 2026-08-24,
    /// punto 8): a differenza di CampoStrumentoValore/RigaEvidenziata questi 4 campi non condividono
    /// un'unica proprietà Colore (sono 4 proprietà distinte sul modello), quindi il Tag del MenuItem porta
    /// "NomeCampo|Colore" invece del solo colore — un handler solo invece di quattro quasi identici.</summary>
    private void OnImpostaColoreCondizione(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Condizione condizione, Tag: string tag }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        var parti = tag.Split('|');
        if (parti.Length != 2 || !Enum.TryParse<ColoreEvidenziazione>(parti[1], out var colore)) return;
        switch (parti[0])
        {
            case "Nota": condizione.ColoreNota = colore; break;
            case "MotivazioneNonApplicabile": condizione.ColoreMotivazioneNonApplicabile = colore; break;
            case "DescrizioneDeviazione": condizione.ColoreDescrizioneDeviazione = colore; break;
            case "DescrizioneProblema": condizione.ColoreDescrizioneProblema = colore; break;
        }
        vm.ModificheNonSalvate = true;
    }

    /// <summary>Stesso menu "Evidenzia", ma per qualunque IEvidenziabile (CampoStrumentoValore PerOperazione o
    /// RigaEvidenziata di un campo ListaTesto): questi espongono già Colore direttamente, un solo Tag basta.</summary>
    private void OnImpostaColoreEvidenziato(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: IEvidenziabile evidenziabile, Tag: string coloreTesto }) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        if (!Enum.TryParse<ColoreEvidenziazione>(coloreTesto, out var colore)) return;
        evidenziabile.Colore = colore;
        vm.ModificheNonSalvate = true;
    }

    /// <summary>Porta la sezione richiesta sotto la barra fissa. I riferimenti sono controlli nominati,
    /// quindi il comando non dipende dal testo tradotto né dall'ordine futuro delle card.</summary>
    /// <summary>Larghezza utile della pagina (senza il padding dello ScrollViewer) al ViewModel: la barra sezioni e le card si adattano.</summary>
    private void AggiornaLarghezzaEditor()
    {
        if (DataContext is not TestCaseEditorViewModel vm || EditorScroll.Bounds.Width <= 0) return;
        vm.AggiornaLarghezzaUtileEditor(EditorScroll.Bounds.Width - EditorScroll.Padding.Left - EditorScroll.Padding.Right);
    }

    /// <summary>Salto alla sezione dai link della barra o dalle voci del menu "Vai a" (Button o MenuItem, con Tag = nome sezione).</summary>
    private void OnVaiSezione(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string nome }) return;
        if (DataContext is TestCaseEditorViewModel vm) vm.MostraSezioneRapida(nome);
        Control? destinazione = nome switch
        {
            nameof(Pianificazione) => Pianificazione,
            nameof(Dettagli) => Dettagli,
            nameof(Prove) => Prove,
            nameof(Allegati) => Allegati,
            nameof(Riferimenti) => Riferimenti,
            nameof(Anomalie) => Anomalie,
            nameof(Revisioni) => Revisioni,
            _ => null
        };
        if (destinazione is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (destinazione.IsVisible) destinazione.BringIntoView();
            }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Sotto soglia, un flyout inline con checkbox a doppio verso (spuntare aggiunge, togliere
    /// rimuove) al posto del dialogo modale — stesso schema di EcuView.OnScegliVeicoli/OperatoriView.
    /// OnScegliGamme (feedback utente 2026-08-21: la regola combobox-pura/pulsante-con-ricerca vale per
    /// qualunque lista, non solo i campi Lista degli strumenti). Sopra soglia resta il dialogo con ricerca
    /// già esistente (SelezionaDialogService), invariato.</summary>
    private void OnScegliRequisito(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (DataContext is not TestCaseEditorViewModel vm) return;
        if (vm.RequisitiDisponibili.Count > 10) { vm.ScegliRequisitoCommand.Execute(null); return; }

        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        if (vm.RequisitiDisponibili.Count == 0)
            panel.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("NessunRequisitoFunzionalitaTesto"), Classes = { "Muted" } });
        foreach (var r in vm.RequisitiDisponibili)
        {
            var check = new CheckBox { Content = $"{r.Id} - {r.Testo}", IsChecked = vm.TestCase.RequisitoId.Contains(r.Id) };
            check.IsCheckedChanged += (_, _) =>
            {
                vm.RequisitoSelezionato = r;
                if (check.IsChecked == true) vm.AggiungiRequisitoCommand.Execute(null);
                else vm.RimuoviRequisitoCommand.Execute(r.Id);
            };
            panel.Children.Add(check);
        }
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>A quale/i componente (tra quelle collegate al test) appartiene questa singola operazione (feedback
    /// utente 2026-08-25, punto 9), stesso schema di OnScegliFunzionalita/EcuView.OnScegliVeicoli: flyout a
    /// checkbox invece di un dialog modale, per non interrompere il flusso a ogni operazione. Il riepilogo sul
    /// pulsante è aggiornato qui direttamente (Condizione non notifica le proprie mutazioni di Componente), stesso
    /// principio già usato da EcuView per "Veicoli associati".</summary>
    private void OnScegliEcuCondizione(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Condizione condizione) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;

        void AggiornaRiepilogo() =>
            button.Content = condizione.Componente.Count == 0 ? LocalizationService.Instance.Traduci("EcuCondizionePlaceholder") : condizione.Componente.ToString();

        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        if (vm.TestCase.Componente.Count == 0)
            panel.Children.Add(new TextBlock { Text = "—", Classes = { "Muted" } });
        foreach (var componente in vm.TestCase.Componente)
        {
            var check = new CheckBox { Content = componente, IsChecked = condizione.Componente.Contains(componente, StringComparison.OrdinalIgnoreCase) };
            check.IsCheckedChanged += (_, _) =>
            {
                // Riassegnato invece di mutato in-place (Add/RemoveAll diretti sulla lista): Condizione.Componente ha
                // un setter con notifica (feedback utente 2026-08-25, video di riproduzione — il pulsante
                // restava sulla dicitura vuota perché una mutazione in-place non passa mai dal setter).
                var aggiornata = new EcuSelection(condizione.Componente);
                if (check.IsChecked == true)
                {
                    if (!aggiornata.Contains(componente, StringComparison.OrdinalIgnoreCase)) aggiornata.Add(componente);
                }
                else aggiornata.RemoveAll(v => v.Equals(componente, StringComparison.OrdinalIgnoreCase));
                condizione.Componente = aggiornata;
                AggiornaRiepilogo();
                vm.ModificheNonSalvate = true;
                vm.SincronizzaEsitoCondizioni();
            };
            panel.Children.Add(check);
        }
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>Stesso schema di OnScegliRequisito.</summary>
    private void OnScegliFunzionalita(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (DataContext is not TestCaseEditorViewModel vm) return;
        if (vm.FunzionalitaDisponibili.Count > 10) { vm.ScegliFunzionalitaCommand.Execute(null); return; }

        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        if (vm.FunzionalitaDisponibili.Count == 0)
            panel.Children.Add(new TextBlock { Text = LocalizationService.Instance.Traduci("NessunRequisitoFunzionalitaTesto"), Classes = { "Muted" } });
        foreach (var nome in vm.FunzionalitaDisponibili)
        {
            var check = new CheckBox { Content = nome, IsChecked = vm.TestCase.Funzionalita.Contains(nome) };
            check.IsCheckedChanged += (_, _) =>
            {
                vm.NuovaFunzionalita = nome;
                if (check.IsChecked == true) vm.AggiungiFunzionalitaCommand.Execute(null);
                else vm.RimuoviFunzionalitaCommand.Execute(nome);
            };
            panel.Children.Add(check);
        }
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>Stesso schema di OnScegliRequisito.</summary>
    private void OnScegliTestCorrelato(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (DataContext is not TestCaseEditorViewModel vm) return;
        if (vm.TestCorrelatiDisponibili.Count > 10) { vm.ScegliTestCorrelatoCommand.Execute(null); return; }

        var panel = new StackPanel { Spacing = 4, MinWidth = 220 };
        if (vm.TestCorrelatiDisponibili.Count == 0)
            panel.Children.Add(new TextBlock { Text = "—", Classes = { "Muted" } });
        foreach (var t in vm.TestCorrelatiDisponibili)
        {
            var check = new CheckBox { Content = $"{t.Componente} · {t.Id}", IsChecked = vm.TestCase.TestCorrelati.Contains(t.Id) };
            check.IsCheckedChanged += (_, _) =>
            {
                vm.TestCorrelatoSelezionato = t;
                if (check.IsChecked == true) vm.AggiungiTestCorrelatoCommand.Execute(null);
                else vm.RimuoviTestCorrelatoCommand.Execute(t.Id);
            };
            panel.Children.Add(check);
        }
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        flyout.ShowAt(button);
    }

    /// <summary>Apre un test correlato in una finestra separata di sola visualizzazione, che resta aperta
    /// accanto a questa (feedback utente 2026-08-21) — non naviga via da qui, il test attualmente in modifica
    /// non viene toccato finché non si chiede esplicitamente di modificare anche l'altro (vedi
    /// TestCaseViewerWindow/TestCaseEditorViewModel.RichiediModificaCorrelato).</summary>
    private void OnApriTestCorrelatoInFinestra(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || DataContext is not TestCaseEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window proprietario) return;
        TestCaseViewerWindow.Apri(proprietario, vm, id);
    }

    /// <summary>Accept only supported, local files, and never modify an editor in read-only mode.
    /// The actual copy and persistence use the existing safe attachment workflow.</summary>
    private void OnAllegatiDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel { TipoTestScelto: true, PuoModificareContenuto: true, TestRichiedeEcu: false } ||
            !e.DataTransfer.Formats.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        e.DragEffects = files is { Count: > 0 and <= 20 } && files.All(file =>
        {
            var path = file.TryGetLocalPath();
            return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) && AttachmentService.EstensioneConsentita(path);
        }) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnAllegatiDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel { TipoTestScelto: true, PuoModificareContenuto: true, TestRichiedeEcu: false } vm ||
            !e.DataTransfer.Formats.Contains(DataFormat.File)) return;
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        if (items is not { Count: > 0 and <= 20 })
        {
            if (items is { Count: > 20 }) vm.SegnalaAllegatiRapidiNonValidi(troppiFile: true);
            return;
        }
        var paths = items.Select(item => item.TryGetLocalPath()).ToList();
        if (paths.Any(p => string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p) ||
                           !AttachmentService.EstensioneConsentita(p)))
        {
            vm.SegnalaAllegatiRapidiNonValidi(troppiFile: false);
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        try
        {
            foreach (var file in paths)
                await vm.AggiungiAllegatoSicuro(file!);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Trascinamento allegati Test", ex);
            await ErroreOperazioneDialogo.MostraAsync(this);
        }
    }

    /// <summary>Explicit clipboard screenshot import through the existing attachment service.
    /// Temporary PNG evidence is never added to the shared workspace directly and is deleted
    /// even when attachment validation or persistence fails.</summary>
    private async void OnIncollaScreenshotAllegato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel { TipoTestScelto: true, PuoModificareContenuto: true, TestRichiedeEcu: false } vm)
            return;
        await IncollaScreenshotAsync(vm, null);
    }

    private static string? ChiaveRiga(Condizione c) => $"Editor_Condizione_{c.Uid}_Riga";

    private Border? TrovaRigaRapida(Condizione condizione) =>
        this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == ChiaveRiga(condizione));

    private static Condizione? CondizioneDi(object? origine) =>
        (origine as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<Condizione>().FirstOrDefault();

    /// <summary>"Prossima da eseguire": scorre fino alla riga e porta il fuoco sul suo pulsante OK, cosi' i tasti 1-4 valgono subito.</summary>
    private void OnProssimaDaEseguire(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm) return;
        var corrente = CondizioneDi(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        var prossima = vm.TrovaProssimaDaEseguire(corrente);
        if (prossima is null) return;
        var riga = TrovaRigaRapida(prossima);
        if (riga is null) return;
        riga.BringIntoView();
        riga.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (AutomationProperties.GetAutomationId(b) ?? "").EndsWith("_QuickOk_Button", StringComparison.Ordinal))?.Focus();
    }

    /// <summary>Frecce su/giu': stesso pulsante della riga precedente/successiva (resta il fuoco sugli esiti, i tasti 1-4 continuano a valere).</summary>
    private bool SpostaFuocoRiga(Condizione corrente, int direzione, Control? sorgente)
    {
        if (DataContext is not TestCaseEditorViewModel vm) return false;
        var righe = this.GetVisualDescendants().OfType<Border>()
            .Select(b => (Id: AutomationProperties.GetAutomationId(b) ?? "", Riga: b))
            .Where(x => x.Id.EndsWith("_Riga", StringComparison.Ordinal)).ToDictionary(x => x.Id, x => x.Riga);
        Border? RigaDi(Condizione c) => righe.GetValueOrDefault(ChiaveRiga(c)!);
        var visibili = vm.CondizioniVisual.Where(c => RigaDi(c) is { IsVisible: true }).ToList();
        var indice = visibili.IndexOf(corrente);
        if (indice < 0) return false;
        var target = visibili.ElementAtOrDefault(indice + direzione);
        if (target is null) return true;
        var riga = RigaDi(target);
        if (riga is null) return true;
        var idSorgente = AutomationProperties.GetAutomationId(sorgente ?? this) ?? "";
        const int prefisso = 18 + 36; // "Editor_Condizione_" + Guid
        var suffisso = idSorgente.StartsWith("Editor_Condizione_", StringComparison.Ordinal) && idSorgente.Length > prefisso ? idSorgente[prefisso..] : "";
        var bottoni = riga.GetVisualDescendants().OfType<Button>().ToList();
        var destinazione = bottoni.FirstOrDefault(b => suffisso.Length > 0 && (AutomationProperties.GetAutomationId(b) ?? "").EndsWith(suffisso, StringComparison.Ordinal))
                           ?? bottoni.FirstOrDefault();
        riga.BringIntoView();
        destinazione?.Focus();
        return true;
    }

    private void OnRigaRapidaDragOver(object? sender, DragEventArgs e)
    {
        if (e.Source is not Avalonia.Visual v || CondizioneDi(v) is not { } condizione ||
            DataContext is not TestCaseEditorViewModel { ModoEditorRapido: true, PuoModificareContenuto: true, TestRichiedeEcu: false } vm ||
            !vm.CondizioniVisual.Contains(condizione) || !e.DataTransfer.Formats.Contains(DataFormat.File))
            return;
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        e.DragEffects = files is { Count: > 0 and <= 20 } && files.All(file =>
        {
            var path = file.TryGetLocalPath();
            return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) && AttachmentService.EstensioneConsentita(path);
        }) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Un file trascinato su una riga diventa evidenza di QUELLA operazione (stesso salvataggio sicuro degli allegati).</summary>
    private async void OnRigaRapidaDrop(object? sender, DragEventArgs e)
    {
        if (e.Source is not Avalonia.Visual v || CondizioneDi(v) is not { } condizione ||
            DataContext is not TestCaseEditorViewModel { ModoEditorRapido: true, PuoModificareContenuto: true, TestRichiedeEcu: false } vm ||
            !vm.CondizioniVisual.Contains(condizione) || !e.DataTransfer.Formats.Contains(DataFormat.File))
            return;
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        e.Handled = true;
        if (items is not { Count: > 0 and <= 20 }) { if (items is { Count: > 20 }) vm.SegnalaAllegatiRapidiNonValidi(troppiFile: true); return; }
        var paths = items.Select(item => item.TryGetLocalPath()).ToList();
        if (paths.Any(p => string.IsNullOrWhiteSpace(p) || !System.IO.File.Exists(p) || !AttachmentService.EstensioneConsentita(p)))
        {
            vm.SegnalaAllegatiRapidiNonValidi(troppiFile: false);
            return;
        }
        try { foreach (var file in paths) await vm.AggiungiEvidenzaOperazioneSicura(condizione, file!); }
        catch (Exception ex)
        {
            CrashLogService.Registra("Trascinamento evidenze operazione", ex);
            await ErroreOperazioneDialogo.MostraAsync(this);
        }
    }

    /// <summary>Scorciatoie della riga rapida: 1 OK, 2 NOK, 3 Dev., 4 N/A, 0 Da eseguire, Ctrl+V screenshot. Valgono solo quando il fuoco
    /// e' sulla riga (non in un campo di testo/lista) e l'editor e' in Rapida e modificabile: la stessa regola dei pulsanti.</summary>
    private void OnTastoRigaRapida(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not TestCaseEditorViewModel { ModoEditorRapido: true, PuoModificareContenuto: true } vm) return;
        if (e.Source is TextBox or ComboBox or CheckBox or Avalonia.Controls.Primitives.ToggleButton { Name: not "Espandi" }) return;
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Control)) return;
        var condizione = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>().Select(c => c.DataContext).OfType<Condizione>().FirstOrDefault();
        if (condizione is null) return;

        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.V)
        {
            if (vm.TestRichiedeEcu) return; // stesso blocco del pulsante "+img": nessun tasto consumato senza effetto
            e.Handled = true;
            _ = IncollaScreenshotAsync(vm, condizione);
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key is Key.Up or Key.Down)
        {
            if (SpostaFuocoRiga(condizione, e.Key == Key.Down ? 1 : -1, e.Source as Control)) e.Handled = true;
            return;
        }
        var comando = e.Key switch
        {
            Key.D1 or Key.NumPad1 => vm.EsitoRapidoConformeCommand,
            Key.D2 or Key.NumPad2 => vm.EsitoRapidoNonConformeCommand,
            Key.D3 or Key.NumPad3 => vm.EsitoRapidoDeviazioneCommand,
            Key.D4 or Key.NumPad4 => vm.EsitoRapidoNonApplicabileCommand,
            Key.D0 or Key.NumPad0 => vm.EsitoRapidoDaEseguireCommand,
            _ => null
        };
        if (comando is null) return;
        e.Handled = true;
        // Con il filtro "solo da eseguire" la riga risposta sparisce: il fuoco passa prima alla riga successiva per non perdere il flusso da tastiera.
        if (vm.SoloDaEseguireRapido) SpostaFuocoRiga(condizione, 1, e.Source as Control);
        _ = comando.ExecuteAsync(condizione);
    }

    /// <summary>Same safe clipboard import, but the saved attachment is also linked to the operation of the clicked card.</summary>
    private async void OnIncollaScreenshotOperazione(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel { TipoTestScelto: true, PuoModificareContenuto: true, TestRichiedeEcu: false } vm ||
            (sender as Control)?.DataContext is not Condizione operazione)
            return;
        await IncollaScreenshotAsync(vm, operazione);
    }

    /// <summary>Native file picker whose selected files become evidence of the clicked operation.</summary>
    private async void OnSceglieFileOperazione(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true, TestRichiedeEcu: false } vm ||
            (sender as Control)?.DataContext is not Condizione operazione ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        try
        {
            var estensioni = AttachmentService.EstensioniConsentite.Select(ext => "*" + ext).ToList();
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("CrScegliAllegatiTitolo"),
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType> { new("Immagini e archivi") { Patterns = estensioni } }
            });
            foreach (var f in file)
            {
                var percorso = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(percorso))
                    await vm.AggiungiEvidenzaOperazioneSicura(operazione, percorso);
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Selezione evidenze operazione", ex); }
    }

    private async Task IncollaScreenshotAsync(TestCaseEditorViewModel vm, Condizione? operazione)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            vm.SegnalaScreenshotNonDisponibile();
            return;
        }
        string? temporaneo = null;
        try
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is null)
            {
                vm.SegnalaScreenshotNonDisponibile();
                return;
            }
            temporaneo = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "testloom-screenshot-" + Guid.NewGuid().ToString("N") + ".png");
            bitmap.Save(temporaneo);
            if (operazione is null) await vm.AggiungiAllegatoSicuro(temporaneo);
            else await vm.AggiungiEvidenzaOperazioneSicura(operazione, temporaneo);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Incolla screenshot Test", ex);
            await ErroreOperazioneDialogo.MostraAsync(this);
        }
        finally
        {
            if (temporaneo is not null)
            {
                try { System.IO.File.Delete(temporaneo); }
                catch (Exception ex) { CrashLogService.Registra("Pulizia screenshot temporaneo Test", ex); }
            }
        }
    }

    /// <summary>Selezione file nativa del sistema operativo al posto del percorso scritto a mano: multipla,
    /// filtrata sulle estensioni ammesse. Il percorso sicuro del ViewModel mantiene un Test nuovo come bozza
    /// locale: aggiungere un'evidenza non crea più implicitamente un JSON condiviso incompleto.</summary>
    private async void OnSceglieFileAllegato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var estensioni = AttachmentService.EstensioniConsentite.Select(ext => "*" + ext).ToList();
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("CrScegliAllegatiTitolo"),
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("Immagini e archivi") { Patterns = estensioni }
                }
            });

            foreach (var f in file)
            {
                var percorso = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(percorso))
                    await vm.AggiungiAllegatoSicuro(percorso);
            }
        }
        catch (Exception ex) { CrashLogService.Registra("Selezione allegati test", ex); }
    }

    /// <summary>Campo strumento di tipo ListaTesto (feedback utente 2026-08-24, punto 8): apre l'editor
    /// dedicato con le righe già presenti, applica il risultato solo se l'operatore conferma (Annulla/chiusura
    /// senza salvare torna null, nessuna modifica).</summary>
    private async void OnModificaListaTesto(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => GestisciModificaListaTesto(sender, e), "Modifica lista di testo (editor Test)",
            mostra: () => ErroreOperazioneDialogo.MostraAsync(this));

    private async Task GestisciModificaListaTesto(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CampoStrumentoValore campo } || TopLevel.GetTopLevel(this) is not Window proprietario) return;
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm) return;
        var risultato = await ListaTestoEditorWindow.Apri(proprietario, campo.RigheListaTesto);
        if (risultato == null) return;
        campo.RigheListaTesto = risultato;
        vm.ModificheNonSalvate = true;
    }

    /// <summary>Campo strumento di tipo File: usa la stessa pipeline di staging sicura degli allegati generici,
    /// evitando il vecchio salvataggio implicito del Test nuovo.</summary>
    private async void OnScegliFileCampoStrumento(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel { PuoModificareContenuto: true } vm
            || sender is not Button button || button.Tag is not CampoStrumentoValore campo) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            var estensioni = AttachmentService.EstensioniConsentite.Select(ext => "*" + ext).ToList();
            var file = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("ScegliFileTitolo"),
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType> { new(LocalizationService.Instance.Traduci("FileConsentitiTitolo")) { Patterns = estensioni } }
            });
            var percorso = file.FirstOrDefault()?.TryGetLocalPath();
            await vm.CollegaFileCampoStrumentoSicuro(campo, percorso);
        }
        catch (Exception ex) { CrashLogService.Registra("Selezione file campo strumento", ex); }
    }

    /// <summary>Apre il menu interattivo "Crea test collegato". Una bozza non può più trasformarsi
    /// implicitamente in un grafo multi-JSON: il wrapper sicuro richiede che il Test padre sia già committato.</summary>
    private async void OnCreaTestCollegato(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not Window proprietario) return;

        await UiExceptionGuard.Esegui(async () =>
        {
            if (!await vm.AssicuraTestSalvatoPerTestCollegato()) return;
            var risultato = await CreaTestCollegatoWindow.Apri(proprietario, vm.TemplatesDisponibili);
            if (risultato != null) vm.CreaTestCollegatiSicuro(risultato.Value.templati, risultato.Value.campi);
        }, "Creazione test collegato");
    }

    /// <summary>Bottone 'Dettaglio' di un'anomalia collegata: apre il dialogo unico di dettaglio (stesso della pagina
    /// Anomalie). Con modifiche non salvate e NC gia persistita offre "Salva e apri".</summary>
    private async void OnApriDettaglioAnomalia(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm) return;
        if (sender is not Button { DataContext: NcRiga riga }) return;
        if (TopLevel.GetTopLevel(this) is not Window proprietario) return;

        await UiExceptionGuard.Esegui(async () =>
        {
            if (!vm.EAnomaliaNonPersistita(riga) && !await vm.AssicuraTestSalvatoPerDettaglioAnomalia()) return;
            var dettaglio = vm.CreaDettaglioAnomalia(riga);
            var eraSporco = vm.ModificheNonSalvate;
            await AnomaliaDettaglioWindow.Apri(proprietario, dettaglio);
            await vm.DopoDettaglioAnomaliaAsync(dettaglio, eraSporco);
        }, "Dettaglio anomalia dal test");
    }

    /// <summary>Prima le sezioni da includere (con anteprima live), poi la cartella di destinazione. L'export
    /// non è più un commit implicito: su Test nuovo usa l'identità della bozza e lascia il JSON condiviso
    /// inesistente fino al Salva esplicito.</summary>
    private async void OnCreaTicket(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        DiagnosticTraceService.Registra("Test.PolarionPackage", "preview requested");
        HashSet<string>? sezioni = null;
        var browserRequested = false;
        var proprietario = TopLevel.GetTopLevel(this) as Window;
        await UiExceptionGuard.Esegui(async () =>
        {
            var dialogo = PolarionSelezioneDialog.Crea(vm.SezioniPolarionDisponibili(), vm.SezioniPolarionDiDefault(), vm.EtichettaSezionePolarion, vm.GeneraAnteprimaPolarion);
            sezioni = proprietario != null ? await dialogo.ShowDialog<HashSet<string>?>(proprietario) : null;
            browserRequested = dialogo.BrowserHandoffRequested;
        }, "Apertura selezione pacchetto Ticketing");
        if (sezioni == null)
        {
            DiagnosticTraceService.Registra("Test.PolarionPackage", "preview canceled");
            return;
        }
        if (browserRequested)
        {
            DiagnosticTraceService.Registra("Test.PolarionPackage",
                "browser TICKET requested sections=" + sezioni.Count);
            if (proprietario is not null)
                await PreparaEstaDaTestAsync(vm, proprietario, vm.GeneraAnteprimaPolarion(sezioni));
            return;
        }

        try
        {
            var cartelle = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance.Traduci("PolarionScegliDoveSalvareTitolo"),
                AllowMultiple = false
            });

            var percorso = cartelle.FirstOrDefault()?.TryGetLocalPath();
            var richiesta = !string.IsNullOrEmpty(percorso) ? new RichiestaPacchettoPolarion(percorso, sezioni) : null;
            if (richiesta != null)
            {
                DiagnosticTraceService.Registra("Test.PolarionPackage", "export requested sections=" + sezioni.Count);
                await vm.CreaTicketSicuro(richiesta);
                DiagnosticTraceService.Registra("Test.PolarionPackage", "export finished");
            }
            else
                DiagnosticTraceService.Registra("Test.PolarionPackage", "folder selection canceled");
        }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("Test.PolarionPackage", "export failed exception=" + ex.GetType().Name);
            CrashLogService.Registra("Selezione destinazione pacchetto Ticketing", ex);
        }
    }

    /// <summary>
    /// Same TICKET creation dialog as NC/CR; the Test never creates a third, orphan
    /// ticket. Requirements on Ticketing remain unaffected and are not treated as TICKET.
    /// </summary>
    private async void OnPreparaEstaDaTest(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await PreparaEstaDaTestAsync(vm, owner);
    }

    private async Task PreparaEstaDaTestAsync(
        TestCaseEditorViewModel vm, Window owner, string? selectedTestContext = null)
    {
        var state = vm.PolarionHandoffState;
        if (!state.CartellaValida || !state.PuoScrivere || state.OperatoreCorrente is null)
        {
            DiagnosticTraceService.Registra("Test.TICKET", "browser-handoff blocked workspace-readonly");
            return;
        }
        try
        {
            var sources = PolarionTestSourceResolver.Find(vm.TestCase,
                state.NonConformita.PeekAllReadOnly(), state.ChangeRequests.PeekAllReadOnly());
            if (sources.Count == 0)
            {
                // No TICKET should be generated just because a Ticketing REQUIREMENT is linked.
                await ShowEstaTestMessage(owner,
                    "Questo Test non ha NC/CR associate. Per creare un TICKET, collega prima una NC o CR. " +
                    "I requisiti Ticketing restano riferimenti distinti e non vengono convertiti in TICKET.");
                DiagnosticTraceService.Registra("Test.TICKET", "browser-handoff no-nc-cr-source");
                return;
            }
            PolarionTestSourceResolver.Source? selected = sources.Count == 1 ? sources[0] : null;
            if (selected is null)
            {
                DiagnosticTraceService.Registra("Test.TICKET", "multiple-sources count=" + sources.Count);
                var chooser = new Window
                {
                    Title = "Seleziona NC/CR per il nuovo TICKET", Width = 570, Height = 225,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                UiInteractionTraceService.Attach(chooser, "Test.TICKET.SourcePicker");
                var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 12 };
                panel.Children.Add(new TextBlock
                {
                    Text = "Seleziona l'elemento TestLoom da collegare al ticket. Nessun ticket verrà creato nel Test.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
                var options = new ComboBox { ItemsSource = sources, SelectedIndex = 0 };
                Avalonia.Automation.AutomationProperties.SetAutomationId(options, "Test_Ticket_SelectSource");
                panel.Children.Add(options);
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                var cancel = new Button { Content = "Annulla", Classes = { "Secondary" } };
                var proceed = new Button { Content = "Continua", Classes = { "Primary" } };
                cancel.Click += (_, _) => chooser.Close(null);
                proceed.Click += (_, _) => chooser.Close(options.SelectedItem as PolarionTestSourceResolver.Source);
                row.Children.Add(cancel); row.Children.Add(proceed); panel.Children.Add(row);
                chooser.Content = panel;
                selected = await chooser.ShowDialog<PolarionTestSourceResolver.Source?>(owner);
            }
            if (selected is null)
            {
                DiagnosticTraceService.Registra("Test.TICKET", "source-selection canceled");
                return;
            }
            DiagnosticTraceService.Registra("Test.TICKET", "handoff-open source-kind=" +
                (selected.IsCr ? "CR" : "NC") + " source-id=" + selected.Id);
            if (selected.Nc is not null)
                await PolarionCreateTicketDialog.OpenNcAsync(owner, state, selected.Nc, vm.TestCase,
                    selectedTestContext);
            else if (selected.Cr is not null)
                await PolarionCreateTicketDialog.OpenCrAsync(owner, state, selected.Cr,
                    selectedTestContext, vm.TestCase);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Preparazione TICKET dal Test", ex);
            await ShowEstaTestMessage(owner, "Impossibile aprire la preparazione TICKET. Consultare il log diagnostico.");
        }
        finally
        {
            // The browser dialog may have written a local, crash-safe handoff receipt.
            // Refresh the presentation only; never assume the external TICKET was saved.
            vm.AggiornaStatoEstaLocale();
        }
    }

    private static async Task ShowEstaTestMessage(Window owner, string text)
    {
        var dialog = new Window
        {
            Title = "TICKET / Ticketing", Width = 550, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        UiInteractionTraceService.Attach(dialog, "Test.TICKET.Message");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(18), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(owner);
    }

    /// <summary>Campi di uno strumento gestibili anche da dentro il test (Addendum §8, feedback QA), non solo
    /// da Impostazioni → Strumenti: stesso editor a tabella di StrumentiView, salvato subito sui master data
    /// alla chiusura della finestra (vedi TestCaseEditorViewModel.SalvaCampiStrumento).</summary>
    /// <summary>Lista virtualizzata degli strumenti (> 10 a catalogo, perf 2026-09-25 bis): lo stato di
    /// selezione vive solo in StrumentoOption.Selezionato (CheckBox in binding), quindi la selezione della
    /// ListBox non ha significato e viene azzerata subito, così un click sulla riga non la evidenzia senza
    /// spuntare l'opzione (stesso schema di FiltroMultiSelectVirtualizzato, commit 58b6250).</summary>
    private void OnListaStrumentiSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem != null) lb.SelectedItem = null;
    }

    private async void OnGestisciCampiStrumento(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not TestCaseEditorViewModel vm
            || sender is not Button button || button.Tag is not string nomeStrumento) return;
        var strumento = vm.TrovaStrumento(nomeStrumento);
        if (strumento == null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        await UiExceptionGuard.Esegui(
            () => CampiStrumentoEditorWindow.Apri(owner, strumento, vm.SalvaCampiStrumento,
                vm.PuoModificareContenuto ? vm.VociVistaDi(nomeStrumento) : null, vm.PuoGestireStrumenti),
            "Apertura campi strumento dal test");
    }
}
