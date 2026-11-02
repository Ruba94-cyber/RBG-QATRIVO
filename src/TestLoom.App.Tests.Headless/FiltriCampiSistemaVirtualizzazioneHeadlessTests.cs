using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using TestLoom.App.Views.Controls;

namespace TestLoom.App.Tests;

/// <summary>Filtri dei campi di sistema in headless (Category=LayoutHeadless, Work 1): (1) cento campi dinamici in un viewport a
/// altezza finita, sia nel controllo sia nella Home e nel Report reali, con i contenitori realizzati limitati alla finestra prima e dopo lo
/// scorrimento; (2) un campo con 10.000 valori nel flyout di selezione: contenitori limitati, ricerca, tastiera, selezione multipla,
/// valore lontano (indice 9500) selezionabile e selezione che sopravvive a scorrimento e riciclo, "Seleziona tutto" con ricerca attiva.
/// Le misure (contenitori realizzati, tempi) finiscono su stdout (<c>FILTRI_VIRTUALIZZAZIONE|...</c>). Nessuna asserzione sul tempo.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class FiltriCampiSistemaVirtualizzazioneHeadlessTests
{
    private const int NumeroCampi = 100;
    private const int NumeroValori = 10_000;
    private const int IndiceLontano = 9_500;

    /// <summary>Viewport del controllo (MaxHeight 220): con righe di circa 36 px si realizzano una decina di righe, non cento.</summary>
    private const int LimiteRighePerViewport = 20;

    /// <summary>Tastiera (revisione 6): Invio o Spazio sulla riga a fuoco apre il filtro della riga, come il suo pulsante.</summary>
    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    public void FiltriCampiSistema_TastoSullaRiga_ApreIlFiltroDellaRiga(Key tasto) => LayoutHeadlessSessione.Esegui(() =>
    {
        var filtri = new ObservableCollection<FiltroCampoCustom>();
        var filtro = new FiltroCampoCustom { Chiave = "campo-tastiera", Etichetta = "Campo da tastiera" };
        filtro.Filtro.Ripopola(new[] { ("A", "A"), ("B", "B") });
        filtri.Add(filtro);
        var vista = new FiltriCampiSistemaView { Filtri = filtri };
        var finestra = new Window { Width = 1280, Height = 720, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            var lista = ListaFiltriSistema(vista);
            LayoutHeadlessSessione.Assesta(finestra);
            var riga = Contenitori<ListBoxItem>(lista).Single();
            var bottone = riga.GetVisualDescendants().OfType<Button>().Single();

            riga.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = tasto, Source = riga });

            Assert.NotNull(FlyoutBase.GetAttachedFlyout(bottone));
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void FiltriCampiSistema_CentoCampi_RealizzaSoloLeRigheDelViewportPrimaEDopoLoScorrimento() => LayoutHeadlessSessione.Esegui(() =>
    {
        var filtri = new ObservableCollection<FiltroCampoCustom>();
        for (var i = 0; i < NumeroCampi; i++)
        {
            var filtro = new FiltroCampoCustom { Chiave = $"campo-{i:000}", Etichetta = $"Campo di sistema {i:000}" };
            filtro.Filtro.Ripopola(new[] { ("A", "A"), ("B", "B") });
            filtri.Add(filtro);
        }
        var vista = new FiltriCampiSistemaView { Filtri = filtri };
        var finestra = new Window { Width = 1280, Height = 720, Content = vista };
        var sw = Stopwatch.StartNew();
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            var lista = ListaFiltriSistema(vista);
            LayoutHeadlessSessione.Assesta(finestra);
            var realizzatiPrima = Contenitori<ListBoxItem>(lista).Count;
            var tempoPrimoPasso = sw.Elapsed.TotalMilliseconds;

            lista.ScrollIntoView(filtri[^1]);
            LayoutHeadlessSessione.Assesta(finestra);
            var realizzatiDopo = Contenitori<ListBoxItem>(lista).Count;
            var ultimoRealizzato = lista.GetVisualDescendants().OfType<Button>()
                .Any(b => AutomationProperties.GetAutomationId(b) == $"FiltroCampoSistema_{filtri[^1].Chiave}_Button");

            Console.WriteLine($"FILTRI_VIRTUALIZZAZIONE|controllo|campi={NumeroCampi}|realizzati_prima={realizzatiPrima}|realizzati_dopo_scroll={realizzatiDopo}|ms_primo_passo={tempoPrimoPasso:0}|ms_totale={sw.Elapsed.TotalMilliseconds:0}");
            Assert.True(realizzatiPrima <= LimiteRighePerViewport, $"prima dello scorrimento realizzati {realizzatiPrima} contenitori su {NumeroCampi}");
            Assert.True(realizzatiDopo <= LimiteRighePerViewport, $"dopo lo scorrimento realizzati {realizzatiDopo} contenitori su {NumeroCampi}");
            Assert.True(ultimoRealizzato, "l'ultimo campo non e' realizzato dopo lo scorrimento in fondo");
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void HomeEReport_CentoCampiDiSistema_ViewportVirtualizzatoNellaPaginaReale() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = CreaStatoConCampi(NumeroCampi);
        LayoutAdattivo.ReimpostaStato();
        LayoutAdattivo.Aggiorna(1280, 672);
        var home = new HomeViewModel(state, _ => { });
        var report = new ReportViewModel(state);
        Assert.Equal(NumeroCampi, home.FiltriCampiCustomDashboard.Count);
        Assert.Equal(NumeroCampi, report.FiltriCampiCustomReport.Count);

        var vistaHome = new HomeView { DataContext = home };
        var finestraHome = new Window { Width = 1280, Height = 672, Content = vistaHome };
        var vistaReport = new ReportView { DataContext = report };
        var finestraReport = new Window { Width = 1280, Height = 672, Content = vistaReport };
        try
        {
            LayoutHeadlessSessione.Mostra(finestraHome);
            LayoutHeadlessSessione.Assesta(finestraHome);
            Assert.True(TrovaListaFiltriSistema(vistaHome) == null, "Home crea il controllo dei campi di sistema con i filtri avanzati chiusi");
            home.ToggleAltriFiltriDashboardCommand.Execute(null);
            LayoutHeadlessSessione.Assesta(finestraHome);
            var listaHome = ListaFiltriSistema(vistaHome);
            Assert.True(listaHome.IsEffectivelyVisible, "Home non mostra i campi di sistema aprendo i filtri avanzati");
            var homePrima = Contenitori<ListBoxItem>(listaHome).Count;
            listaHome.ScrollIntoView(home.FiltriCampiCustomDashboard[^1]);
            LayoutHeadlessSessione.Assesta(finestraHome);
            var homeDopo = Contenitori<ListBoxItem>(listaHome).Count;

            LayoutHeadlessSessione.Mostra(finestraReport);
            LayoutHeadlessSessione.Assesta(finestraReport);
            Assert.True(TrovaListaFiltriSistema(vistaReport) == null, "Report crea il controllo dei campi di sistema con i filtri avanzati chiusi");
            report.ToggleFiltriAvanzatiCommand.Execute(null);
            LayoutHeadlessSessione.Assesta(finestraReport);
            var listaReport = ListaFiltriSistema(vistaReport);
            Assert.True(listaReport.IsEffectivelyVisible, "Report non mostra i campi di sistema aprendo i filtri avanzati");
            var reportPrima = Contenitori<ListBoxItem>(listaReport).Count;
            listaReport.ScrollIntoView(report.FiltriCampiCustomReport[^1]);
            LayoutHeadlessSessione.Assesta(finestraReport);
            var reportDopo = Contenitori<ListBoxItem>(listaReport).Count;

            Console.WriteLine($"FILTRI_VIRTUALIZZAZIONE|home|campi={NumeroCampi}|realizzati_prima={homePrima}|realizzati_dopo_scroll={homeDopo}");
            Console.WriteLine($"FILTRI_VIRTUALIZZAZIONE|report|campi={NumeroCampi}|realizzati_prima={reportPrima}|realizzati_dopo_scroll={reportDopo}");
            Assert.True(homePrima <= LimiteRighePerViewport && homeDopo <= LimiteRighePerViewport, $"Home: realizzati {homePrima}/{homeDopo}");
            Assert.True(reportPrima <= LimiteRighePerViewport && reportDopo <= LimiteRighePerViewport, $"Report: realizzati {reportPrima}/{reportDopo}");
        }
        finally
        {
            finestraHome.Hide();
            finestraReport.Hide();
        }
    });

    /// <summary>Lista Test: i campi di sistema usabili come filtro sono contenuti nei filtri avanzati.</summary>
    [Fact]
    public void ListaTest_CampiSistemaNelPannelloFiltri_RichiedonoFiltriAvanzatiAperti() => LayoutHeadlessSessione.Esegui(() =>
    {
        var state = CreaStatoListaTestConCampi();
        LayoutAdattivo.ReimpostaStato();
        var lista = new TestCaseListViewModel(state, _ => { });
        var vista = new TestCaseListView { DataContext = lista };
        var finestra = new Window { Width = 1280, Height = 800, Content = vista };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            LayoutHeadlessSessione.Assesta(finestra);

            Assert.False(lista.MostraFiltriAvanzati);
            Assert.True(TrovaListaFiltriSistema(vista) == null, "il controllo dei campi di sistema viene creato con i filtri avanzati chiusi");

            lista.ToggleFiltriAvanzatiCommand.Execute(null);
            LayoutHeadlessSessione.Assesta(finestra);
            var elenco = ListaFiltriSistema(vista);
            Assert.True(elenco.IsEffectivelyVisible, "il pannello dei campi di sistema non compare aprendo i filtri avanzati");
            Assert.Equal(2, Contenitori<ListBoxItem>(elenco).Count);
            var pulsante = vista.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "FiltroCampoSistema_vci-connection_Button");

            lista.FiltriCampiCustomLista.Single(f => f.Chiave == "vci-connection").Filtro.Opzioni.Single(o => o.Valore == "VCI USB").Selezionato = true;
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.Equal("VCI USB", pulsante.Content);
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Master con due campi di sistema usabili come filtro (Lista con due opzioni, Testo) e nessun Test: basta a far comparire il pannello.</summary>
    private static AppState CreaStatoListaTestConCampi()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ListaTestCampiHeadless_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.CampiSistemaDettagli.Save(new List<CampoSistemaDefinizione>
        {
            new()
            {
                Chiave = "vci-connection", Etichetta = "VCI Connection", Gruppo = GruppoCampoSistema.Dettagli, Ruolo = RuoloCampoSistema.Nessuno,
                UsabileComeFiltro = true, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "VCI USB", "VCI LAN" },
            },
            new() { Chiave = "flotta", Etichetta = "Flotta", Gruppo = GruppoCampoSistema.Dettagli, Ruolo = RuoloCampoSistema.Nessuno, UsabileComeFiltro = true },
        });
        return state;
    }

    /// <summary>Misura di riferimento del contenitore precedente (ItemsControl con WrapPanel dentro uno ScrollViewer senza altezza): non
    /// virtualizza, quindi realizza tutti i campi. Il numero serve a confrontare il viewport attuale; non asserisce latenze.</summary>
    [Fact]
    public void FiltriCampiSistema_RiferimentoWrapPanel_RealizzaTuttiICampi() => LayoutHeadlessSessione.Esegui(() =>
    {
        var filtri = new ObservableCollection<FiltroCampoCustom>();
        for (var i = 0; i < NumeroCampi; i++)
        {
            var filtro = new FiltroCampoCustom { Chiave = $"campo-{i:000}", Etichetta = $"Campo di sistema {i:000}" };
            filtro.Filtro.Ripopola(new[] { ("A", "A"), ("B", "B") });
            filtri.Add(filtro);
        }
        var controllo = new ItemsControl
        {
            ItemsSource = filtri,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<FiltroCampoCustom>((f, _) => new Button { Content = f!.Etichetta }),
        };
        var finestra = new Window { Width = 1280, Height = 720, Content = new ScrollViewer { Content = controllo } };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            LayoutHeadlessSessione.Assesta(finestra);
            var realizzati = controllo.GetVisualDescendants().OfType<Button>().Count();
            Console.WriteLine($"FILTRI_VIRTUALIZZAZIONE|riferimento_wrappanel|campi={NumeroCampi}|realizzati={realizzati}");
            Assert.Equal(NumeroCampi, realizzati);
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void Flyout_DiecimilaValori_RealizzaPocheRigheELeSelezioniSopravvivonoAlloScorrimento() => LayoutHeadlessSessione.Esegui(() =>
    {
        var filtro = new FiltroMultiSelect();
        filtro.Ripopola(Enumerable.Range(0, NumeroValori).Select(i => ($"V{i:00000}", $"Valore {i:00000}")));
        var sw = Stopwatch.StartNew();
        var contenuto = FiltroMultiSelectVirtualizzato.Costruisci(filtro, null, FiltroMultiSelectFlyoutHelper.SogliaRicerca);
        var msCostruzione = sw.Elapsed.TotalMilliseconds;
        var finestra = new Window { Width = 420, Height = 640, Content = contenuto };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            var lista = ListaFlyout(contenuto);
            LayoutHeadlessSessione.Assesta(finestra);
            var realizzatiPrima = Contenitori<ListBoxItem>(lista).Count;

            // Valore lontano: selezione dalla tastiera (indice 9500), poi scorrimento.
            var lontano = filtro.Opzioni[IndiceLontano];
            lista.SelectedIndex = IndiceLontano;
            LayoutHeadlessSessione.Assesta(finestra);
            lista.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = lista });
            Assert.True(lontano.Selezionato, "la barra spaziatrice non seleziona il valore lontano");
            Assert.Contains(lontano.Valore, filtro.Selezionati);

            lista.ScrollIntoView(filtro.Opzioni[^1]);
            LayoutHeadlessSessione.Assesta(finestra);
            var realizzatiDopo = Contenitori<ListBoxItem>(lista).Count;

            lista.ScrollIntoView(lontano);
            LayoutHeadlessSessione.Assesta(finestra);
            var casella = CaselleDelValore(contenuto, lontano.Etichetta).SingleOrDefault();
            Assert.NotNull(casella);
            Assert.True(casella!.IsChecked == true, "la selezione del valore lontano non sopravvive allo scorrimento");

            // Riciclo: tornando in cima, la voce 0 non eredita la spunta della voce lontana.
            lista.ScrollIntoView(filtro.Opzioni[0]);
            LayoutHeadlessSessione.Assesta(finestra);
            var primaCasella = CaselleDelValore(contenuto, filtro.Opzioni[0].Etichetta).SingleOrDefault();
            Assert.NotNull(primaCasella);
            Assert.False(primaCasella!.IsChecked == true, "il riciclo del contenitore ha propagato la spunta");

            Console.WriteLine($"FILTRI_VIRTUALIZZAZIONE|flyout|valori={NumeroValori}|ms_costruzione={msCostruzione:0}|realizzati_prima={realizzatiPrima}|realizzati_dopo_scroll={realizzatiDopo}");
            Assert.True(realizzatiPrima <= LimiteRighePerViewport, $"flyout: realizzati {realizzatiPrima} contenitori su {NumeroValori}");
            Assert.True(realizzatiDopo <= LimiteRighePerViewport, $"flyout dopo scorrimento: realizzati {realizzatiDopo} su {NumeroValori}");
        }
        finally
        {
            finestra.Hide();
        }
    });

    [Fact]
    public void Flyout_DiecimilaValori_RicercaTastieraSelezioneMultiplaESelezionaTuttoConRicerca() => LayoutHeadlessSessione.Esegui(() =>
    {
        var filtro = new FiltroMultiSelect();
        filtro.Ripopola(Enumerable.Range(0, NumeroValori).Select(i => ($"V{i:00000}", $"Valore {i:00000}")));
        var contenuto = FiltroMultiSelectVirtualizzato.Costruisci(filtro, null, FiltroMultiSelectFlyoutHelper.SogliaRicerca);
        var finestra = new Window { Width = 420, Height = 640, Content = contenuto };
        try
        {
            LayoutHeadlessSessione.Mostra(finestra);
            var lista = ListaFlyout(contenuto);
            var ricerca = contenuto.GetVisualDescendants().OfType<TextBox>()
                .Single(t => AutomationProperties.GetAutomationId(t) == "FiltroMultiSelect_Ricerca_Input");

            // La ricerca restringe la lista a una sola voce (sottostringa su tutte le M opzioni), senza troncare la sorgente.
            ricerca.Text = "Valore 09500";
            AttendiLista(finestra, lista, count: 1);
            Assert.Equal(1, Cuenta(lista));
            Assert.Equal(NumeroValori, filtro.Opzioni.Count);

            // La selezione di massa agisce sulla sola voce visibile, senza toccare le selezioni nascoste.
            filtro.Opzioni[10].Selezionato = true;
            var seleziona = contenuto.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "FiltroMultiSelect_SelezionaTutto_Button");
            var deseleziona = contenuto.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetAutomationId(b) == "FiltroMultiSelect_DeselezionaTutto_Button");
            seleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.Equal(2, filtro.Selezionati.Count);
            Assert.Contains("V09500", filtro.Selezionati);
            Assert.Contains("V00010", filtro.Selezionati);

            // Una query intermedia deve essere cancellata. Durante il debounce la massa resta disabilitata;
            // dopo la seconda query agisce sul set completo mostrato, non sul risultato precedente o sui soli container realizzati.
            ricerca.Text = "Valore 00010";
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.False(seleziona.IsEnabled);
            Assert.False(deseleziona.IsEnabled);
            ricerca.Text = "Valore 09600";
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.False(seleziona.IsEnabled);
            AttendiRisultato(finestra, lista, "V09600");
            seleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.Equal(3, filtro.Selezionati.Count);
            Assert.Contains("V09600", filtro.Selezionati);

            deseleziona.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LayoutHeadlessSessione.Assesta(finestra);
            Assert.Equal(new[] { "V00010", "V09500" }, filtro.Selezionati.OrderBy(v => v));

            // Ricerca cancellata: di nuovo tutte le voci, e la voce lontana si seleziona ancora con la tastiera.
            ricerca.Text = "";
            AttendiLista(finestra, lista, count: NumeroValori);
            lista.SelectedIndex = IndiceLontano - 1;
            lista.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = lista });
            Assert.Equal(new[] { "V00010", "V09499", "V09500" }, filtro.Selezionati.OrderBy(v => v));
        }
        finally
        {
            finestra.Hide();
        }
    });

    /// <summary>Attende (pompando il dispatcher) che la ricerca asincrona applichi il numero di voci atteso.</summary>
    private static void AttendiLista(Window finestra, ListBox lista, int count)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limite && Cuenta(lista) != count)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            Thread.Sleep(25);
        }
        LayoutHeadlessSessione.Assesta(finestra);
        Assert.Equal(count, Cuenta(lista));
    }

    private static void AttendiRisultato(Window finestra, ListBox lista, string valore)
    {
        var limite = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < limite)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            var voci = lista.ItemsSource?.Cast<FiltroOpzione>().ToList();
            if (voci is { Count: 1 } && voci[0].Valore == valore) break;
            Thread.Sleep(10);
        }
        var applicate = lista.ItemsSource?.Cast<FiltroOpzione>().Select(o => o.Valore).ToList();
        Assert.Equal(new[] { valore }, applicate);
    }

    /// <summary>Numero di voci della lista (la sorgente puo' essere una lista di sola lettura).</summary>
    private static int Cuenta(ListBox lista) => lista.ItemsSource?.Cast<object>().Count() ?? -1;

    private static ListBox ListaFiltriSistema(Visual radice) => TrovaListaFiltriSistema(radice)!;

    private static ListBox? TrovaListaFiltriSistema(Visual radice) =>
        radice.GetVisualDescendants().OfType<ListBox>().SingleOrDefault(l => AutomationProperties.GetAutomationId(l) == "FiltriCampiSistema_Lista");

    private static ListBox ListaFlyout(Visual radice) =>
        radice.GetVisualDescendants().OfType<ListBox>().Single(l => AutomationProperties.GetAutomationId(l) == "FiltroMultiSelect_Lista");

    private static List<T> Contenitori<T>(ListBox lista) where T : Control =>
        lista.GetVisualDescendants().OfType<T>().ToList();

    private static IEnumerable<CheckBox> CaselleDelValore(Visual radice, string etichetta) =>
        radice.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(c => AutomationProperties.GetAutomationId(c) == $"FiltroMultiSelect_Voce_{etichetta}")
            .SelectMany(c => c.GetVisualDescendants().OfType<CheckBox>());

    /// <summary>Stato minimo con la cartella valida e <paramref name="campi"/> campi di sistema usabili come filtro.</summary>
    private static AppState CreaStatoConCampi(int campi)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_FiltriCampiSistema_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.CampiSistemaDettagli.Save(Enumerable.Range(0, campi).Select(i => new CampoSistemaDefinizione
        {
            Chiave = $"campo-{i:000}",
            Etichetta = $"Campo di sistema {i:000}",
            Gruppo = GruppoCampoSistema.Dettagli,
            Ruolo = RuoloCampoSistema.Nessuno,
            UsabileComeFiltro = true,
        }).ToList());
        return state;
    }
}
