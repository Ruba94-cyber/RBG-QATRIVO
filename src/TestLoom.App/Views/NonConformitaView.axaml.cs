using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views.Controls;

namespace TestLoom.App.Views;

public partial class NonConformitaView : UserControl
{
    public NonConformitaView()
    {
        InitializeComponent();
        CollegaFiltroData(NcDataAperturaDa, nameof(NonConformitaViewModel.DataAperturaDa));
        CollegaFiltroData(NcDataAperturaA, nameof(NonConformitaViewModel.DataAperturaA));
        CollegaFiltroData(NcDataChiusuraDa, nameof(NonConformitaViewModel.DataChiusuraDa));
        CollegaFiltroData(NcDataChiusuraA, nameof(NonConformitaViewModel.DataChiusuraA));
        CollegaFiltroData(CrDataAperturaDa, nameof(NonConformitaViewModel.DataAperturaDa));
        CollegaFiltroData(CrDataAperturaA, nameof(NonConformitaViewModel.DataAperturaA));
        CollegaFiltroData(CrDataChiusuraDa, nameof(NonConformitaViewModel.DataChiusuraDa));
        CollegaFiltroData(CrDataChiusuraA, nameof(NonConformitaViewModel.DataChiusuraA));
        DataContextChanged += (_, _) => { CollegaRiepilogo(); CollegaRichiesteDettaglioCr(); };
        // Lightweight TICKET table cells read the shared cache off the UI thread.
        // Subscribe only while the NC/CR page is visible; no static-event leaks.
        AttachedToVisualTree += (_, _) => PolarionGridLifecycleConverter.CacheUpdated += OnEstaCacheUpdated;
        DetachedFromVisualTree += (_, _) => PolarionGridLifecycleConverter.CacheUpdated -= OnEstaCacheUpdated;
        // Il modo di layout (Compatto/Standard) dipende dalla finestra: al cambio di dimensione il riepilogo si ricalcola.
        SizeChanged += (_, _) => AggiornaRiepilogo();
        // Maniglia tra anomalie e CR: il rapporto trascinato si salva per operatore; doppio clic: torna 50/50.
        SplitterSchede.DragStarted += (_, _) => IniziaTrascinamentoSchede();
        SplitterSchede.DragCompleted += (_, _) => ConcludiTrascinamentoSchede();
        SplitterSchede.AddHandler(InputElement.DoubleTappedEvent,
            (_, _) => RipristinaRapportoPredefinito(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnEstaCacheUpdated()
    {
        if (DataContext is NonConformitaViewModel vm)
            vm.NotificaAggiornamentoCacheEsta();
    }

    /// <summary>Filtri di data (CalendarDatePicker): un testo digitato non valido viene segnalato al ViewModel, che azzera
    /// il vincolo del campo e mostra il messaggio sotto i filtri. La segnalazione si toglie al tasto premuto o all'uscita dal campo.</summary>
    private void CollegaFiltroData(Avalonia.Controls.CalendarDatePicker picker, string campo) =>
        FiltroDataCalendario.Collega(picker,
            () => { if (DataContext is NonConformitaViewModel vm) vm.SegnalaDataNonValida(campo); },
            () => { if (DataContext is NonConformitaViewModel vm) vm.AzzeraDataNonValida(campo); });

    // Nota (fix review 2026-09-25, aggiornata con il dialogo Dettaglio): il ricablaggio a runtime dei comandi grezzi verso le varianti "Sicure"
    // (InstradaComandiStateSafety, introdotto in 23604c4 perche' lo XAML storico legava ancora ai comandi
    // pre-hardening) e' stato rimosso. Con la lista Righe virtualizzata (ListBox + VirtualizingStackPanel) i
    // container venivano creati/riciclati dopo la prima passata (scroll, filtro, riga espansa con editor lazy)
    // e restavano quindi agganciati ai comandi grezzi: bug critico, non solo di stile. Lo XAML ora lega
    // direttamente ChiudiManualmenteSicuroCommand/RiapriAnomaliaSicuroCommand/EliminaAnomaliaSicuroCommand/
    // ClassificaSegnalazioneSicuroCommand/EscludiSegnalazioneSicuroCommand (stessi parametri e stessa
    // IsEnabled/IsVisible di prima), quindi ogni container - realizzato subito o dopo scroll/riciclo - usa
    // sempre e solo i percorsi fail-closed, senza bisogno di alcun ricablaggio.

    private static readonly Dictionary<string, string> ChiaviTitoloColonna = new()
    {
        ["Id"] = "ColId", ["Stato"] = "ColStatoAnomalia", ["Priorita"] = "ColPriorita", ["Gravita"] = "ColGravita",
        ["Assegnatario"] = "ColAssegnatario", ["Apertura"] = "ColApertura", ["Chiusura"] = "ColChiusura",
        ["Descrizione"] = "ColDescrizioneLunga", ["TestCollegato"] = "ColTestCollegato", ["DescrizioneBreve"] = "ColDescrizioneBreve",
        ["Ticket"] = "ColTicketNc", ["Veicolo"] = "ColVeicoliAssociati",
    };

    private NonConformitaViewModel? _vmCollegato;
    private NonConformitaViewModel? _vmDettaglioCr;

    private void CollegaRichiesteDettaglioCr()
    {
        if (DataContext is not NonConformitaViewModel vm || ReferenceEquals(_vmDettaglioCr, vm)) return;
        if (_vmDettaglioCr != null) _vmDettaglioCr.DettaglioChangeRequestRichiesto -= OnDettaglioChangeRequestRichiesto;
        _vmDettaglioCr = vm;
        vm.DettaglioChangeRequestRichiesto += OnDettaglioChangeRequestRichiesto;
    }

    private async void OnDettaglioChangeRequestRichiesto(ChangeRequest riga)
    {
        if (DataContext is not NonConformitaViewModel vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        await UiExceptionGuard.Esegui(async () =>
        {
            var cr = vm.LeggiDettaglioChangeRequest(riga);
            if (cr == null) return;
            var modifica = await CreaFinestraDettaglioCr(cr, vm.PuoModificareChangeRequest(cr)).ShowDialog<bool>(owner);
            if (modifica) vm.ModificaChangeRequestCommand.Execute(cr);
        }, "Dettagli Change Request");
    }

    /// <summary>L'ordinamento avviene sull'intero risultato (indice SQLite), non sulla pagina visibile: l'evento
    /// Sorting della DataGrid viene quindi inoltrato al ViewModel e marcato gestito, cosi' la griglia non riordina
    /// localmente le sole 250 righe caricate.</summary>
    private void OnOrdinaColonna(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (DataContext is NonConformitaViewModel vm && Enum.TryParse<AnomalieOrdinamentoChiave>(e.Column.SortMemberPath, out var chiave))
            vm.ImpostaOrdinamento(chiave);
    }

    private void OnGrigliaCaricata(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NonConformitaViewModel vm && !ReferenceEquals(_vmCollegato, vm))
        {
            if (_vmCollegato != null) _vmCollegato.PropertyChanged -= OnVmProprietaCambiata;
            _vmCollegato = vm;
            vm.PropertyChanged += OnVmProprietaCambiata;
        }
        AggiornaIntestazioni();
    }

    private void OnVmProprietaCambiata(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NonConformitaViewModel.Ordinamento)) AggiornaIntestazioni();
        else if (e.PropertyName is nameof(NonConformitaViewModel.OrdinamentoCrChiave) or nameof(NonConformitaViewModel.OrdinamentoCrDecrescente))
            AggiornaIntestazioniCr();
    }

    /// <summary>TICKET lifecycle analytics hosted from the NC/CR tables, not the general Report page.</summary>
    private async Task ApriAnalisiEstaAsync(string scope, string? selectedDitmId = null)
    {
        if (DataContext is not NonConformitaViewModel vm || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var lifecycle = new PolarionLifecycleViewModel(vm.PolarionState, scope, selectedDitmId);
        var dialog = new Window
        {
            Title = lifecycle.DashboardTitle,
            Width = 1370, Height = 820, MinWidth = 860, MinHeight = 510,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new PolarionLifecycleDashboardView { DataContext = lifecycle }
        };
        AutomationProperties.SetAutomationId(dialog,
            scope == "NC" ? "Anomalie_EstaDashboard_Window" : "Anomalie_Cr_EstaDashboard_Window");
        dialog.Opened += (_, _) => lifecycle.AggiornaReportPolarionCommand.Execute(null);
        try { await dialog.ShowDialog(owner); }
        finally
        {
            lifecycle.Dispose();
            PolarionGridLifecycleConverter.Invalidate(); // refresh in-table status after manual dashboard refresh
        }
    }

    private async void OnApriAnalisiEstaNc(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => ApriAnalisiEstaAsync("NC"), "Analisi TICKET NC");

    private async void OnApriAnalisiEstaCr(object? sender, RoutedEventArgs e) =>
        await UiExceptionGuard.Esegui(() => ApriAnalisiEstaAsync("CR"), "Analisi TICKET CR");

    private async void OnApriTimelineEstaNc(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NcRiga riga }) return;
        await UiExceptionGuard.Esegui(() => ApriAnalisiEstaAsync("NC", riga.Nc.Id), "Timeline TICKET NC");
    }

    private async void OnApriTimelineEstaCr(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChangeRequest cr }) return;
        await UiExceptionGuard.Esegui(() => ApriAnalisiEstaAsync("CR", cr.Id), "Timeline TICKET CR");
    }

    /// <summary>Apre il ticket Ticketing associato alla NC nel browser predefinito.</summary>
    private void OnApriTicketPolarion(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TestLoom.App.Models.CollegamentoPolarion ticket }) return;
        ApriTicketWeb(ticket.Url);
    }

    private void OnApriTicketChangeRequest(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TicketRif ticket }) return;
        ApriTicketWeb(ticket.Url);
    }

    private void ApriTicketWeb(string? url)
    {
        if (!UrlWebValidoConverter.IsWebUrl(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url!) { UseShellExecute = true });
            if (DataContext is NonConformitaViewModel vm) vm.MessaggioErrore = null;
        }
        catch
        {
            if (DataContext is NonConformitaViewModel vm) vm.MessaggioErrore = LocalizationService.Instance.Traduci("ApriPolarionErrore");
        }
    }

    /// <summary>Titolo localizzato + freccia della colonna ordinata (stringa: il menu colonne di
    /// TableLayoutPreferences usa Header.ToString()).</summary>
    private void AggiornaIntestazioni()
    {
        if (DataContext is NonConformitaViewModel vm)
        {
            foreach (var colonna in AnomalieGrid.Columns)
            {
                var chiave = colonna.SortMemberPath;
                if (string.IsNullOrEmpty(chiave) || !ChiaviTitoloColonna.TryGetValue(chiave, out var chiaveTesto)) continue;
                var titolo = LocalizationService.Instance.Traduci(chiaveTesto);
                if (Enum.TryParse<AnomalieOrdinamentoChiave>(chiave, out var k) && k == vm.OrdinamentoChiave)
                    titolo += vm.OrdinamentoDecrescente ? " ▼" : " ▲";
                colonna.Header = titolo;
            }
        }
        // Colonne non ordinabili (titoli localizzati).
        foreach (var colonna in AnomalieGrid.Columns)
        {
            if (!string.IsNullOrEmpty(colonna.SortMemberPath)) continue;
            var h = colonna.Header?.ToString();
            if (h == "Test") colonna.Header = LocalizationService.Instance.Traduci("ColTestCollegato");
            else if (h == "Ticket") colonna.Header = LocalizationService.Instance.Traduci("ColTicketNc");
        }
        AggiornaIntestazioniCr();
    }

    private void OnGrigliaCrCaricata(object? sender, RoutedEventArgs e) => AggiornaIntestazioniCr();

    private static readonly Dictionary<string, string> ChiaviTitoloColonnaCr = new()
    {
        ["Id"] = "ColId", ["Titolo"] = "CrEdTitoloLabel", ["DescrizioneBreve"] = "ColDescrizioneBreve", ["Descrizione"] = "ColDescrizioneLunga",
        ["Ticket"] = "ColTicketNc", ["Stato"] = "ColStato", ["Veicolo"] = "ColVeicoliAssociati", ["Apertura"] = "ColApertura",
        ["Implementazione"] = "ColImplementazione", ["Chiusura"] = "ColChiusura",
    };

    /// <summary>Come per le Anomalie, l'ordinamento riguarda l'intero risultato filtrato: l'evento Sorting va al ViewModel e la griglia
    /// non riordina localmente la sola pagina caricata.</summary>
    private void OnOrdinaColonnaCr(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (DataContext is NonConformitaViewModel vm && Enum.TryParse<ChangeRequestOrdinamentoChiave>(e.Column.SortMemberPath, out var chiave))
            vm.ImpostaOrdinamentoCr(chiave);
    }

    /// <summary>Titolo localizzato + freccia della colonna ordinata; le colonne senza chiave di ordinamento sono quella delle azioni.</summary>
    private void AggiornaIntestazioniCr()
    {
        var vm = DataContext as NonConformitaViewModel;
        foreach (var colonna in ListaCr.Columns)
        {
            var chiave = colonna.SortMemberPath;
            if (string.IsNullOrEmpty(chiave))
            {
                // Solo la colonna delle azioni (Tag) e' rinominata: le colonne TICKET non ordinabili tengono la propria intestazione.
                if (colonna.Tag is "Azioni") colonna.Header = LocalizationService.Instance.Traduci("ColonnaAzioni");
                continue;
            }
            if (!ChiaviTitoloColonnaCr.TryGetValue(chiave, out var chiaveTesto)) continue;
            var titolo = LocalizationService.Instance.Traduci(chiaveTesto);
            if (vm != null && Enum.TryParse<ChangeRequestOrdinamentoChiave>(chiave, out var k) && k == vm.OrdinamentoCrChiave)
                titolo += vm.OrdinamentoCrDecrescente ? " ▼" : " ▲";
            colonna.Header = titolo;
        }
    }

    /// <summary>Bottone 'Dettaglio' di riga: apre il dialogo unico di dettaglio anomalia e, se ha scritto
    /// qualcosa, riallinea la pagina.</summary>
    private async void OnApriDettaglio(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NonConformitaViewModel vm) return;
        if (sender is not Button { DataContext: NcRiga riga }) return;
        if (TopLevel.GetTopLevel(this) is not Window proprietario) return;

        await UiExceptionGuard.Esegui(async () =>
        {
            var modificato = await AnomaliaDettaglioWindow.Apri(proprietario, vm.CreaDettaglio(riga));
            if (modificato) vm.RicaricaDopoDettaglio();
        }, "Dettaglio anomalia");
    }

    private async void OnApriDettaglioSolaLettura(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NonConformitaViewModel vm || sender is not Button { DataContext: NcRiga riga } ||
            TopLevel.GetTopLevel(this) is not Window proprietario) return;

        // Una NC nata da segnalazione di rete si apre nel suo editor completo (come alla creazione), prima in sola lettura.
        if (riga.Nc.DaSegnalazioneRete && vm.ApriSegnalazioneReteSolaLettura is { } apriSegnalazione)
        {
            apriSegnalazione(riga.Nc.Id);
            return;
        }

        await UiExceptionGuard.Esegui(async () =>
        {
            var modificato = await AnomaliaDettaglioWindow.Apri(proprietario,
                vm.CreaDettaglio(riga, solaLettura: true, abilitaModificaDaDettaglio: true));
            if (modificato) vm.RicaricaDopoDettaglio();
        }, "Apertura in sola lettura dell'anomalia");
    }

    private async void OnModificaRapidaNc(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NonConformitaViewModel vm || sender is not Button { DataContext: NcRiga riga } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await UiExceptionGuard.Esegui(async () =>
        {
            var detail = vm.CreaDettaglio(riga);
            switch (await ApriModificaRapidaNc(owner, detail, () => vm.CreaDettaglio(riga), vm.PolarionPaths, vm.PolarionOperator, vm.PolarionState))
            {
                case EsitoModificaRapidaNc.Modificata:
                    vm.RicaricaDopoDettaglio();
                    break;
                case EsitoModificaRapidaNc.ModificaSegnalazione:
                    vm.ModificaSegnalazioneReteCommand.Execute(riga);
                    break;
            }
        }, "Modifica rapida NC");
    }

    private static async Task<EsitoModificaRapidaNc> ApriModificaRapidaNc(Window owner, AnomaliaDettaglioViewModel vm, Func<AnomaliaDettaglioViewModel> creaVm, DataPaths polarionPaths, string polarionOperator, AppState polarionState)
    {
        var loc = LocalizationService.Instance;
        var host = new ContentControl();
        var window = new Window
        {
            Title = loc.Scegli("Modifica rapida anomalia", "Quick anomaly edit", "Modification rapide de l’anomalie") + " · " + vm.Nc.Id,
            Width = 560,
            MinWidth = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = true,
            Content = new ScrollViewer { MaxHeight = 640, Content = host }
        };
        AutomationProperties.SetAutomationId(window, "Anomalie_NcModificaRapida_Window");
        var esito = EsitoModificaRapidaNc.Annullata;
        var completion = new TaskCompletionSource<EsitoModificaRapidaNc>();
        System.ComponentModel.PropertyChangedEventHandler? handler = null;
        var vmAttuale = vm;

        Control Costruisci(AnomaliaDettaglioViewModel v)
        {
            var errore = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = v.HaErrore, Text = v.Errore ?? "" };
            AutomationProperties.SetAutomationId(errore, "Anomalie_NcModificaRapida_Errore_Text");

            var stato = CreaComboRapida(v.StatiWorkflow.Cast<StatoAnomalia>()
                .Select(value => new OpzioneRapida<StatoAnomalia>(value, value.Label())).ToList(),
                v.Nc.StatoWorkflow, "Anomalie_NcModificaRapida_Stato_Combo");
            stato.IsEnabled = v.Aperta && v.PuoModificare;

            var severitaVoci = new List<OpzioneRapida<SeveritaAnomalia?>>
            {
                new(null, loc.Scegli("Non impostata", "Not set", "Non définie"))
            };
            severitaVoci.AddRange(Enum.GetValues<SeveritaAnomalia>()
                .Select(value => new OpzioneRapida<SeveritaAnomalia?>(value, value.Label())));
            var severita = CreaComboRapida(severitaVoci, v.Nc.Severita, "Anomalie_NcModificaRapida_Severita_Combo");
            severita.IsEnabled = v.PuoModificare;

            var prioritaVoci = Enum.GetValues<PrioritaOperativa>()
                .Select(value => new OpzioneRapida<PrioritaOperativa>(value, value.Label())).ToList();
            var priorita = CreaComboRapida(prioritaVoci, v.Nc.Priorita, "Anomalie_NcModificaRapida_Priorita_Combo");
            priorita.IsEnabled = v.EMaster && v.PuoModificare;

            var descrizione = new TextBox
            {
                Text = v.Nc.Descrizione,
                IsReadOnly = true,
                MaxLines = 3,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            ToolTip.SetTip(descrizione, v.Nc.Descrizione);
            AutomationProperties.SetAutomationId(descrizione, "Anomalie_NcModificaRapida_Descrizione_TextBox");

            var ownerBox = new TextBox { Text = v.Nc.Owner, IsEnabled = v.PuoModificare };
            AutomationProperties.SetAutomationId(ownerBox, "Anomalie_NcModificaRapida_Owner_TextBox");

            var opzioniDestinatario = new List<OpzioneRapida<DestinatarioSegnalazione?>> { new(null, "—") };
            opzioniDestinatario.AddRange(v.Destinatari.Cast<DestinatarioSegnalazione>()
                .Select(value => new OpzioneRapida<DestinatarioSegnalazione?>(value, value.Label())));
            var destinatario = CreaComboRapida<DestinatarioSegnalazione?>(opzioniDestinatario,
                v.Nc.Destinatario, "Anomalie_Nc_ModificaRapida_Destinatario_Combo");
            destinatario.IsEnabled = v.PuoModificare;

            var motivo = new TextBox
            {
                Text = v.MotivoChiusura,
                PlaceholderText = loc.Scegli("Motivo di chiusura obbligatorio", "Closure reason required", "Motif de clôture obligatoire"),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 72,
                VerticalContentAlignment = VerticalAlignment.Top,
                IsEnabled = v.PuoChiudere
            };
            var motivoPanel = new StackPanel { Spacing = 4, IsVisible = v.PuoChiudere };
            motivoPanel.Children.Add(new TextBlock { Text = loc.Traduci("MotivoChiusuraLabel"), FontWeight = FontWeight.SemiBold });
            motivoPanel.Children.Add(motivo);
            AutomationProperties.SetAutomationId(motivo, "Anomalie_NcStato_Motivo_TextBox");
            AutomationProperties.SetAutomationId(motivoPanel.Children[0], "Anomalie_NcStato_Motivo_Label");

            var motivoRiapertura = new TextBox
            {
                Text = v.MotivoRiapertura,
                PlaceholderText = loc.Scegli("Motivo della riapertura (obbligatorio)", "Reopen reason (required)", "Motif de réouverture (obligatoire)"),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 72,
                VerticalContentAlignment = VerticalAlignment.Top,
                IsEnabled = v.PuoRiaprire
            };
            var motivoRiaperturaPanel = new StackPanel { Spacing = 4, IsVisible = v.PuoRiaprire };
            motivoRiaperturaPanel.Children.Add(new TextBlock { Text = loc.Scegli("Motivo della riapertura (obbligatorio)", "Reopen reason (required)", "Motif de réouverture (obligatoire)"), FontWeight = FontWeight.SemiBold });
            motivoRiaperturaPanel.Children.Add(motivoRiapertura);
            AutomationProperties.SetAutomationId(motivoRiapertura, "Anomalie_Nc_ModificaRapida_MotivoRiapertura_TextBox");

            var bannerConflitto = new StackPanel { Spacing = 6, IsVisible = v.ConflittoInAttesa };
            var testoConflitto = new TextBlock { Text = v.Errore ?? "", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
            var ricarica = new Button { Content = loc.Scegli("Ricarica", "Reload", "Recharger"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(ricarica, "Anomalie_Nc_ModificaRapida_Ricarica_Button");
            bannerConflitto.Children.Add(testoConflitto);
            bannerConflitto.Children.Add(ricarica);

            var solaLettura = new TextBlock
            {
                Text = loc.Scegli("Sola lettura: non hai i permessi per modificare questa anomalia.", "Read-only: you do not have permission to modify this anomaly.", "Lecture seule : vous n’avez pas les droits pour modifier cette anomalie."),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                IsVisible = !v.PuoModificare
            };

            var annulla = new Button { Content = loc.Traduci("Annulla"), Classes = { "Secondary" } };
            var modificaSegnalazione = new Button
            {
                Content = loc.Traduci("SegnalazioneReteAzione"),
                Classes = { "Secondary" },
                IsVisible = v.Nc.DaSegnalazioneRete
            };
            var riapri = new Button { Content = loc.Traduci("RiapriAnomaliaBtn"), Classes = { "Secondary" }, IsVisible = v.PuoRiaprire, IsEnabled = v.PuoRiaprire };
            var chiudi = new Button { Content = loc.Traduci("ChiudiAnomaliaBtn"), Classes = { "Danger" }, IsVisible = v.PuoChiudere, IsEnabled = v.PuoChiudere };
            var salva = new Button { Content = loc.Traduci("SalvaModificheBtn"), Classes = { "Primary" }, IsEnabled = v.PuoModificare };
            AutomationProperties.SetAutomationId(annulla, "Anomalie_NcModificaRapida_Annulla_Button");
            AutomationProperties.SetAutomationId(modificaSegnalazione, "Anomalie_NcModificaRapida_ModificaSegnalazione_Button");
            AutomationProperties.SetAutomationId(riapri, "Anomalie_NcStato_Riapri_Button");
            AutomationProperties.SetAutomationId(chiudi, "Anomalie_NcStato_Chiudi_Button");
            AutomationProperties.SetAutomationId(salva, "Anomalie_NcModificaRapida_Salva_Button");

            var azioni = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            azioni.Children.Add(annulla);
            azioni.Children.Add(modificaSegnalazione);
            azioni.Children.Add(riapri);
            azioni.Children.Add(chiudi);
            azioni.Children.Add(salva);

            var titolo = new TextBlock
            {
                Text = loc.Scegli("Modifica rapida", "Quick edit", "Modification rapide") + " · " + v.Nc.Id,
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            ToolTip.SetTip(titolo, loc.Scegli("Modifica rapida", "Quick edit", "Modification rapide") + " · " + v.Nc.Id);

            var contenuto = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
            contenuto.Children.Add(titolo);
            AggiungiCampoRapido(contenuto, loc.Traduci("WorkflowLabel"), stato);
            AggiungiCampoRapido(contenuto, loc.Traduci("SeveritaObblLabel"), severita);
            AggiungiCampoRapido(contenuto, loc.Traduci("PrioritaLabel"), priorita);
            AggiungiCampoRapido(contenuto, loc.Traduci("OwnerLabel"), ownerBox);
            AggiungiCampoRapido(contenuto, loc.Scegli("Destinatario", "Recipient", "Destinataire"), destinatario);
            AggiungiCampoRapido(contenuto, loc.Traduci("ColDescrizioneBreve"), descrizione);
            var estaLinks = v.Nc.CollegamentiPolarion.Select(x => (Id: x.Codice, Url: x.Url)).ToList();
            if (estaLinks.Count == 0 && v.TestCollegato != null)
                estaLinks.AddRange(v.TestCollegato.TicketCollegati.Select(x => (Id: x.Riferimento, Url: x.Url)));
            if (estaLinks.Count > 0)
                contenuto.Children.Add(PolarionQuickEditPanel.Create(polarionPaths, estaLinks, window, polarionOperator, isCr: false, testloomClosed: v.Chiusa, operatorCanWrite: polarionState.PuoScrivere,
                    operatorCanWriteNow: () => polarionState.PuoScrivere && polarionState.OperatoreCorrente is not null &&
                        string.Equals(polarionState.Config.OperatoreAttivo, polarionOperator, StringComparison.OrdinalIgnoreCase)));
            if (polarionState.PuoScrivere && v.Persistita)
            {
                var prepareEsta = new Button { Content = "Prepara / Crea ticket TICKET", Classes = { "Secondary" } };
                AutomationProperties.SetAutomationId(prepareEsta, "PolarionCreate_Nc_Preview_Button");
                prepareEsta.Click += async (_, _) =>
                {
                    if (v.HaModificheNonSalvate)
                    {
                        errore.Text = "Salvare prima le modifiche NC in corso, poi preparare il ticket TICKET.";
                        errore.IsVisible = true;
                        return;
                    }
                    await PolarionCreateTicketDialog.OpenNcAsync(window, polarionState, v.Nc, v.TestCollegato);
                };
                contenuto.Children.Add(prepareEsta);
            }
            contenuto.Children.Add(motivoPanel);
            contenuto.Children.Add(motivoRiaperturaPanel);
            contenuto.Children.Add(bannerConflitto);
            contenuto.Children.Add(solaLettura);
            contenuto.Children.Add(errore);
            contenuto.Children.Add(azioni);

            stato.SelectionChanged += (_, _) =>
            {
                if (stato.SelectedItem is not OpzioneRapida<StatoAnomalia> scelta || v.Nc.StatoWorkflow == scelta.Valore) return;
                v.Nc.StatoWorkflow = scelta.Valore;
                v.SegnalaModificaCampo();
            };
            severita.SelectionChanged += (_, _) =>
            {
                if (severita.SelectedItem is not OpzioneRapida<SeveritaAnomalia?> scelta || v.Nc.Severita == scelta.Valore) return;
                v.Nc.Severita = scelta.Valore;
                v.SegnalaModificaCampo();
            };
            priorita.SelectionChanged += (_, _) =>
            {
                if (priorita.SelectedItem is not OpzioneRapida<PrioritaOperativa> scelta || v.Nc.Priorita == scelta.Valore) return;
                v.Nc.Priorita = scelta.Valore;
                v.SegnalaModificaCampo();
            };
            destinatario.SelectionChanged += (_, _) =>
            {
                if (destinatario.SelectedItem is not OpzioneRapida<DestinatarioSegnalazione?> scelta || v.Nc.Destinatario == scelta.Valore) return;
                v.Nc.Destinatario = scelta.Valore;
                v.SegnalaModificaCampo();
            };
            ownerBox.TextChanged += (_, _) =>
            {
                var value = ownerBox.Text ?? "";
                if (v.Nc.Owner != value) { v.Nc.Owner = value; v.SegnalaModificaCampo(); }
            };
            motivo.TextChanged += (_, _) => v.MotivoChiusura = motivo.Text ?? "";
            motivoRiapertura.TextChanged += (_, _) => v.MotivoRiapertura = motivoRiapertura.Text ?? "";

            async Task Esegui(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand c, AnomaliaDettaglioViewModel v)
            {
                await c.ExecuteAsync(null);
            }

            void ChiudiDopoAzione()
            {
                if (!v.Modificato) return;
                esito = EsitoModificaRapidaNc.Modificata;
                window.Close();
            }
            v.ChiusuraRichiesta += ChiudiDopoAzione;

            annulla.Click += async (_, _) =>
            {
                if (v.HaModificheNonSalvate && !await ConfirmService.Chiedi(loc.Scegli("Annulla modifiche", "Discard changes", "Annuler les modifications"), loc.Scegli("Scartare le modifiche non salvate?", "Discard unsaved changes?", "Abandonner les modifications non enregistrées ?"), loc.Scegli("Scarta", "Discard", "Abandonner"), loc.Traduci("Annulla")))
                    return;
                window.Close();
            };
            modificaSegnalazione.Click += async (_, _) =>
            {
                if (v.HaModificheNonSalvate && !await ConfirmService.Chiedi(loc.Scegli("Annulla modifiche", "Discard changes", "Annuler les modifications"), loc.Scegli("Scartare le modifiche non salvate?", "Discard unsaved changes?", "Abandonner les modifications non enregistrées ?"), loc.Scegli("Scarta", "Discard", "Abandonner"), loc.Traduci("Annulla")))
                    return;
                esito = EsitoModificaRapidaNc.ModificaSegnalazione;
                window.Close();
            };
            salva.Click += (_, _) => { v.SalvaCommand.Execute(null); errore.Text = v.Errore ?? ""; errore.IsVisible = !string.IsNullOrWhiteSpace(v.Errore); };
            chiudi.Click += async (_, _) => await Esegui(v.ChiudiCommand, v);
            riapri.Click += async (_, _) => await Esegui(v.RiapriCommand, v);
            ricarica.Click += (_, _) =>
            {
                var nuovo = creaVm();
                nuovo.MotivoChiusura = v.MotivoChiusura;
                nuovo.MotivoRiapertura = v.MotivoRiapertura;
                if (handler != null) vmAttuale.PropertyChanged -= handler;
                vmAttuale = nuovo;
                host.Content = Costruisci(nuovo);
            };

            handler = (sender, args) =>
            {
                var vmOsservato = (AnomaliaDettaglioViewModel)sender!;
                if (args.PropertyName == nameof(AnomaliaDettaglioViewModel.Errore))
                {
                    errore.Text = vmOsservato.Errore ?? "";
                    errore.IsVisible = !string.IsNullOrWhiteSpace(vmOsservato.Errore);
                }
                if (args.PropertyName == nameof(AnomaliaDettaglioViewModel.ConflittoInAttesa)) bannerConflitto.IsVisible = vmOsservato.ConflittoInAttesa;
                if (args.PropertyName == nameof(AnomaliaDettaglioViewModel.PuoChiudere)) { motivoPanel.IsVisible = vmOsservato.PuoChiudere; chiudi.IsEnabled = vmOsservato.PuoChiudere; }
                if (args.PropertyName == nameof(AnomaliaDettaglioViewModel.PuoRiaprire)) { motivoRiaperturaPanel.IsVisible = vmOsservato.PuoRiaprire; riapri.IsEnabled = vmOsservato.PuoRiaprire; }
                if (args.PropertyName == nameof(AnomaliaDettaglioViewModel.PuoModificare)) solaLettura.IsVisible = !vmOsservato.PuoModificare;
            };
            v.PropertyChanged += handler;
            return contenuto;
        }

        host.Content = Costruisci(vm);
        window.Closed += (_, _) =>
        {
            if (handler != null) vmAttuale.PropertyChanged -= handler;
            completion.TrySetResult(esito != EsitoModificaRapidaNc.Annullata
                ? esito
                : vm.Modificato ? EsitoModificaRapidaNc.Modificata : EsitoModificaRapidaNc.Annullata);
        };
        AdattamentoDialogo.Registra(window);
        await window.ShowDialog(owner);
        return await completion.Task;
    }




    private sealed record OpzioneRapida<T>(T Valore, string Etichetta);
    private enum EsitoModificaRapidaNc { Annullata, Modificata, ModificaSegnalazione }


    private static ComboBox CreaComboRapida<T>(IReadOnlyList<OpzioneRapida<T>> opzioni, T selezionata, string automationId)
    {
        var combo = new ComboBox
        {
            ItemsSource = opzioni,
            SelectedItem = opzioni.FirstOrDefault(opzione => EqualityComparer<T>.Default.Equals(opzione.Valore, selezionata)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<OpzioneRapida<T>>((opzione, _) => new TextBlock { Text = opzione.Etichetta })
        };
        AutomationProperties.SetAutomationId(combo, automationId);
        return combo;
    }

    private static void AggiungiCampoRapido(Panel pannello, string etichetta, Control controllo)
    {
        var campo = new StackPanel { Spacing = 3 };
        campo.Children.Add(new TextBlock { Text = etichetta, Classes = { "Muted" }, FontWeight = FontWeight.SemiBold });
        campo.Children.Add(controllo);
        pannello.Children.Add(campo);
    }

    private async void OnModificaRapidaChangeRequest(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NonConformitaViewModel vm || sender is not Button { DataContext: ChangeRequest riga } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await UiExceptionGuard.Esegui(async () =>
        {
            vm.MessaggioErrore = null;
            var editor = vm.CreaEditorStatoChangeRequest(riga);
            if (editor == null) return;
            var window = CreaFinestraModificaRapidaCr(vm, riga, editor);
            var esito = await window.ShowDialog<bool>(owner);
            if (window.Tag as string == "editor")
            {
                vm.ModificaChangeRequestCommand.Execute(riga);
            }
            else if (esito)
            {
                vm.RicaricaDopoDettaglio();
            }
        }, "Modifica rapida Change Request");
    }

    private Window CreaFinestraModificaRapidaCr(NonConformitaViewModel vmNc, ChangeRequest riga, ChangeRequestEditorViewModel editor)
    {
        var loc = LocalizationService.Instance;
        var dialog = new Window
        {
            Title = editor.ChangeRequest.Id + " · " + loc.Scegli("Modifica rapida", "Quick edit", "Modification rapide"),
            Width = 560, MinWidth = 460, SizeToContent = SizeToContent.Height, CanResize = true
        };
        AutomationProperties.SetAutomationId(dialog, "Anomalie_Cr_ModificaRapida_Window");
        var host = new ContentControl();
        dialog.Content = new ScrollViewer { Content = host, MaxHeight = 600 };

        Control Costruisci(ChangeRequestEditorViewModel vm)
        {
            var cr = vm.ChangeRequest;
            var contenuto = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
            contenuto.Children.Add(new TextBlock { Text = loc.Scegli("Modifica rapida", "Quick edit", "Modification rapide"), FontSize = 20, FontWeight = FontWeight.Bold });

            var intestazione = new StackPanel { Spacing = 4 };
            var rigaIdTitolo = new TextBlock
            {
                Text = $"{cr.Id} · {cr.Titolo}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeight.SemiBold
            };
            ToolTip.SetTip(rigaIdTitolo, $"{cr.Id} · {cr.Titolo}");
            intestazione.Children.Add(rigaIdTitolo);

            var descrizione = new TextBlock
            {
                Text = cr.Descrizione ?? "",
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap
            };
            ToolTip.SetTip(descrizione, cr.Descrizione ?? "");
            intestazione.Children.Add(descrizione);

            var statoLabel = new TextBlock { Text = EtichettaStatoCr(cr.Stato), Classes = { "Muted" } };
            intestazione.Children.Add(statoLabel);

            if (cr.ChiusaSenzaValidazione)
            {
                var chiusaLabel = new TextBlock
                {
                    Text = loc.Scegli("Chiusa senza validazione", "Closed without validation", "Fermée sans validation"),
                    Foreground = Brushes.DarkOrange,
                    FontWeight = FontWeight.SemiBold
                };
                ToolTip.SetTip(chiusaLabel, $"{cr.MotivoChiusura}\n{cr.ChiusaDa} · {cr.ChiusaIl:yyyy-MM-dd HH:mm}");
                intestazione.Children.Add(chiusaLabel);
            }
            contenuto.Children.Add(intestazione);

            var bannerErrore = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            AutomationProperties.SetAutomationId(bannerErrore, "Anomalie_Cr_ModificaRapida_Errore_Text");
            contenuto.Children.Add(bannerErrore);

            var bannerConflitto = new StackPanel { Spacing = 4, IsVisible = false };
            var testoConflitto = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
            var btnRicarica = new Button { Content = loc.Scegli("Ricarica", "Reload", "Recharger"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(btnRicarica, "Anomalie_Cr_ModificaRapida_Ricarica_Button");
            bannerConflitto.Children.Add(testoConflitto);
            bannerConflitto.Children.Add(btnRicarica);
            contenuto.Children.Add(bannerConflitto);

            var bannerMessaggio = new TextBlock { Foreground = Brushes.SeaGreen, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            AutomationProperties.SetAutomationId(bannerMessaggio, "Anomalie_Cr_ModificaRapida_Messaggio_Text");
            contenuto.Children.Add(bannerMessaggio);

            var modificheNonSalvate = new TextBlock
            {
                Text = loc.Traduci("ModificheNonSalvateTitolo"),
                Foreground = Brushes.DarkOrange,
                FontWeight = FontWeight.SemiBold,
                IsVisible = false
            };
            AutomationProperties.SetAutomationId(modificheNonSalvate, "Anomalie_Cr_ModificaRapida_ModificheNonSalvate_Text");
            contenuto.Children.Add(modificheNonSalvate);

            var soloLettura = new TextBlock
            {
                Text = loc.Scegli("Sola lettura: la CR è chiusa, archiviata o non hai i permessi per modificarla.", "Read-only: the CR is closed, archived, or you do not have permission to edit it.", "Lecture seule : la CR est fermée, archivée ou vous n'avez pas les autorisations pour la modifier."),
                Classes = { "Muted" },
                IsVisible = false
            };
            contenuto.Children.Add(soloLettura);

            var responsabileCombo = new ComboBox { ItemsSource = vm.OperatoriDisponibili, SelectedItem = vm.Responsabile };
            AutomationProperties.SetAutomationId(responsabileCombo, "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox");
            responsabileCombo.SelectionChanged += (_, _) =>
            {
                if (responsabileCombo.SelectedItem is string sel) vm.Responsabile = sel;
            };
            AggiungiCampoRapido(contenuto, loc.Scegli("Responsabile", "Owner", "Responsable"), responsabileCombo);

            var ticketPanel = new StackPanel { Spacing = 4 };
            var ticketLabel = new TextBlock { Text = loc.Scegli("Ticket collegati", "Linked tickets", "Tickets liés"), Classes = { "Muted" }, FontWeight = FontWeight.SemiBold };
            ticketPanel.Children.Add(ticketLabel);
            var ticketList = new StackPanel { Spacing = 2 };
            ticketPanel.Children.Add(ticketList);
            var nuovoTicketRif = new TextBox { Text = vm.NuovoTicketRiferimento, PlaceholderText = loc.Scegli("Riferimento", "Reference", "Référence") };
            var nuovoTicketUrl = new TextBox { Text = vm.NuovoTicketUrl, PlaceholderText = loc.Scegli("URL", "URL", "URL") };
            var btnAggiungiTicket = new Button { Content = loc.Scegli("Aggiungi", "Add", "Ajouter"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(btnAggiungiTicket, "Anomalie_Cr_ModificaRapida_AggiungiTicket_Button");
            nuovoTicketRif.TextChanged += (_, _) => vm.NuovoTicketRiferimento = nuovoTicketRif.Text ?? "";
            nuovoTicketUrl.TextChanged += (_, _) => vm.NuovoTicketUrl = nuovoTicketUrl.Text ?? "";
            AutomationProperties.SetAutomationId(nuovoTicketRif, "Anomalie_Cr_ModificaRapida_TicketRif_TextBox");
            AutomationProperties.SetAutomationId(nuovoTicketUrl, "Anomalie_Cr_ModificaRapida_TicketUrl_TextBox");
            btnAggiungiTicket.Click += (_, _) => vm.AggiungiTicketCommand.Execute(null);
            var rigaNuovoTicket = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            rigaNuovoTicket.Children.Add(nuovoTicketRif);
            rigaNuovoTicket.Children.Add(nuovoTicketUrl);
            rigaNuovoTicket.Children.Add(btnAggiungiTicket);
            ticketPanel.Children.Add(rigaNuovoTicket);
            contenuto.Children.Add(ticketPanel);
            if (cr.TicketCollegati.Count > 0)
                contenuto.Children.Add(PolarionQuickEditPanel.Create(vmNc.PolarionPaths,
                    cr.TicketCollegati.Select(x => (Id: x.Riferimento, Url: x.Url)), dialog, vmNc.PolarionOperator,
                    isCr: true, testloomClosed: cr.Stato == StatoChangeRequest.Chiusa,
                    operatorCanWrite: vmNc.PolarionState.PuoScrivere,
                    operatorCanWriteNow: () => vmNc.PolarionState.PuoScrivere && vmNc.PolarionState.OperatoreCorrente is not null &&
                        string.Equals(vmNc.PolarionState.Config.OperatoreAttivo, vmNc.PolarionOperator, StringComparison.OrdinalIgnoreCase)));
            if (vmNc.PolarionState.PuoScrivere && !cr.Archiviata)
            {
                var prepareEsta = new Button { Content = "Prepara / Crea ticket TICKET", Classes = { "Secondary" } };
                AutomationProperties.SetAutomationId(prepareEsta, "PolarionCreate_Cr_Preview_Button");
                prepareEsta.Click += async (_, _) =>
                {
                    if (vm.ModificheNonSalvate)
                    {
                        vmNc.MessaggioErrore = "Salvare prima le modifiche CR in corso, poi preparare il ticket TICKET.";
                        return;
                    }
                    await PolarionCreateTicketDialog.OpenCrAsync(dialog, vmNc.PolarionState, cr);
                };
                contenuto.Children.Add(prepareEsta);
            }

            void AggiornaTicketList()
            {
                ticketList.Children.Clear();
                foreach (var t in vm.TicketCollegatiVisual)
                {
                    var riga = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    var testo = new TextBlock { Text = $"{t.Riferimento} {t.Url}", TextTrimming = TextTrimming.CharacterEllipsis };
                    ToolTip.SetTip(testo, $"{t.Riferimento} {t.Url}");
                    var btnRimuovi = new Button { Content = loc.Scegli("Rimuovi", "Remove", "Supprimer"), Classes = { "Secondary" } };
                    btnRimuovi.Click += (_, _) => vm.RimuoviTicketCommand.Execute(t);
                    riga.Children.Add(testo);
                    riga.Children.Add(btnRimuovi);
                    ticketList.Children.Add(riga);
                }
            }
            vm.TicketCollegatiVisual.CollectionChanged += (_, _) => AggiornaTicketList();
            AggiornaTicketList();

            var pannelloStato = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var btnImplementa = new Button { Content = loc.Scegli("Implementa", "Implement", "Implémenter"), Classes = { "Primary" } };
            AutomationProperties.SetAutomationId(btnImplementa, "Anomalie_Cr_ModificaRapida_Implementa_Button");
            var btnChiudi = new Button { Content = loc.Scegli("Chiudi CR", "Close CR", "Fermer CR"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(btnChiudi, "Anomalie_Cr_ModificaRapida_Chiudi_Button");
            var btnRiapri = new Button { Content = loc.Scegli("Riapri", "Reopen", "Rouvrir"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(btnRiapri, "Anomalie_Cr_ModificaRapida_Riapri_Button");
            pannelloStato.Children.Add(btnImplementa);
            pannelloStato.Children.Add(btnChiudi);
            pannelloStato.Children.Add(btnRiapri);
            contenuto.Children.Add(pannelloStato);

            var motivoPanel = new StackPanel { Spacing = 3, IsVisible = false };
            var motivoLabel = new TextBlock { Text = loc.Scegli("Motivo (obbligatorio)", "Reason (required)", "Motif (obligatoire)"), Classes = { "Muted" }, FontWeight = FontWeight.SemiBold };
            var motivoBox = new TextBox { Text = vm.MotivoTransizione, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, VerticalContentAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAutomationId(motivoBox, "Anomalie_Cr_ModificaRapida_Motivo_TextBox");
            motivoBox.TextChanged += (_, _) => vm.MotivoTransizione = motivoBox.Text ?? "";
            motivoPanel.Children.Add(motivoLabel);
            motivoPanel.Children.Add(motivoBox);
            contenuto.Children.Add(motivoPanel);

            var btnApriEditor = new Button { Content = loc.Scegli("Apri per completare", "Open to complete", "Ouvrir pour compléter"), Classes = { "Secondary" }, IsVisible = false };
            AutomationProperties.SetAutomationId(btnApriEditor, "Anomalie_Cr_ModificaRapida_ApriEditor_Button");
            btnApriEditor.Click += (_, _) =>
            {
                dialog.Tag = "editor";
                dialog.Close(false);
            };
            contenuto.Children.Add(btnApriEditor);

            var azioni = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var btnAnnulla = new Button { Content = loc.Traduci("Annulla"), Classes = { "Secondary" } };
            AutomationProperties.SetAutomationId(btnAnnulla, "Anomalie_Cr_ModificaRapida_Annulla_Button");
            var btnSalva = new Button { Content = loc.Traduci("SalvaBtnSemplice"), Classes = { "Primary" } };
            AutomationProperties.SetAutomationId(btnSalva, "Anomalie_Cr_ModificaRapida_Salva_Button");
            azioni.Children.Add(btnAnnulla);
            azioni.Children.Add(btnSalva);
            contenuto.Children.Add(azioni);

            void Esegui(CommunityToolkit.Mvvm.Input.IRelayCommand c, ChangeRequestEditorViewModel vmAttuale)
            {
                c.Execute(null);
                if (string.IsNullOrEmpty(vmAttuale.Errore) && !string.IsNullOrEmpty(vmAttuale.Messaggio))
                    dialog.Close(true);
            }

            btnImplementa.Click += (_, _) => Esegui(vm.ImplementaCommand, vm);
            btnChiudi.Click += (_, _) => Esegui(vm.ChiudiCommand, vm);
            btnRiapri.Click += (_, _) => Esegui(vm.RiapriCommand, vm);
            btnSalva.Click += (_, _) => Esegui(vm.SalvaRapidaCommand, vm);
            btnAnnulla.Click += async (_, _) =>
            {
                if (vm.ModificheNonSalvate)
                {
                    var conferma = await ConfirmService.Chiedi(
                        loc.Scegli("Annulla modifiche", "Discard changes", "Annuler les modifications"),
                        loc.Scegli("Hai modifiche non salvate. Vuoi scartarle?", "You have unsaved changes. Do you want to discard them?", "Vous avez des modifications non enregistrées. Voulez-vous les annuler ?"),
                        loc.Scegli("Scarta", "Discard", "Annuler"),
                        loc.Scegli("Annulla", "Cancel", "Annuler"));
                    if (!conferma) return;
                }
                dialog.Close(false);
            };
            btnRicarica.Click += (_, _) =>
            {
                var nuovo = vmNc.CreaEditorStatoChangeRequest(riga);
                if (nuovo == null)
                {
                    dialog.Close(false);
                    return;
                }
                nuovo.MotivoTransizione = vm.MotivoTransizione;
                host.Content = Costruisci(nuovo);
            };

            void Aggiorna()
            {
                bannerErrore.Text = vm.Errore ?? "";
                bannerErrore.IsVisible = !string.IsNullOrWhiteSpace(vm.Errore);
                btnApriEditor.IsVisible = !string.IsNullOrWhiteSpace(vm.Errore) && vm.PuoImplementare;
                bannerConflitto.IsVisible = vm.ConflittoRevisione;
                if (vm.ConflittoRevisione) testoConflitto.Text = vm.Errore ?? "";
                bannerMessaggio.Text = vm.Messaggio ?? "";
                bannerMessaggio.IsVisible = !string.IsNullOrWhiteSpace(vm.Messaggio);
                modificheNonSalvate.IsVisible = vm.ModificheNonSalvate;
                soloLettura.IsVisible = !vm.PuoModificare && !vm.HaAzioniDiChiusura;
                responsabileCombo.IsEnabled = vm.PuoModificare;
                if (!ReferenceEquals(responsabileCombo.ItemsSource, vm.OperatoriDisponibili)) responsabileCombo.ItemsSource = vm.OperatoriDisponibili;
                if (!Equals(responsabileCombo.SelectedItem, vm.Responsabile)) responsabileCombo.SelectedItem = vm.Responsabile;
                if (nuovoTicketRif.Text != vm.NuovoTicketRiferimento) nuovoTicketRif.Text = vm.NuovoTicketRiferimento;
                if (nuovoTicketUrl.Text != vm.NuovoTicketUrl) nuovoTicketUrl.Text = vm.NuovoTicketUrl;
                ticketPanel.IsEnabled = vm.PuoModificare;
                btnImplementa.IsVisible = vm.PuoImplementare;
                btnChiudi.IsVisible = vm.PuoChiudere;
                btnRiapri.IsVisible = vm.PuoRiaprire;
                motivoPanel.IsVisible = vm.HaAzioniDiChiusura;
                btnSalva.IsEnabled = vm.PuoModificare;
                btnSalva.IsVisible = vm.PuoModificare;
            }

            void OnVmChanged(object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName is nameof(ChangeRequestEditorViewModel.Errore) or nameof(ChangeRequestEditorViewModel.Messaggio) or
                    nameof(ChangeRequestEditorViewModel.ConflittoRevisione) or nameof(ChangeRequestEditorViewModel.ModificheNonSalvate) or
                    nameof(ChangeRequestEditorViewModel.PuoModificare) or nameof(ChangeRequestEditorViewModel.PuoImplementare) or
                    nameof(ChangeRequestEditorViewModel.PuoChiudere) or nameof(ChangeRequestEditorViewModel.PuoRiaprire) or
                    nameof(ChangeRequestEditorViewModel.HaAzioniDiChiusura) or nameof(ChangeRequestEditorViewModel.Responsabile) or
                    nameof(ChangeRequestEditorViewModel.OperatoriDisponibili) or nameof(ChangeRequestEditorViewModel.MotivoTransizione) or
                    nameof(ChangeRequestEditorViewModel.NuovoTicketRiferimento) or nameof(ChangeRequestEditorViewModel.NuovoTicketUrl))
                {
                    Aggiorna();
                }
            }
            vm.PropertyChanged += OnVmChanged;
            dialog.Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;
            Aggiorna();
            return contenuto;
        }

        host.Content = Costruisci(editor);
        AdattamentoDialogo.Registra(dialog);
        return dialog;
    }


    private async void OnCambiaStatoNc(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NonConformitaViewModel vm || sender is not Button { DataContext: NcRiga riga } ||
            TopLevel.GetTopLevel(this) is not Window owner) return;
        await UiExceptionGuard.Esegui(async () =>
        {
            var detail = vm.CreaDettaglio(riga);
            if (await ApriStatoNc(owner, detail)) vm.RicaricaDopoDettaglio();
        }, "Cambio stato rapido NC");
    }

    private static Window CreaFinestraDettaglioCr(ChangeRequest cr, bool puoModificare)
    {
        var loc = LocalizationService.Instance;
        var contenuto = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        var statoTesto = EtichettaStatoCr(cr.Stato);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Id_Text", "ID", cr.Id);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Titolo_Text", loc.Scegli("Titolo", "Title", "Titre"), cr.Titolo);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Stato_Text", loc.Traduci("CrEdStatoLabel"), statoTesto);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Creatore_Text", loc.Scegli("Creato da", "Created by", "Créé par"), cr.CreatoDa);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Responsabile_Text", loc.Scegli("Responsabile", "Owner", "Responsable"), cr.Responsabile);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Descrizione_Text", loc.Scegli("Descrizione", "Description", "Description"), cr.Descrizione);
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Veicoli_Text", loc.Scegli("Veicoli", "Vehicles", "Véhicules"), string.Join(", ", cr.VeicoliImpattati));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Componente_Text", "Componente", string.Join(", ", cr.EcuImpattate));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Strumenti_Text", loc.Scegli("Strumenti", "Tools", "Outils"), string.Join(", ", cr.StrumentiImpattati));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Ticket_Text", loc.Scegli("Ticket", "Tickets", "Tickets"), string.Join(", ", cr.TicketCollegati.Select(t => t.Riferimento)));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Test_Text", loc.Scegli("Test di validazione", "Validation tests", "Tests de validation"), string.Join(", ", cr.TestValidazione.Select(t => t.TestId)));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Allegati_Text", loc.Scegli("Allegati", "Attachments", "Pièces jointes"), string.Join(", ", cr.Allegati.Select(a => a.NomeFile)));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_CampiCustom_Text", loc.Scegli("Campi aggiuntivi", "Custom fields", "Champs personnalisés"),
            string.Join(Environment.NewLine, cr.CampiCustom.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}: {p.Value}")));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Changelog_Text", loc.Scegli("Storico modifiche", "Change history", "Historique des modifications"),
            string.Join(Environment.NewLine, cr.Changelog.TakeLast(20).Select(c => $"{c.Data:g} · {c.Operatore} · {c.Campo}: {c.ValorePrecedente} → {c.ValoreNuovo}")));
        AggiungiCampoCr(contenuto, "Anomalie_Cr_Dettaglio_Modifica_Text", loc.Scegli("Ultima modifica", "Last modified", "Dernière modification"), cr.UltimaModifica.ToString("g"));
        var chiudi = new Button { Content = loc.Traduci("Annulla") };
        var modifica = new Button { Content = loc.Traduci("ModificaChangeRequestBtn"), Classes = { "Primary" }, IsVisible = puoModificare };
        AutomationProperties.SetAutomationId(chiudi, "Anomalie_Cr_Dettaglio_Chiudi_Button");
        AutomationProperties.SetAutomationId(modifica, "Anomalie_Cr_Dettaglio_Modifica_Button");
        var azioni = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        azioni.Children.Add(chiudi);
        azioni.Children.Add(modifica);
        contenuto.Children.Add(azioni);
        var scroll = new ScrollViewer { Content = contenuto, MaxHeight = 600, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window
        {
            Title = loc.Scegli("Dettagli Change Request", "Change Request details", "Détails de la demande de changement") + " · " + cr.Id,
            Width = 650, MinWidth = 430, SizeToContent = SizeToContent.Height, CanResize = true,
            Content = scroll
        };
        chiudi.Click += (_, _) => window.Close(false);
        modifica.Click += (_, _) => window.Close(true);
        AdattamentoDialogo.Registra(window);
        AutomationProperties.SetAutomationId(window, "Anomalie_Cr_Dettaglio_Window");
        return window;
    }

    private static void AggiungiCampoCr(Panel pannello, string id, string etichetta, string? valore)
    {
        var campo = new StackPanel { Spacing = 3 };
        campo.Children.Add(new TextBlock { Text = etichetta, FontWeight = FontWeight.SemiBold });
        var testo = new TextBlock { Text = string.IsNullOrWhiteSpace(valore) ? "—" : valore, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(testo, id);
        campo.Children.Add(testo);
        pannello.Children.Add(campo);
    }


    private static string EtichettaStatoCr(StatoChangeRequest stato) =>
        WorkflowLabelConverter.Instance.Convert(stato, typeof(string), null,
            System.Globalization.CultureInfo.InvariantCulture)?.ToString() ?? stato.ToString();

    private static async Task<bool> ApriStatoNc(Window owner, AnomaliaDettaglioViewModel vm)
    {
        var loc = LocalizationService.Instance;
        var motivo = new TextBox { Text = vm.MotivoChiusura, PlaceholderText = loc.Scegli("Motivo di chiusura obbligatorio", "Closure reason required", "Motif de clôture obligatoire"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, VerticalContentAlignment = VerticalAlignment.Top };
        var errore = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        var chiudi = new Button { Content = loc.Traduci("ChiudiAnomaliaBtn"), Classes = { "Danger" }, IsVisible = vm.PuoChiudere, IsEnabled = vm.PuoChiudere };
        var riapri = new Button { Content = loc.Traduci("RiapriAnomaliaBtn"), IsVisible = vm.PuoRiaprire, IsEnabled = vm.PuoRiaprire };
        var annulla = new Button { Content = loc.Traduci("Annulla") };
        AutomationProperties.SetAutomationId(motivo, "Anomalie_NcStato_Motivo_TextBox");
        AutomationProperties.SetAutomationId(errore, "Anomalie_NcStato_Errore_Text");
        AutomationProperties.SetAutomationId(chiudi, "Anomalie_NcStato_Chiudi_Button");
        AutomationProperties.SetAutomationId(riapri, "Anomalie_NcStato_Riapri_Button");
        AutomationProperties.SetAutomationId(annulla, "Anomalie_NcStato_Annulla_Button");
        var azioni = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        azioni.Children.Add(annulla);
        azioni.Children.Add(riapri);
        azioni.Children.Add(chiudi);
        var contenuto = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        contenuto.Children.Add(new TextBlock { Text = vm.Nc.Id + " · " + vm.Nc.StatoWorkflow.Label(), FontSize = 20, FontWeight = FontWeight.Bold });
        if (vm.PuoChiudere)
        {
            var motivoLabel = new TextBlock { Text = loc.Traduci("MotivoChiusuraLabel"), FontWeight = FontWeight.SemiBold };
            AutomationProperties.SetAutomationId(motivoLabel, "Anomalie_NcStato_Motivo_Label");
            contenuto.Children.Add(motivoLabel);
            contenuto.Children.Add(motivo);
        }
        contenuto.Children.Add(errore);
        contenuto.Children.Add(azioni);
        var window = new Window { Title = loc.Scegli("Stato anomalia", "Anomaly state", "Statut de l’anomalie") + " · " + vm.Nc.Id, Width = 520, MinWidth = 430, SizeToContent = SizeToContent.Height, CanResize = true, Content = contenuto };
        var completion = new TaskCompletionSource<bool>();
        vm.ChiusuraRichiesta += ChiudiDopoAzione;
        window.Closed += (_, _) => completion.TrySetResult(vm.Modificato);
        void ChiudiDopoAzione() { if (vm.Modificato) window.Close(); }
        motivo.TextChanged += (_, _) => vm.MotivoChiusura = motivo.Text ?? "";
        window.Opened += (_, _) => { if (vm.PuoChiudere) motivo.Focus(); };
        annulla.Click += (_, _) => window.Close();
        chiudi.Click += async (_, _) =>
        {
            await vm.ChiudiCommand.ExecuteAsync(null);
            errore.Text = vm.Errore ?? "";
        };
        riapri.Click += async (_, _) =>
        {
            await vm.RiapriCommand.ExecuteAsync(null);
            errore.Text = vm.Errore ?? "";
        };
        AdattamentoDialogo.Registra(window);
        try
        {
            await window.ShowDialog(owner);
            return await completion.Task;
        }
        finally { vm.ChiusuraRichiesta -= ChiudiDopoAzione; }
    }

    /// <summary>Stesso schema di ReportView/HomeView (feedback utente 2026-08-24, filtri veicolo/gamma/componente
    /// aggiunti anche qui): flyout condiviso, un solo posto dove costruirlo.</summary>
    private void OnApriFiltroMultiSelect(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FiltroMultiSelect filtro) return;
        FiltroMultiSelectFlyoutHelper.Apri(button, filtro);
    }

    /// <summary>Stesso schema di AuditView.OnEsportaExcel/ReportView.OnEsportaExcel (feedback utente
    /// 2026-09-17): picker nativo di salvataggio, mai un percorso fisso automatico.</summary>
    private async void OnEsportaExcel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NonConformitaViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        try
        {
            // Dialogo unico di export: sezioni, colonne e filtri (che partono da quelli a video e, confermando, restano applicati alla pagina).
            if (topLevel is not Window finestraProprietaria) return;
            var opzioni = await EsportaOpzioniDialogo.Apri(finestraProprietaria, vm.StatoPerExport, vm.CreaDescrittoreExport());
            if (opzioni == null) return;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance.Scegli("Salva Excel Non Conformità", "Save Non-conformities Excel", "Enregistrer l'Excel des non-conformités"),
                SuggestedFileName = $"TestLoom_NonConformita_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExtension = "xlsx",
                FileTypeChoices = new[] { new FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } }
            });

            var percorso = file?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(percorso)) await vm.EsportaExcelVerso(percorso, opzioni);
        }
        catch (Exception ex) { CrashLogService.Registra("Esportazione Excel Non Conformità", ex); }
    }
}
