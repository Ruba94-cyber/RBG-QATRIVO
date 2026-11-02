using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using System.Reflection;

namespace TestLoom.App.Tests;

/// <summary>Grafici in headless (Category=LayoutHeadless). Verifica la Home a 1280x672: la card resta entro il 45% della
/// pagina, il suo Image e' legato a quell'altezza, e la card usa una colonna. Verifica la finestra "Apri": titolo = grafico
/// + filtri, e il render che segue i ridimensionamenti (l'ultima richiesta vince dopo il debounce).
/// Il layout headless non assegna sempre le misure dei controlli annidati: le misure in pixel si controllano dal
/// ViewModel (<see cref="ChartRenderizzato.UltimaDimensione"/>), non dalla bitmap headless.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class GraficiHeadlessTests
{
    [Fact]
    public void Home_CardGrafico_A1280x672_RestaEntroIl45PercentoELegataAllAltezzaCalcolata() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1280, 672);
            var home = new HomeViewModel(TestStateFactory.Create(), _ => { });
            var card = CartaDonut();
            home.ChartsAttivi.Add(card);
            home.ImpostaAltezzaVisibile(672);
            card.ImpostaLarghezza(1000);
            Assert.Equal(1, home.ChartColumns); // layout Compatto: un grafico per riga

            var limite = 672 * ChartRenderizzato.FrazioneAltezzaMassima;
            Assert.True(card.AltezzaGrafico <= limite + 0.5, $"altezza card {card.AltezzaGrafico} sopra il 45% della pagina ({limite})");

            var vista = new HomeView { DataContext = home };
            var finestra = new Window { Width = 1280, Height = 672, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Image? immagine = null;
                Assert.True(AttendiCondizione(() =>
                {
                    immagine = vista.GetVisualDescendants().OfType<Image>()
                        .FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == card.AutomationId);
                    return immagine != null;
                }, finestra), "immagine della card non creata");
                // Il binding dell'altezza dell'Image segue il ViewModel (anche se l'altezza della pagina cambia durante il caricamento).
                Assert.True(AttendiCondizione(() => Math.Abs(immagine!.Height - card.AltezzaGrafico) < 1, finestra),
                    $"altezza dell'immagine {immagine!.Height} diversa da quella della card {card.AltezzaGrafico}");
                Assert.True(immagine.Height <= limite + 0.5, $"altezza dell'immagine {immagine.Height} sopra il 45% della pagina");

                // Il render segue la dimensione della card: la dimensione applicata corrisponde alla richiesta.
                card.RichiediRender(1000, card.AltezzaGrafico, 1);
                Assert.True(AttendiCondizione(() => card.UltimaDimensione != null, finestra), "render della card non applicato");
                Assert.Equal(1000, card.UltimaDimensione!.Value.Larghezza, 1);
                Assert.True(card.UltimaDimensione.Value.Altezza <= limite + 0.5,
                    $"render alto {card.UltimaDimensione.Value.Altezza}, oltre il 45% della pagina");
            }
            finally
            {
                finestra.Close();
            }
        });

    [Fact]
    public void GraficiCard_RidimensionamentoIndipendenteELayoutPersistitoPerOperatoreEId() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var riferimentoStatoViewState = typeof(ViewStateService).GetField("_statoLocale", BindingFlags.Static | BindingFlags.NonPublic)!;
            var statoPrecedente = riferimentoStatoViewState.GetValue(null) as AppState;
            var operatorePrecedente = statoPrecedente?.Config.OperatoreAttivo;
            var stato = TestStateFactory.Create();
            stato.Config.OperatoreAttivo = "Operatore A";
            ViewStateService.Configura(stato);
            ViewStateService.ScriviPerOperatore(GraficoCard.ChiaveDimensioniSalvate(CartaPerId("copertura")), "520;260");
            ViewStateService.ScriviPerOperatore(GraficoCard.ChiaveDimensioniSalvate(CartaPerId("andamento")), "740;380");

            var immagineIniziale = ImmagineSegnaposto();
            var copertura = CartaDonut("copertura", immagineIniziale);
            var andamento = CartaPerId("andamento");
            var home = new HomeViewModel(stato, _ => { });
            home.ChartsAttivi.Add(copertura);
            home.ChartsAttivi.Add(andamento);
            var vistaHome = new HomeView { DataContext = home };
            var finestra = new Window
            {
                Width = 1600,
                Height = 1600,
                Content = vistaHome,
            };

            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                var vistaCopertura = vistaHome.GetVisualDescendants().OfType<GraficoCard>()
                    .Single(v => ReferenceEquals(v.DataContext, copertura));
                var vistaAndamento = vistaHome.GetVisualDescendants().OfType<GraficoCard>()
                    .Single(v => ReferenceEquals(v.DataContext, andamento));
                Assert.Equal(520, copertura.LarghezzaCard, 1);
                Assert.Equal(260, copertura.AltezzaGrafico, 1);
                Assert.Equal(740, andamento.LarghezzaCard, 1);
                Assert.Equal(380, andamento.AltezzaGrafico, 1);

                // Simula il risultato del trascinamento: modificare la larghezza non lega l'altezza al suo rapporto,
                // e il salvataggio non altera la card con ID differente.
                vistaCopertura.ApplicaDimensioniRidimensionate(640, 310);
                LayoutHeadlessSessione.Assesta(finestra);
                vistaCopertura.SalvaDimensioniPersonalizzate();
                Assert.Equal(640, copertura.LarghezzaCard, 1);
                Assert.Equal(310, copertura.AltezzaGrafico, 1);
                Assert.Equal("640;310", ViewStateService.LeggiPerOperatore(GraficoCard.ChiaveDimensioniSalvate(copertura)));
                Assert.Equal("740;380", ViewStateService.LeggiPerOperatore(GraficoCard.ChiaveDimensioniSalvate(andamento)));
                var immagine = vistaCopertura.FindControl<Image>("ImmagineCard")!;
                var contenitore = vistaCopertura.FindControl<Border>("Contenitore")!;
                var pannello = vistaCopertura.GetVisualAncestors().OfType<WrapPanel>().FirstOrDefault();
                Assert.True(pannello != null && pannello.Bounds.Width >= 640,
                    $"WrapPanel non disponibile o troppo stretto: {pannello?.Bounds}");
                Assert.True(contenitore.Bounds.Width >= 640,
                    $"contenitore card limitato: bounds={contenitore.Bounds}, maxWidth={contenitore.MaxWidth}");
                Assert.True(AttendiCondizione(() => Math.Abs(immagine.Bounds.Height - 310) < 1, finestra),
                    $"il bounds visibile non segue l'altezza scelta: Height={immagine.Height}, bounds={immagine.Bounds.Width}x{immagine.Bounds.Height}; " +
                    string.Join(" <- ", immagine.GetVisualAncestors().OfType<Control>().Take(5)
                        .Select(c => $"{c.GetType().Name}={c.Bounds.Width}x{c.Bounds.Height} (Height={c.Height}, MaxHeight={c.MaxHeight})")));
                copertura.RichiediRender(immagine.Bounds.Width, immagine.Bounds.Height, 1);
                Assert.True(AttendiCondizione(() => copertura.UltimaDimensione is { } dimensione &&
                    Math.Abs(dimensione.Altezza - immagine.Bounds.Height) < 1, finestra),
                    $"il render asincrono non ha applicato le nuove dimensioni della card; immagine={immagine.Bounds}, ultima={copertura.UltimaDimensione}, indisponibile={copertura.NonDisponibile}");
                Assert.Equal(immagine.Bounds.Width, copertura.UltimaDimensione!.Value.Larghezza, 1);

                var ricaricata = CartaPerId("copertura");
                var vistaRicaricata = new GraficoCard { DataContext = ricaricata };
                vistaRicaricata.CaricaDimensioniSalvate();
                Assert.Equal(640, ricaricata.LarghezzaCard, 1);
                Assert.Equal(310, ricaricata.AltezzaGrafico, 1);

                // La perdita della cattura conclude il trascinamento e salva la misura corrente;
                // eventi PointerMoved successivi non devono proseguire il ridimensionamento.
                SimulaRidimensionamentoConPerditaCattura(vistaCopertura, finestra, 24, 18);
                var larghezzaDopoPerdita = copertura.LarghezzaCard;
                var altezzaDopoPerdita = copertura.AltezzaGrafico;
                Assert.Equal($"{larghezzaDopoPerdita:0.##};{altezzaDopoPerdita:0.##}",
                    ViewStateService.LeggiPerOperatore(GraficoCard.ChiaveDimensioniSalvate(copertura)));
                Assert.Equal((larghezzaDopoPerdita, altezzaDopoPerdita), DimensioniDopoSpostamento(vistaCopertura, finestra, 200, 200));

                stato.Config.OperatoreAttivo = "Operatore B";
                var preferenzeAltroOperatore = CartaPerId("copertura");
                var vistaAltroOperatore = new GraficoCard { DataContext = preferenzeAltroOperatore };
                vistaAltroOperatore.CaricaDimensioniSalvate();
                Assert.True(double.IsNaN(preferenzeAltroOperatore.LarghezzaCard),
                    "la dimensione dell'operatore A e' stata condivisa con l'operatore B");
                Assert.Null(ViewStateService.LeggiPerOperatore(GraficoCard.ChiaveDimensioniSalvate(preferenzeAltroOperatore)));
                preferenzeAltroOperatore.Dispose();
            }
            finally
            {
                finestra.Close();
                copertura.Dispose();
                andamento.Dispose();
                immagineIniziale.Dispose();
                if (statoPrecedente == null)
                    riferimentoStatoViewState.SetValue(null, null);
                else
                {
                    statoPrecedente.Config.OperatoreAttivo = operatorePrecedente ?? string.Empty;
                    ViewStateService.Configura(statoPrecedente);
                }
            }
        });

    private static void SimulaRidimensionamentoConPerditaCattura(GraficoCard vista, Window finestra, double deltaX, double deltaY)
    {
        var maniglia = vista.FindControl<Border>("ManigliaAngolo")!;
        var puntatore = new Avalonia.Input.Pointer(91, PointerType.Mouse, true);
        var inizio = maniglia.TranslatePoint(new Point(maniglia.Bounds.Width / 2, maniglia.Bounds.Height / 2), finestra)
            ?? throw new InvalidOperationException("La maniglia del grafico non e' collegata alla finestra.");
        var premute = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        maniglia.RaiseEvent(new PointerPressedEventArgs(maniglia, puntatore, finestra, inizio, 1, premute, KeyModifiers.None, 1));
        Assert.Same(maniglia, puntatore.Captured);
        var trascinamento = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var destinazione = new Point(inizio.X + deltaX, inizio.Y + deltaY);
        maniglia.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, maniglia, puntatore, finestra,
            destinazione, 2, trascinamento, KeyModifiers.None));
        puntatore.Capture(null);
        Assert.Null(puntatore.Captured);
    }

    private static (double Width, double Height) DimensioniDopoSpostamento(GraficoCard vista, Window finestra, double deltaX, double deltaY)
    {
        var maniglia = vista.FindControl<Border>("ManigliaAngolo")!;
        var puntatore = new Avalonia.Input.Pointer(92, PointerType.Mouse, true);
        var punto = maniglia.TranslatePoint(new Point(maniglia.Bounds.Width / 2, maniglia.Bounds.Height / 2), finestra)
            ?? throw new InvalidOperationException("La maniglia del grafico non e' collegata alla finestra.");
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        maniglia.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, maniglia, puntatore, finestra,
            new Point(punto.X + deltaX, punto.Y + deltaY), 3, props, KeyModifiers.None));
        return ((vista.DataContext as ChartRenderizzato)!.LarghezzaCard, (vista.DataContext as ChartRenderizzato)!.AltezzaGrafico);
    }

    [Fact]
    public void GraficoCard_DimensioniPersonalizzateRispettanoLimitiEIlResetRipristinaIlLayoutAdattivo()
    {
        var card = CartaDonut();
        card.ImpostaLarghezza(680);
        card.ImpostaAltezzaVisibile(800);
        card.ImpostaDimensioniPersonalizzate(100, 4000);
        Assert.Equal(ChartRenderizzato.LarghezzaCardMinima, card.LarghezzaCard);
        Assert.Equal(ChartRenderizzato.AltezzaCardMassima, card.AltezzaGrafico);

        card.ImpostaDimensioniPersonalizzate(2000, 120);
        Assert.Equal(ChartRenderizzato.LarghezzaCardMassima, card.LarghezzaCard);
        Assert.Equal(ChartRenderizzato.AltezzaCardMinima, card.AltezzaGrafico);

        card.ReimpostaDimensioniPersonalizzate();
        Assert.True(double.IsNaN(card.LarghezzaCard));
        Assert.Equal(340, card.AltezzaGrafico, 1);
    }

    [Fact]
    public void GraficoWindow_TitoloERidisegnoAlRidimensionamento() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var snapshot = new GraficoSnapshot(HomeChartCatalog.IdAndamento, "Andamento anomalie",
                new DatiAndamento(Enumerable.Range(0, 30).Select(i => (new DateTime(2026, 1, 1).AddDays(i * 2), 10 + i % 5, i / 3)).ToList()),
                TipoGrafico.Linea);
            var vm = new GraficoWindowViewModel(snapshot, "Componente: componente-A");
            var finestra = new GraficoWindow(vm, null) { Width = 800, Height = 600 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assert.Equal("Andamento anomalie — componente: componente-A", finestra.Title);

                vm.RichiediRender(800, 500, 1);
                Assert.True(AttendiCondizione(() => vm.UltimaDimensione?.Larghezza == 800, finestra), "render iniziale non applicato");

                // Ridimensionamento in rapida successione: il debounce applica solo l'ultima dimensione richiesta.
                vm.RichiediRender(1000, 600, 1);
                vm.RichiediRender(1200, 800, 1);
                Assert.True(AttendiCondizione(() => vm.UltimaDimensione?.Larghezza == 1200, finestra), "il grafico non si e' ridisegnato alla nuova dimensione");
                Assert.Equal(800, vm.UltimaDimensione!.Value.Altezza, 1);
            }
            finally
            {
                finestra.Close();
            }
        });

    [Fact]
    public void GraficoWindow_CostruttoreSenzaParametri_CreaUnaFinestraSegnaposto() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            // Il costruttore senza parametri serve solo al designer Avalonia (AVLN3001): non deve mai lanciare.
            var finestra = new GraficoWindow();
            Assert.IsType<GraficoWindowViewModel>(finestra.DataContext);
            finestra.Close();
        });

    [Fact]
    public void GraficoWindow_AperturaSenzaRichiestaManuale_RendeIlGraficoAllaDimensioneDellaFinestra() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            // Come "Apri" dalla Home: la finestra si mostra con lo snapshot della card, senza richieste di render dal test.
            var vm = new GraficoWindowViewModel(SnapshotAndamento(), "Componente: componente-A");
            var finestra = new GraficoWindow(vm, null) { Width = 800, Height = 600 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                var diagnosi = $"bounds immagine {ImmagineFinestra(finestra).Bounds}, UltimaDimensione {vm.UltimaDimensione}";
                Assert.True(AttendiCondizione(() => vm.Immagine != null, finestra), $"nessuna immagine dopo l'apertura ({diagnosi})");
                Assert.True(vm.Immagine!.PixelSize.Width > 0 && vm.Immagine.PixelSize.Height > 0, "immagine senza pixel");
            }
            finally
            {
                finestra.Close();
            }
        });

    private static GraficoSnapshot SnapshotAndamento() => new(HomeChartCatalog.IdAndamento, "Andamento anomalie",
        new DatiAndamento(Enumerable.Range(0, 30).Select(i => (new DateTime(2026, 1, 1).AddDays(i * 2), 10 + i % 5, i / 3)).ToList()),
        TipoGrafico.Linea);

    private static Image ImmagineFinestra(Window finestra) => finestra.GetVisualDescendants().OfType<Image>()
        .Single(i => AutomationProperties.GetAutomationId(i) == "GraficoFinestra_Immagine");

    [Fact]
    public void GraficoWindow_RidimensionamentoRidisegnaAllaNuovaArea() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var vm = new GraficoWindowViewModel(SnapshotAndamento(), "Componente: componente-A");
            var finestra = new GraficoWindow(vm, null) { Width = 800, Height = 600 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assert.True(AttendiCondizione(() => vm.UltimaDimensione != null, finestra), "render iniziale non applicato");
                var iniziale = vm.UltimaDimensione!.Value.Larghezza;

                finestra.Width = 1100;
                Assert.True(AttendiCondizione(() => vm.UltimaDimensione?.Larghezza > iniziale + 200, finestra),
                    $"il grafico non si e' ridisegnato alla nuova larghezza (iniziale {iniziale}, ultima {vm.UltimaDimensione?.Larghezza})");
            }
            finally
            {
                finestra.Close();
            }
        });

    [Fact]
    public void GraficoWindow_RenderFallito_MostraGraficoNonDisponibileERegistraNelLog() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            // Dati che il renderer non sa disegnare: il fallimento e' deterministico e non deve lasciare un'area vuota.
            var snapshot = new GraficoSnapshot(HomeChartCatalog.IdAndamento, "Grafico guasto", new DatiNonSupportati(), TipoGrafico.Linea);
            var vm = new GraficoWindowViewModel(snapshot, "");
            var finestra = new GraficoWindow(vm, null) { Width = 800, Height = 600 };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assert.True(AttendiCondizione(() => vm.NonDisponibile, finestra), "fallimento di rendering non notificato");
                Assert.Null(vm.Immagine);
                Assert.False(ImmagineFinestra(finestra).IsVisible);
                Assert.True(MessaggioNonDisponibileVisibile(finestra), "messaggio 'Grafico non disponibile' non mostrato");
                Assert.Contains("Rendering del grafico", LeggiCrashLog());
            }
            finally
            {
                finestra.Close();
            }
        });

    [Fact]
    public void GraficoCard_ApriDisattivatoSenzaDati() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            var senzaDati = new ChartRenderizzato { Id = HomeChartCatalog.IdAndamento, Tipo = TipoGrafico.Linea, Titolo = "Andamento" };
            var conDati = CartaDonut();
            var finestra = new Window { Width = 800, Height = 600, Content = new GraficoCard { DataContext = senzaDati } };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                Assert.False(PulsanteApri(finestra, senzaDati).IsEnabled, "Apri attivo senza dati: il click non farebbe nulla");

                finestra.Content = new GraficoCard { DataContext = conDati };
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(PulsanteApri(finestra, conDati).IsEnabled, "Apri disattivato con dati");
            }
            finally
            {
                finestra.Close();
                senzaDati.Dispose();
                conDati.Dispose();
            }
        });

    private static Button PulsanteApri(Window finestra, ChartRenderizzato card) => finestra.GetVisualDescendants().OfType<Button>()
        .Single(b => AutomationProperties.GetAutomationId(b) == card.ApriAutomationId);

    private static bool MessaggioNonDisponibileVisibile(Window finestra) => finestra.GetVisualDescendants().OfType<TextBlock>()
        .Any(t => AutomationProperties.GetAutomationId(t) == "GraficoFinestra_NonDisponibile" && t.IsVisible);

    private static string LeggiCrashLog()
    {
        var percorso = Path.Combine(CrashLogService.LogDirectory, "crash.log");
        if (!File.Exists(percorso)) return "";
        using var flusso = new FileStream(percorso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var lettore = new StreamReader(flusso);
        return lettore.ReadToEnd();
    }

    /// <summary>Dati che il renderer non supporta: il rendering lancia un'eccezione.</summary>
    private sealed record DatiNonSupportati() : DatiGrafico("non_supportato");

    private static ChartRenderizzato CartaDonut(string id = HomeChartCatalog.IdDonut, Bitmap? immagine = null) => new()
    {
        Id = id,
        Tipo = TipoGrafico.Ciambella,
        Titolo = "Esiti per sottoveicolo",
        Immagine = immagine,
        Dati = new DatiEsiti(
            Enumerable.Range(1, 12).Select(i => ($"Componente-{i:00}", (long)(60 - i * 3))).ToList(),
            new List<(Esito Esito, long Valore)> { (Esito.Conforme, 300), (Esito.ConformeConDeviazioni, 40), (Esito.NonConforme, 25), (Esito.DaEseguire, 60) },
            Enumerable.Range(1, 12).SelectMany(i => new[]
            {
                ($"Componente-{i:00}", Esito.Conforme, (long)(40 - i)),
                ($"Componente-{i:00}", Esito.NonConforme, (long)(i % 3)),
            }).ToList()),
        Filtri = "nessun filtro",
    };

    private static Bitmap ImmagineSegnaposto() => GraficoBitmap.Decodifica(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII="))
        ?? throw new InvalidOperationException("Impossibile decodificare il PNG segnaposto del test.");

    private static ChartRenderizzato CartaPerId(string id) => new() { Id = id, Titolo = id };

    /// <summary>Attende sul thread UI che una condizione diventi vera, eseguendo i job in coda e un passaggio di layout a ogni giro.</summary>
    private static bool AttendiCondizione(Func<bool> condizione, TopLevel finestra, int millisecondi = 10_000)
    {
        var scadenza = DateTime.UtcNow.AddMilliseconds(millisecondi);
        while (DateTime.UtcNow < scadenza)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            if (condizione()) return true;
            Thread.Sleep(20);
        }
        LayoutHeadlessSessione.Assesta(finestra);
        return condizione();
    }
}
