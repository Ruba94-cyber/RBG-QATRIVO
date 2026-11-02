using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Views;

/// <summary>Pannello filtri comprimibile e riepilogo a chip della pagina Anomalie/CR (6.6.1), piu' le altezze delle due schede
/// e la maniglia che le divide. Sta nel code-behind e non nel ViewModel (condiviso con il lavoro di paginazione): legge solo le
/// proprieta' pubbliche dei filtri, e ogni chip annulla un solo filtro richiamando la stessa proprieta' che la pagina usa gia'.</summary>
public partial class NonConformitaView
{
    private const string ChiavePannelloFiltri = "Anomalie.PannelloFiltri";
    private const string ChiaveCrEspansa = "Anomalie.CrEspansa";
    private const string ChiaveRapportoSchede = "Anomalie.RapportoSchede";
    /// <summary>Quota iniziale delle anomalie: lascia alla CR spazio per mostrare piu' righe quando entrambe le schede sono aperte.</summary>
    private const double RapportoPredefinito = 0.45;
    private const double RapportoMinimo = 0.05;
    private const double RapportoMassimo = 0.95;
    private const double AltezzaAreaFiltriAperti = 700;
    /// <summary>Altezza minima di una riga CR, usata finche' la tabella non ne ha ancora realizzata una.</summary>
    private const double PassoCrPredefinito = 52;
    private const double AltezzaMinimaAnomalieConCr = 112;
    private const double AltezzaMinimaAnomalieViewportMinimo = 80;
    private const double AltezzaMinimaTabellaAnomalie = 160;
    private NonConformitaViewModel? _vmRiepilogo;
    private bool _pannelloFiltriAperto = true;
    private bool _crEspansa;
    // Stato effettivo della scheda filtri: la preferenza dell'operatore resta in _pannelloFiltriAperto; in Compatto si nasconde con la CR aperta.
    private bool _pannelloFiltriVisibile = true;
    // Anomalie e CR aperte insieme: le altezze minime (AdattaMinimi) valgono solo in questo caso.
    private bool _minimiAttivi;
    // I filtri NC possono comprimersi solo per il layout quando non entrano insieme a entrambe le tabelle.
    private bool _filtriRidottiAutomaticamentePerSpazio;
    private bool _filtriCrRidottiAutomaticamentePerSpazio;
    private bool _tabellaAnomalieCompattaAutomaticamente;
    private double _altezzaNecessariaPerFiltriAperti;
    // Quota delle anomalie nello spazio tra le due schede (0..1). Salvata per operatore, riapplicata come stelle a ogni aggiornamento.
    private double _rapportoAnomalie = RapportoPredefinito;
    // "Espandi": con la CR aperta, anomalie ridotte alla sola intestazione e CR a tutta l'area.
    private bool _anomalieRidotte;
    // Mentre la maniglia viene trascinata, il riepilogo non riapplica il rapporto.
    private bool _trascinamentoSchede;
    private double _altezzaAnomalieInizio;

    /// <summary>Collega il riepilogo al ViewModel corrente: stato del pannello (per operatore), chip, rapporto delle schede.</summary>
    private void CollegaRiepilogo()
    {
        if (DataContext is not NonConformitaViewModel vm || ReferenceEquals(_vmRiepilogo, vm)) return;
        if (_vmRiepilogo != null) _vmRiepilogo.PropertyChanged -= OnVmRiepilogoCambiato;
        _vmRiepilogo = vm;
        vm.PropertyChanged += OnVmRiepilogoCambiato;
        _pannelloFiltriAperto = ViewStateService.LeggiPerOperatore(ChiavePannelloFiltri) != "0";
        // Scheda CR: aperta fuori da Compatto/Minimo; dentro, chiusa per dare spazio alle anomalie. La scelta si ricorda per operatore.
        _crEspansa = ViewStateService.LeggiPerOperatore(ChiaveCrEspansa) is { } salvata ? salvata == "1" : LayoutAdattivo.ModoCorrente > ModoLayout.Compatto;
        _rapportoAnomalie = LeggiRapportoSchede();
        _anomalieRidotte = false;
        AggiornaRiepilogo();
    }

    /// <summary>Apre o chiude la scheda Change Request (intestazione cliccabile). Con le sole CR visibili resta sempre aperta.</summary>
    private void OnApriChiudiCr(object? sender, RoutedEventArgs e)
    {
        // In Compatto aprire la CR chiude i filtri avanzati: non stanno insieme (a 672 px la tabella non ci entrerebbe).
        if (!_crEspansa && DataContext is NonConformitaViewModel vmCr && LayoutAdattivo.ModoCorrente <= ModoLayout.Compatto)
            vmCr.FiltriEspansi = false;
        _crEspansa = !_crEspansa;
        ViewStateService.ScriviPerOperatore(ChiaveCrEspansa, _crEspansa ? "1" : "0");
        AggiornaRiepilogo();
    }

    private void OnVmRiepilogoCambiato(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(NonConformitaViewModel.HaFiltriAttivi) or nameof(NonConformitaViewModel.FiltroTesto)
            or nameof(NonConformitaViewModel.FiltroStato) or nameof(NonConformitaViewModel.FiltroTicket)
            or nameof(NonConformitaViewModel.FiltroTicketEsta) or nameof(NonConformitaViewModel.FiltroDataAperturaDa)
            or nameof(NonConformitaViewModel.FiltroDataAperturaA) or nameof(NonConformitaViewModel.FiltroDataChiusuraDa)
            or nameof(NonConformitaViewModel.FiltroDataChiusuraA) or nameof(NonConformitaViewModel.MostraAnomalie)
            or nameof(NonConformitaViewModel.MostraChangeRequest) or nameof(NonConformitaViewModel.FiltriEspansi)
            or nameof(NonConformitaViewModel.TotaleCr))
            AggiornaRiepilogo();
    }

    /// <summary>Apre o chiude il pannello filtri; la scelta resta per operatore nello stesso store locale. In Compatto il pannello
    /// e la CR non stanno aperti insieme: aprire i filtri chiude la CR, che si riapre dalla sua intestazione.</summary>
    private void OnApriChiudiFiltriNc(object? sender, RoutedEventArgs e)
    {
        var apri = !_pannelloFiltriVisibile;
        // Se il layout li ha compressi automaticamente, il pulsante apre i filtri dando loro la vista intera.
        // Chiude quindi la scheda CR, come avviene gia' in Compatto.
        if (apri && _filtriRidottiAutomaticamentePerSpazio && _crEspansa)
        {
            _crEspansa = false;
            ViewStateService.ScriviPerOperatore(ChiaveCrEspansa, "0");
        }
        _pannelloFiltriAperto = apri;
        if (!apri) _filtriRidottiAutomaticamentePerSpazio = false;
        ViewStateService.ScriviPerOperatore(ChiavePannelloFiltri, _pannelloFiltriAperto ? "1" : "0");
        if (_pannelloFiltriAperto && _crEspansa && LayoutAdattivo.ModoCorrente <= ModoLayout.Compatto)
        {
            _crEspansa = false;
            ViewStateService.ScriviPerOperatore(ChiaveCrEspansa, "0");
        }
        AggiornaRiepilogo();
    }

    /// <summary>Pulsante Espandi nell'intestazione della CR: riduce le anomalie alla sola intestazione e lascia alla CR tutta
    /// l'area; un secondo clic le ripristina. Disponibile solo con anomalie e CR aperte.</summary>
    private void OnEspandiAnomalie(object? sender, RoutedEventArgs e)
    {
        _anomalieRidotte = !_anomalieRidotte;
        AggiornaRiepilogo();
    }

    private void IniziaTrascinamentoSchede()
    {
        _trascinamentoSchede = true;
        _altezzaAnomalieInizio = AreaSchede.RowDefinitions[0].ActualHeight;
    }

    private void ConcludiTrascinamentoSchede()
    {
        _trascinamentoSchede = false;
        // Un semplice clic sulla maniglia non cambia il rapporto: si salva solo uno spostamento reale.
        if (Math.Abs(AreaSchede.RowDefinitions[0].ActualHeight - _altezzaAnomalieInizio) >= 1) RegistraRapportoTrascinato();
        AggiornaRiepilogo();
    }

    /// <summary>Legge il rapporto dalle altezze delle due schede dopo lo spostamento della maniglia e lo salva per l'operatore.</summary>
    internal void RegistraRapportoTrascinato()
    {
        var anomalie = AreaSchede.RowDefinitions[0].ActualHeight;
        var cr = AreaSchede.RowDefinitions[2].ActualHeight;
        if (!(anomalie > 0 && cr > 0)) return;
        ImpostaRapporto(anomalie / (anomalie + cr));
    }

    /// <summary>Doppio clic sulla maniglia: torna alla ripartizione predefinita 45/55.</summary>
    internal void RipristinaRapportoPredefinito()
    {
        ImpostaRapporto(RapportoPredefinito);
        AggiornaRiepilogo();
    }

    private void ImpostaRapporto(double rapporto)
    {
        _rapportoAnomalie = Math.Clamp(rapporto, RapportoMinimo, RapportoMassimo);
        ViewStateService.ScriviPerOperatore(ChiaveRapportoSchede, _rapportoAnomalie.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static double LeggiRapportoSchede()
    {
        var salvato = ViewStateService.LeggiPerOperatore(ChiaveRapportoSchede);
        return double.TryParse(salvato, NumberStyles.Float, CultureInfo.InvariantCulture, out var letto) && letto > 0 && letto < 1
            ? Math.Clamp(letto, RapportoMinimo, RapportoMassimo)
            : RapportoPredefinito;
    }

    private void AggiornaRiepilogo()
    {
        if (DataContext is not NonConformitaViewModel vm) return;
        var compatto = LayoutAdattivo.ModoCorrente <= ModoLayout.Compatto;
        // Scheda CR aperta, oppure sola CR visibile (non chiudibile). Chiusa: la CR occupa solo la sua intestazione.
        // In Compatto la CR e i filtri avanzati non stanno insieme: con i filtri avanzati aperti la CR si mostra chiusa.
        var crAperta = !vm.MostraAnomalie || (_crEspansa && !(compatto && vm.FiltriEspansi));
        var entrambe = vm.MostraAnomalie && vm.MostraChangeRequest && crAperta;
        if (!entrambe || AreaSchede.Bounds.Height >= _altezzaNecessariaPerFiltriAperti)
        {
            _filtriRidottiAutomaticamentePerSpazio = false;
            _filtriCrRidottiAutomaticamentePerSpazio = false;
            _tabellaAnomalieCompattaAutomaticamente = false;
            _altezzaNecessariaPerFiltriAperti = 0;
        }
        if (!entrambe) _anomalieRidotte = false;   // senza la CR aperta le anomalie tornano complete
        var ridotte = entrambe && _anomalieRidotte;
        // In Compatto o quando lo spazio verticale non basta, i filtri si comprimono senza cambiare la preferenza salvata.
        var filtriAutoNascosti = _filtriRidottiAutomaticamentePerSpazio && AreaSchede.Bounds.Height < _altezzaNecessariaPerFiltriAperti;
        ImpostaPannelloFiltri(_pannelloFiltriAperto && !(compatto && entrambe) && !filtriAutoNascosti);
        FiltriCr.IsVisible = !_filtriCrRidottiAutomaticamentePerSpazio;
        ImpostaVisibilitaTabellaAnomalie(vm.MostraAnomalie && !ridotte);

        // Altezze. Con anomalie e CR aperte, la quota e' il rapporto (stelle) e la maniglia sta in mezzo; con le anomalie ridotte
        // la CR ha tutta l'area; con una sola scheda visibile quella prende tutto. Mentre si trascina, decide il trascinamento.
        var righe = AreaSchede.RowDefinitions;
        if (!_trascinamentoSchede)
        {
            righe[0].Height = !vm.MostraAnomalie || ridotte ? GridLength.Auto
                : entrambe ? new GridLength(_rapportoAnomalie, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            righe[2].Height = !vm.MostraChangeRequest || !crAperta ? GridLength.Auto
                : entrambe && !ridotte ? new GridLength(1 - _rapportoAnomalie, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        }
        SplitterSchede.IsVisible = entrambe && !ridotte;
        _minimiAttivi = entrambe && !ridotte;
        if (_minimiAttivi) Dispatcher.UIThread.Post(AdattaMinimi, DispatcherPriority.Loaded);
        else
        {
            righe[0].MinHeight = 0;
            righe[2].MinHeight = 0;
            AnomalieGrid.MinHeight = AltezzaMinimaTabellaAnomalie;
            ListaCr.MinHeight = PassoCrPredefinito;
        }

        FrecciaCr.Text = crAperta ? "▾" : "▸";
        FiltriCr.IsVisible = crAperta && !_filtriCrRidottiAutomaticamentePerSpazio;
        ListaCr.IsVisible = PaginazioneCr.IsVisible = crAperta;
        VuotoCr.IsVisible = crAperta && vm.ChangeRequests.Count == 0;
        BottoneEspandiCr.IsVisible = entrambe;
        BottoneEspandiCr.Content = Traduci(ridotte ? "CrRipristinaBtn" : "CrEspandiBtn");
    }

    private void ImpostaPannelloFiltri(bool visibile)
    {
        _pannelloFiltriVisibile = visibile;
        ScorrimentoFiltriNc.IsVisible = visibile && !_anomalieRidotte;
        var chip = DataContext is NonConformitaViewModel vm ? CostruisciChip(vm) : new List<ChipFiltroLista>();
        ChipFiltriNc.ItemsSource = chip;
        ChipFiltriNc.IsVisible = !visibile && chip.Count > 0 && !_anomalieRidotte;
        var titolo = LocalizationService.Instance.Traduci("TestListFiltriBtn");
        var freccia = visibile ? "▴" : "▾";
        BottoneFiltriNc.Content = chip.Count > 0 ? $"{titolo} ({chip.Count}) {freccia}" : $"{titolo} {freccia}";
    }

    /// <summary>Nasconde ricerca, tabella e paginazione delle anomalie quando sono ridotte all'intestazione. La barra di caricamento
    /// resta: e' in una riga a parte e si vede solo durante un caricamento.</summary>
    private void ImpostaVisibilitaTabellaAnomalie(bool mostra)
    {
        RigaRicercaNc.IsVisible = mostra;
        AnomalieGrid.IsVisible = mostra;
        PaginazioneNc.IsVisible = mostra;
    }

    /// <summary>Altezze minime con anomalie e CR aperte, misurate dopo il layout. Entrambe le tabelle restano usabili anche nel
    /// viewport basso; la scheda CR conserva intestazione, filtri, paginazione e una riga. Il rapporto trascinato si applica sopra
    /// questi minimi.</summary>
    private void AdattaMinimi()
    {
        if (!_minimiAttivi) return;
        if ((_filtriRidottiAutomaticamentePerSpazio || _filtriCrRidottiAutomaticamentePerSpazio || _tabellaAnomalieCompattaAutomaticamente) &&
            AreaSchede.Bounds.Height >= _altezzaNecessariaPerFiltriAperti)
        {
            _filtriRidottiAutomaticamentePerSpazio = false;
            _filtriCrRidottiAutomaticamentePerSpazio = false;
            _tabellaAnomalieCompattaAutomaticamente = false;
            _altezzaNecessariaPerFiltriAperti = 0;
            ImpostaPannelloFiltri(_pannelloFiltriAperto);
            FiltriCr.IsVisible = true;
            AnomalieGrid.MinHeight = AltezzaMinimaAnomalieConCr;
            Dispatcher.UIThread.Post(AdattaMinimi, DispatcherPriority.Loaded);
            return;
        }
        var righe = AreaSchede.RowDefinitions;
        // Con entrambe le tabelle aperte riserva spazio per almeno una riga NC e una riga CR. Il resto dello spazio
        // rimane disponibile allo splitter: un minimo NC fisso di 160 px nascondeva la prima riga CR a 1366x800.
        AnomalieGrid.MinHeight = _tabellaAnomalieCompattaAutomaticamente
            ? AltezzaMinimaAnomalieViewportMinimo
            : AltezzaMinimaAnomalieConCr;
        var minimoAnomalie = MinimoAnomalie();
        if (minimoAnomalie > 0 && Math.Abs(righe[0].MinHeight - minimoAnomalie) > 0.5) righe[0].MinHeight = minimoAnomalie;
        var minimoCr = MinimoCr();
        var altezzaNecessaria = minimoAnomalie + SplitterSchede.Bounds.Height + minimoCr;
        var viewportBasso = AreaSchede.Bounds.Height < AltezzaAreaFiltriAperti;
        var spazioInsufficiente = altezzaNecessaria > AreaSchede.Bounds.Height + 0.5;
        if (spazioInsufficiente && (!_tabellaAnomalieCompattaAutomaticamente || FiltriCr.IsVisible || ScorrimentoFiltriNc.IsVisible))
        {
            // Alla dimensione minima di MainWindow non bastano il collasso dei soli filtri NC: compatta la griglia NC
            // mantenendo intestazione e una riga, e nasconde temporaneamente i filtri CR. La preferenza dei filtri resta
            // invariata e i controlli riappaiono quando la finestra supera la soglia misurata.
            _tabellaAnomalieCompattaAutomaticamente = true;
            _filtriCrRidottiAutomaticamentePerSpazio = FiltriCr.IsVisible;
            FiltriCr.IsVisible = false;
            AnomalieGrid.MinHeight = AltezzaMinimaAnomalieViewportMinimo;
            if (ScorrimentoFiltriNc.IsVisible)
            {
                _filtriRidottiAutomaticamentePerSpazio = true;
                ImpostaPannelloFiltri(false);
            }
            _altezzaNecessariaPerFiltriAperti = Math.Max(altezzaNecessaria, AltezzaAreaFiltriAperti);
            Dispatcher.UIThread.Post(AdattaMinimi, DispatcherPriority.Loaded);
            return;
        }
        if (ScorrimentoFiltriNc.IsVisible && (spazioInsufficiente || viewportBasso))
        {
            // I filtri restano nella preferenza dell'operatore, ma un viewport basso li comprime per lasciare spazio a entrambe
            // le tabelle. Si riaprono automaticamente quando torna disponibile l'altezza misurata qui.
            _filtriRidottiAutomaticamentePerSpazio = true;
            _altezzaNecessariaPerFiltriAperti = Math.Max(altezzaNecessaria, AltezzaAreaFiltriAperti);
            ImpostaPannelloFiltri(false);
            Dispatcher.UIThread.Post(AdattaMinimi, DispatcherPriority.Loaded);
            return;
        }
        if (minimoCr > 0 && Math.Abs(righe[2].MinHeight - minimoCr) > 0.5) righe[2].MinHeight = minimoCr;
    }

    /// <summary>Fuori dalla tabella anomalie piu' la tabella al suo minimo. Si sommano le righe della griglia, non le misure della
    /// scheda: se la scheda e' troppo bassa la tabella sborda e la sua altezza non dice quanto occupano le altre parti.</summary>
    private double MinimoAnomalie()
    {
        if (CardAnomalie.Child is not Grid interna) return 0;
        var indiceTabella = Grid.GetRow(AnomalieGrid);
        var fuori = CardAnomalie.Padding.Top + CardAnomalie.Padding.Bottom + CardAnomalie.BorderThickness.Top + CardAnomalie.BorderThickness.Bottom
                    + AnomalieGrid.Margin.Top + AnomalieGrid.Margin.Bottom;
        var misurate = SommaRighe(interna, indiceTabella);
        return misurate > 0 ? fuori + misurate + AnomalieGrid.MinHeight : 0;   // 0: ancora senza layout, lo ripete il prossimo SizeChanged
    }

    /// <summary>Intestazione, filtri, paginazione e una riga CR. Le righe successive usano lo spazio disponibile senza
    /// diventare un vincolo che impedisce di ridimensionare le schede.</summary>
    private double MinimoCr()
    {
        if (CardChangeRequest.Child is not Grid interna) return 0;
        var indiceLista = Grid.GetRow(ListaCr);
        var fuori = CardChangeRequest.Padding.Top + CardChangeRequest.Padding.Bottom + CardChangeRequest.BorderThickness.Top + CardChangeRequest.BorderThickness.Bottom;
        var fissa = fuori + SommaRighe(interna, indiceLista);
        if (!(fissa > fuori)) return 0;   // ancora senza layout
        var altezzaIntestazione = ListaCr.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Select(i => i.Bounds.Height).FirstOrDefault(h => h > 0);
        if (altezzaIntestazione <= 0) altezzaIntestazione = 34;
        // Usa l'altezza della riga realizzata, quando disponibile, per evitare di assegnare alla lista un viewport in cui la
        // prima azione resta tagliata. Due pixel di margine coprono il bordo inferiore del DataGrid senza limitare lo splitter.
        var altezzaRiga = ListaCr.GetVisualDescendants().OfType<DataGridRow>()
            .Select(r => r.Bounds.Height).Where(h => h > 0).DefaultIfEmpty(PassoCrPredefinito).Max();
        ListaCr.MinHeight = altezzaIntestazione + Math.Max(PassoCrPredefinito, altezzaRiga) + 2;
        return fissa + ListaCr.MinHeight;
    }

    private static double SommaRighe(Grid griglia, int escludi)
    {
        var somma = 0.0;
        for (var i = 0; i < griglia.RowDefinitions.Count; i++)
            if (i != escludi) somma += griglia.RowDefinitions[i].ActualHeight;
        return somma;
    }

    /// <summary>Un chip per ogni filtro attivo. Il comando annulla solo quel filtro, con la stessa proprieta' del ViewModel.</summary>
    private static List<ChipFiltroLista> CostruisciChip(NonConformitaViewModel vm)
    {
        var chip = new List<ChipFiltroLista>();
        void Aggiungi(string chiave, string valore, Action rimuovi) =>
            chip.Add(new ChipFiltroLista($"{Traduci(chiave)}: {valore}", new RelayCommand(rimuovi)));

        if (!string.IsNullOrWhiteSpace(vm.FiltroTesto)) Aggiungi("TestListChipTesto", vm.FiltroTesto, () => vm.FiltroTesto = "");
        if (vm.FiltroStato is { } stato) Aggiungi("ColStatoAnomalia", Etichetta(WorkflowLabelConverter.Instance, stato), () => vm.FiltroStato = null);
        foreach (var (filtro, chiave) in FiltriMultiSelect(vm))
            if (filtro.HaSelezione) Aggiungi(chiave, filtro.TestoRiepilogo, () => filtro.DeselezionaTutto());
        if (!string.IsNullOrWhiteSpace(vm.FiltroTicket)) Aggiungi("EstaPlaceholder", vm.FiltroTicket, () => vm.FiltroTicket = "");
        if (vm.FiltroTicketEsta != FiltroPresenzaTicketEsta.Tutti)
            Aggiungi("FiltroPresenzaTicketEsta", Etichetta(FiltroTicketEstaLabelConverter.Instance, vm.FiltroTicketEsta),
                () => vm.FiltroTicketEsta = FiltroPresenzaTicketEsta.Tutti);
        if (vm.FiltroDataAperturaDa is { } aperturaDa) Aggiungi("AnomalieDataAperturaDa", Data(aperturaDa), () => vm.FiltroDataAperturaDa = null);
        if (vm.FiltroDataAperturaA is { } aperturaA) Aggiungi("AnomalieDataAperturaA", Data(aperturaA), () => vm.FiltroDataAperturaA = null);
        if (vm.FiltroDataChiusuraDa is { } chiusuraDa) Aggiungi("AnomalieDataChiusuraDa", Data(chiusuraDa), () => vm.FiltroDataChiusuraDa = null);
        if (vm.FiltroDataChiusuraA is { } chiusuraA) Aggiungi("AnomalieDataChiusuraA", Data(chiusuraA), () => vm.FiltroDataChiusuraA = null);
        return chip;
    }

    /// <summary>Filtri a selezione multipla con la chiave dell'etichetta mostrata nel chip (stessa etichetta del pulsante in pagina).</summary>
    private static (FiltroMultiSelect Filtro, string Chiave)[] FiltriMultiSelect(NonConformitaViewModel vm) =>
    [
        (vm.FiltroAssegnatario, "AnomalieFiltroAssegnatario"), (vm.FiltroGamma, "GammaVeicolareLabel"),
        (vm.FiltroVeicolo, "SottoveicoloLabel"), (vm.FiltroEcu, "ColEcu"), (vm.FiltroPriorita, "AnomalieFiltroPriorita"),
        (vm.FiltroGravita, "AnomalieFiltroGravita"), (vm.FiltroStatoAnomalia, "AnomalieFiltroStatoAnomalia"),
        (vm.FiltroFunzionalita, "AnomalieFiltroFunzionalita"),
    ];

    private static string Traduci(string chiave) => LocalizationService.Instance.Traduci(chiave);

    private static string Etichetta(IValueConverter convertitore, object valore) =>
        convertitore.Convert(valore, typeof(string), null, CultureInfo.CurrentCulture)?.ToString() ?? "";

    private static string Data(DateTimeOffset data) => data.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
