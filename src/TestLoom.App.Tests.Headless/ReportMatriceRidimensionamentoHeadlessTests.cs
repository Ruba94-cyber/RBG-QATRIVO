using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class ReportMatriceRidimensionamentoHeadlessTests
{
    [Fact]
    public void BordiColonnaRigaEViewport_PreferenzePerOperatore_LimitiECap50x12()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato();
            stato.State.Config.OperatoreAttivo = "Operatore A";
            var vm = new ReportViewModel(stato.State);
            var vista = new ReportView { DataContext = vm };
            var finestra = new Window { Width = 700, Height = 672, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                AttendiMatrice(vm, finestra);

                Assert.DoesNotContain(Id(vista), id => id.StartsWith("Report_Matrice_Handle_", StringComparison.Ordinal));
                Assert.True(vm.ColonneMatrice.Count >= 2);
                Assert.True(vm.RigheMatrice.Count >= 2);
                Assert.InRange(vm.RigheMatrice.Count, 1, 50);
                Assert.InRange(vm.ColonneMatrice.Count, 1, 12);
                Assert.InRange(Id(vista).Count(id => id.StartsWith("ReportMatrice_Cella_", StringComparison.Ordinal)), 1, 50 * 12);

                var intestazioni = vista.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Tag is SegmentoIntestazione { Ridimensionabile: true })
                    .ToList();
                var primaIntestazione = intestazioni.First(b => ((SegmentoIntestazione)b.Tag!).ChiaveDimensione == vm.ColonneMatrice[0]);
                var secondaColonna = vm.ColonneMatrice[1];
                Assert.True(primaIntestazione.Focusable, "l'intestazione non e' raggiungibile da tastiera");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(primaIntestazione)), "l'intestazione non ha un nome accessibile");

                var colonnePrimaRidimensionamento = vm.ColonneMatrice.Count;
                var larghezzaIniziale = vm.LarghezzaPerColonnaMatrice(vm.ColonneMatrice[0]);
                Drag(vista, finestra, primaIntestazione,
                    new Point(primaIntestazione.Bounds.Width - 1, primaIntestazione.Bounds.Height / 2), 200, 0);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(vm.ColonneMatrice.Count < colonnePrimaRidimensionamento,
                    "il resize riduce la finestra virtualizzata delle colonne");
                Assert.Equal(larghezzaIniziale + 200, Cella(vista, vm.RigheMatrice[0].Riga, vm.ColonneMatrice[0]).Bounds.Width, 1);
                Assert.Equal(ReportViewModel.LarghezzaCellaMatricePredefinita, vm.LarghezzaPerColonnaMatrice(secondaColonna), 1);

                primaIntestazione = vista.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Tag is SegmentoIntestazione { Ridimensionabile: true } segmento
                        && segmento.ChiaveDimensione == vm.ColonneMatrice[0]);
                primaIntestazione.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = primaIntestazione });
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(larghezzaIniziale + 208, Cella(vista, vm.RigheMatrice[0].Riga, vm.ColonneMatrice[0]).Bounds.Width, 1);

                var primaRigaKey = vm.RigheMatrice[0].Riga;
                var secondaRigaKey = vm.RigheMatrice[1].Riga;
                var primaRiga = Riga(vista, primaRigaKey);
                Assert.True(primaRiga.Focusable, "la riga non e' raggiungibile da tastiera");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(primaRiga)), "la riga non ha un nome accessibile");
                var linguaOriginale = LocalizationService.Instance.Lingua;
                try
                {
                    LocalizationService.Instance.Lingua = "en";
                    LayoutHeadlessSessione.Assesta(finestra);
                    Assert.Equal($"Resize column {((SegmentoIntestazione)primaIntestazione.Tag!).Etichetta}",
                        AutomationProperties.GetName(primaIntestazione));
                    Assert.Equal($"Resize row height {primaRigaKey}", AutomationProperties.GetName(primaRiga));
                }
                finally { LocalizationService.Instance.Lingua = linguaOriginale; }

                Drag(vista, finestra, primaRiga,
                    new Point(primaRiga.Bounds.Width / 2, primaRiga.Bounds.Height - 1), 0, 14);
                LayoutHeadlessSessione.Assesta(finestra);
                var primaRigaRidimensionata = Riga(vista, primaRigaKey);
                Assert.Equal(ReportViewModel.AltezzaRigaMatricePredefinita + 14, primaRigaRidimensionata.Bounds.Height, 1);
                Assert.Equal(ReportViewModel.AltezzaRigaMatricePredefinita, Riga(vista, secondaRigaKey).Bounds.Height, 1);
                primaRigaRidimensionata.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = primaRigaRidimensionata });
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(ReportViewModel.AltezzaRigaMatricePredefinita + 22, Riga(vista, primaRigaKey).Bounds.Height, 1);

                var viewportEdge = PerId(vista, "Report_Matrice_Viewport_ResizeEdge").Single();
                var altezzaViewportIniziale = vm.AltezzaViewportMatrice;
                DragConPerditaCattura(vista, finestra, viewportEdge, 0, 28);
                Assert.Equal(altezzaViewportIniziale + 28, vm.AltezzaViewportMatrice, 1);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(vm.AltezzaViewportMatrice, PerId(vista, "Report_Matrice_Viewport_Grid").Single().Bounds.Height, 1);

                vm.SalvaDimensioniMatrice();
                using (var preferenza = JsonDocument.Parse(ViewStateService.Leggi(stato.State,
                           "Report.DimensioniMatrice.v2|Operatore A")!))
                {
                    Assert.Equal(2, preferenza.RootElement.GetProperty("Versione").GetInt32());
                    Assert.Equal(larghezzaIniziale + 208,
                        preferenza.RootElement.GetProperty("LarghezzeColonne").GetProperty(vm.ColonneMatrice[0]).GetDouble(), 1);
                    Assert.Equal(ReportViewModel.AltezzaRigaMatricePredefinita + 22,
                        preferenza.RootElement.GetProperty("AltezzeRighe").GetProperty(primaRigaKey).GetDouble(), 1);
                    Assert.Equal(altezzaViewportIniziale + 28, preferenza.RootElement.GetProperty("AltezzaViewport").GetDouble(), 1);
                }

                var vmOperatoreA = new ReportViewModel(stato.State);
                Assert.Equal(larghezzaIniziale + 208, vmOperatoreA.LarghezzaPerColonnaMatrice(vm.ColonneMatrice[0]), 1);
                Assert.Equal(ReportViewModel.LarghezzaCellaMatricePredefinita, vmOperatoreA.LarghezzaPerColonnaMatrice(secondaColonna), 1);
                Assert.Equal(ReportViewModel.AltezzaRigaMatricePredefinita + 22, vmOperatoreA.AltezzaPerRigaMatrice(primaRigaKey), 1);
                Assert.Equal(altezzaViewportIniziale + 28, vmOperatoreA.AltezzaViewportMatrice, 1);

                stato.State.Config.OperatoreAttivo = "Operatore B";
                var vmOperatoreB = new ReportViewModel(stato.State);
                Assert.Equal(ReportViewModel.LarghezzaCellaMatricePredefinita, vmOperatoreB.LarghezzaPerColonnaMatrice(vm.ColonneMatrice[0]));
                Assert.Equal(ReportViewModel.AltezzaRigaMatricePredefinita, vmOperatoreB.AltezzaPerRigaMatrice(primaRigaKey));
                Assert.Equal(ReportViewModel.AltezzaViewportMatricePredefinita, vmOperatoreB.AltezzaViewportMatrice);

                vmOperatoreB.ImpostaLarghezzaColonnaMatrice(vm.ColonneMatrice[0], 1);
                vmOperatoreB.ImpostaAltezzaRigaMatrice(primaRigaKey, 1);
                Assert.Equal(ReportViewModel.LarghezzaCellaMatriceMinima, vmOperatoreB.LarghezzaPerColonnaMatrice(vm.ColonneMatrice[0]));
                Assert.Equal(ReportViewModel.AltezzaRigaMatriceMinima, vmOperatoreB.AltezzaPerRigaMatrice(primaRigaKey));
                vmOperatoreB.AggiornaDimensioniViewportMatrice(10000, 10000);
                Assert.Equal(50, vmOperatoreB.RigheVisibiliMatrice);
                Assert.Equal(12, vmOperatoreB.ColonneVisibiliMatrice);

                ViewStateService.Scrivi(stato.State, "Report.DimensioniMatrice.v1|Operatore Legacy",
                    "{\"Versione\":1,\"LarghezzaColonna\":120,\"AltezzaRiga\":60,\"AltezzaViewport\":640}");
                stato.State.Config.OperatoreAttivo = "Operatore Legacy";
                var vmMigrato = new ReportViewModel(stato.State);
                Assert.Equal(120, vmMigrato.LarghezzaCellaMatrice, 1);
                Assert.Equal(60, vmMigrato.AltezzaRigaMatrice, 1);
                Assert.Equal(640, vmMigrato.AltezzaViewportMatrice, 1);
            }
            finally
            {
                finestra.Hide();
            }
        });
    }

    [Fact]
    public void ColonneERigheIngrossate_AggiornanoLaFinestraEIlLimiteDiScorrimento()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato();
            var vm = new ReportViewModel(stato.State);
            var vista = new ReportView { DataContext = vm };
            var finestra = new Window { Width = 1280, Height = 672, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                AttendiMatrice(vm, finestra);
                finestra.Hide();
                vm.AggiornaDimensioniViewportMatrice(10000, 10000);
                var tutteLeRighe = vm.RigheMatrice.Select(r => r.Riga).ToList();
                var tutteLeColonne = vm.ColonneMatrice.ToList();
                Assert.True(tutteLeRighe.Count >= 2);
                Assert.True(tutteLeColonne.Count >= 2);
                var ultimaRiga = tutteLeRighe[^1];
                var ultimaColonna = tutteLeColonne[^1];

                vm.AggiornaDimensioniViewportMatrice(800, 400);
                var righePrima = vm.RigheVisibiliMatrice;
                var colonnePrima = vm.ColonneVisibiliMatrice;
                foreach (var riga in tutteLeRighe)
                    vm.ImpostaAltezzaRigaMatrice(riga, ReportViewModel.AltezzaRigaMatriceMassima);
                foreach (var colonna in tutteLeColonne)
                    vm.ImpostaLarghezzaColonnaMatrice(colonna, ReportViewModel.LarghezzaCellaMatriceMassima);

                Assert.True(vm.RigheVisibiliMatrice < righePrima, "l'aumento dell'altezza riduce il numero di righe materializzate");
                Assert.True(vm.ColonneVisibiliMatrice < colonnePrima, "l'aumento della larghezza riduce il numero di colonne materializzate");
                Assert.True(vm.MassimoOffsetRigheMatrice > 0);
                Assert.True(vm.MassimoOffsetColonneMatrice > 0);

                var offsetRigheFinale = vm.MassimoOffsetRigheMatrice;
                vm.OffsetRigheMatrice = offsetRigheFinale;
                Assert.True(vm.OffsetRigheMatrice == offsetRigheFinale,
                    $"offset righe non applicato: richiesto {offsetRigheFinale}, attuale {vm.OffsetRigheMatrice}, massimo {vm.MassimoOffsetRigheMatrice}");
                var offsetColonneFinale = vm.MassimoOffsetColonneMatrice;
                vm.OffsetColonneMatrice = offsetColonneFinale;
                Assert.Equal(offsetColonneFinale, vm.OffsetColonneMatrice);
                Assert.True(vm.OffsetRigheMatrice == offsetRigheFinale,
                    $"scroll colonne ha reimpostato righe: richiesto {offsetRigheFinale}, attuale {vm.OffsetRigheMatrice}, massimo {vm.MassimoOffsetRigheMatrice}");
                Assert.True(ultimaRiga == vm.RigheMatrice[^1].Riga,
                    $"valore terminale riga {ultimaRiga} non raggiunto; offset {vm.OffsetRigheMatrice}/{vm.MassimoOffsetRigheMatrice}; visibili: {string.Join(", ", vm.RigheMatrice.Select(r => r.Riga))}");
                Assert.Equal(ultimaColonna, vm.ColonneMatrice[^1]);
            }
            finally { finestra.Hide(); }
        });
    }

    [Fact]
    public void DimensioniMiste_NonRendonoIValoriFinaliIrraggiungibili()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato();
            var vm = new ReportViewModel(stato.State);
            var vista = new ReportView { DataContext = vm };
            var finestra = new Window { Width = 1280, Height = 672, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                AttendiMatrice(vm, finestra);
                var righe = vm.RigheMatrice.Select(r => r.Riga).ToList();
                var colonne = vm.ColonneMatrice.ToList();
                Assert.Equal(3, righe.Count);
                Assert.Equal(3, colonne.Count);

                // Due voci piccole entrano nella finestra. L'ultima, molto più grande, deve comunque
                // restare raggiungibile come singola voce nella coda dello scorrimento.
                vm.AggiornaDimensioniViewportMatrice(400, 160);
                vm.ImpostaAltezzaRigaMatrice(righe[0], ReportViewModel.AltezzaRigaMatriceMinima);
                vm.ImpostaAltezzaRigaMatrice(righe[1], ReportViewModel.AltezzaRigaMatriceMinima);
                vm.ImpostaAltezzaRigaMatrice(righe[2], ReportViewModel.AltezzaRigaMatriceMassima);
                vm.ImpostaLarghezzaColonnaMatrice(colonne[0], ReportViewModel.LarghezzaCellaMatriceMinima);
                vm.ImpostaLarghezzaColonnaMatrice(colonne[1], ReportViewModel.LarghezzaCellaMatriceMinima);
                vm.ImpostaLarghezzaColonnaMatrice(colonne[2], ReportViewModel.LarghezzaCellaMatriceMassima);

                vm.OffsetRigheMatrice = vm.MassimoOffsetRigheMatrice;
                vm.OffsetColonneMatrice = vm.MassimoOffsetColonneMatrice;

                Assert.Equal(righe[^1], vm.RigheMatrice[^1].Riga);
                Assert.Equal(colonne[^1], vm.ColonneMatrice[^1]);
            }
            finally { finestra.Hide(); }
        });
    }

    private static IEnumerable<string> Id(Visual radice) => radice.GetVisualDescendants().OfType<Control>()
        .Select(c => AutomationProperties.GetAutomationId(c)).OfType<string>();

    private static IEnumerable<Control> PerId(Visual radice, string id) => radice.GetVisualDescendants().OfType<Control>()
        .Where(c => AutomationProperties.GetAutomationId(c) == id);

    private static Border Cella(ReportView vista, string riga, string colonna) => vista.GetVisualDescendants().OfType<Border>()
        .Single(c => AutomationProperties.GetAutomationId(c) == $"ReportMatrice_Cella_{Sanitizza(riga)}_{Sanitizza(colonna)}");

    private static StackPanel Riga(ReportView vista, string chiave) => vista.GetVisualDescendants().OfType<StackPanel>()
        .Single(r => r.DataContext is TestLoom.App.Services.RigaMatriceReport dato && dato.Riga == chiave
            && AutomationProperties.GetAutomationId(r) == $"Report_Matrice_Riga_{chiave}");

    private static string Sanitizza(string valore) => new(valore.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    private static Grid MatriceViewport(ReportView vista) => vista.FindControl<Grid>("MatriceViewport")
        ?? throw new InvalidOperationException("Il viewport della matrice non e' presente.");

    private static void Drag(ReportView vista, Window finestra, Control superficie, Point puntoLocale, double deltaX, double deltaY)
    {
        var puntatore = new Pointer(101, PointerType.Mouse, true);
        var viewport = MatriceViewport(vista);
        var inizio = superficie.TranslatePoint(puntoLocale, finestra)
            ?? throw new InvalidOperationException("La superficie Report non e' collegata alla finestra.");
        var premute = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        superficie.RaiseEvent(new PointerPressedEventArgs(superficie, puntatore, finestra, inizio, 1, premute, KeyModifiers.None, 1));
        Assert.Same(viewport, puntatore.Captured);
        var trascinamento = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var primaDestinazione = new Point(inizio.X + deltaX / 2, inizio.Y + deltaY / 2);
        viewport.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, viewport, puntatore, finestra,
            primaDestinazione, 2, trascinamento, KeyModifiers.None));
        Assert.True(ReferenceEquals(viewport, puntatore.Captured), "il puntatore resta sul viewport dopo il primo movimento");
        var destinazione = new Point(inizio.X + deltaX, inizio.Y + deltaY);
        viewport.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, viewport, puntatore, finestra,
            destinazione, 3, trascinamento, KeyModifiers.None));
        Assert.True(ReferenceEquals(viewport, puntatore.Captured), "il puntatore resta sul viewport dopo il secondo movimento");
        var rilascio = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        viewport.RaiseEvent(new PointerReleasedEventArgs(viewport, puntatore, finestra, destinazione, 4,
            rilascio, KeyModifiers.None, MouseButton.Left));
        Assert.True(puntatore.Captured is null, puntatore.Captured is Control cattura
            ? $"cattura residua: {cattura.GetType().Name}, id={AutomationProperties.GetAutomationId(cattura)}, tag={cattura.Tag}, bounds={cattura.Bounds}"
            : "cattura residua non nulla");
    }

    private static void DragConPerditaCattura(ReportView vista, Window finestra, Control superficie, double deltaX, double deltaY)
    {
        var puntatore = new Pointer(102, PointerType.Mouse, true);
        var inizio = superficie.TranslatePoint(new Point(superficie.Bounds.Width / 2, superficie.Bounds.Height / 2), finestra)
            ?? throw new InvalidOperationException("Il bordo del viewport Report non e' collegato alla finestra.");
        var premute = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        superficie.RaiseEvent(new PointerPressedEventArgs(superficie, puntatore, finestra, inizio, 1, premute, KeyModifiers.None, 1));
        Assert.Same(MatriceViewport(vista), puntatore.Captured);
        var trascinamento = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var destinazione = new Point(inizio.X + deltaX, inizio.Y + deltaY);
        superficie.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, superficie, puntatore, finestra,
            destinazione, 2, trascinamento, KeyModifiers.None));
        puntatore.Capture(null);
        var vm = (ReportViewModel)vista.DataContext!;
        var altezzaPersistita = vm.AltezzaViewportMatrice;
        superficie.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, superficie, puntatore, finestra,
            new Point(destinazione.X, destinazione.Y + deltaY), 3, trascinamento, KeyModifiers.None));
        Assert.Equal(altezzaPersistita, vm.AltezzaViewportMatrice, 1);
        Assert.Null(puntatore.Captured);
    }

    private static void AttendiMatrice(ReportViewModel vm, Window finestra)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (vm.RigheMatrice.Count < 2 && DateTime.UtcNow < limite)
        {
            LayoutHeadlessSessione.Assesta(finestra);
            Thread.Sleep(50);
        }
        LayoutHeadlessSessione.Assesta(finestra);
        Assert.True(vm.RigheMatrice.Count >= 2);
    }

    private static StatoMatriceReport CreaStato()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportResize_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily A", GammaNome = "LCV" }, new() { Nome = "Daily B", GammaNome = "LCV" } });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = "ABS", VeicoliAssociati = ["Daily B"] },
            new() { Nome = "BCM", VeicoliAssociati = ["Daily A", "Daily B"] },
            new() { Nome = "EDC", VeicoliAssociati = ["Daily A"] },
        });
        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = "Lettura guasto", Categorie = ["Diagnostica"] },
            new() { Nome = "Lettura Parametri", Categorie = ["Funzionali"] },
            new() { Nome = "Lettura Sensori", Categorie = ["Sensori"] },
        });
        SalvaTest(state, "T-A", "BCM", "Daily A", Esito.Conforme, "Lettura guasto");
        SalvaTest(state, "T-B", "BCM", "Daily B", Esito.NonConforme, "Lettura guasto");
        SalvaTest(state, "T-C", "EDC", "Daily A", Esito.Conforme, "Lettura Parametri");
        SalvaTest(state, "T-D", "ABS", "Daily B", Esito.ConformeConDeviazioni, "Lettura Sensori");
        return new StatoMatriceReport(state, dir);
    }

    private static void SalvaTest(AppState state, string id, string componente, string veicolo, Esito esito, string funzionalita)
    {
        var tc = new TestCase { Id = id, Componente = new EcuSelection([componente]), Veicoli = [veicolo], Funzionalita = [funzionalita] };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = new DateTime(2026, 1, 1).AddDays(tc.Id.Length),
            Condizioni = [new Condizione { Funzionalita = funzionalita, Esito = esito, Componente = new EcuSelection([componente]) }],
        });
        state.TestCases.Save(tc, isNew: true);
    }

    private sealed class StatoMatriceReport(AppState state, string dir) : IDisposable
    {
        public AppState State { get; } = state;
        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
