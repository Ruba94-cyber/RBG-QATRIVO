using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using System.Diagnostics;
using System.Text.Json;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class AuditMatriceResizeHeadlessTests
{
    private const int Requisiti = 1700;
    private const int Componente = 20;
    private const string ChiaveLayout = "Audit.Matrice.Layout";

    [Fact]
    public void Matrice1700x20_ResizeRiordinoPersistenzaEVirtualizzazione()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var stato = CreaStato(Requisiti, Componente);
            ViewStateService.Configura(stato.State);
            var vm = new AuditViewModel(stato.State) { SottoveicoloSelezionato = "Veicolo-A" };
            var vista = new AuditView { DataContext = vm };
            var finestra = new Window { Width = 1280, Height = 672, Content = vista };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(TestoVisibile(vista, "ST0001"), "la prima riga non e' realizzata");
                Assert.False(TestoVisibile(vista, "ST1700"), "la matrice ha realizzato tutte le righe");

                var area = vista.FindControl<Control>("MatriceHost")!;
                var controlliIniziali = area.GetVisualDescendants().Count();
                Assert.InRange(controlliIniziali, 1, 1000);

                Assert.DoesNotContain(Id(vista), id => id.StartsWith("Audit_Matrice_Resize", StringComparison.Ordinal)
                    || id.StartsWith("Audit_Matrice_Reorder", StringComparison.Ordinal));
                var resizeEcu = Intestazione(vista, "AUDIT:COLONNA:componente:componente01");
                Assert.True(resizeEcu.Focusable, "l'intestazione ridimensionabile non e' raggiungibile da tastiera");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(resizeEcu)), "l'intestazione non ha un nome accessibile");

                var larghezzaIniziale = Intestazione(vista, "AUDIT:COLONNA:componente:componente01").Width;
                var sw = Stopwatch.StartNew();
                Drag(vista, resizeEcu, new Point(1, resizeEcu.Bounds.Height / 2), 34, 0);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(larghezzaIniziale + 34, Intestazione(vista, "AUDIT:COLONNA:componente:componente01").Width, 1);
                Assert.Equal(Intestazione(vista, "AUDIT:COLONNA:componente:componente01").Width,
                    Cella(vista, "ST0001", "AUDIT:CELLA:componente:componente01").Width, 1);

                var resizeRequisito = Intestazione(vista, "AUDIT:COLONNA:REQUISITO");
                var larghezzaRequisitoIniziale = Intestazione(vista, "AUDIT:COLONNA:REQUISITO").Width;
                Drag(vista, resizeRequisito, new Point(resizeRequisito.Bounds.Width - 1, resizeRequisito.Bounds.Height / 2), 24, 0);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(larghezzaRequisitoIniziale + 24, Intestazione(vista, "AUDIT:COLONNA:REQUISITO").Width, 1);
                Assert.Equal(Intestazione(vista, "AUDIT:COLONNA:REQUISITO").Width,
                    Cella(vista, "ST0001", "AUDIT:COLONNA:REQUISITO").Width, 1);

                var ordineEcu01 = Intestazione(vista, "AUDIT:COLONNA:componente:componente01");
                Assert.True(ordineEcu01.Focusable, "l'intestazione non e' raggiungibile da tastiera");
                var ordineIniziale = HeaderTags(vista).ToList();
                var target = Intestazione(vista, "AUDIT:COLONNA:componente:componente03");
                var start = PosizioneCentrale(ordineEcu01, vista);
                var end = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), vista)!.Value;
                Drag(vista, ordineEcu01, new Point(ordineEcu01.Bounds.Width / 2, ordineEcu01.Bounds.Height / 2), end.X - start.X, end.Y - start.Y);
                LayoutHeadlessSessione.Assesta(finestra);
                var ordineDopoDrag = HeaderTags(vista).ToList();
                Assert.Equal("AUDIT:COLONNA:componente:componente02", ordineDopoDrag[1]);
                Assert.Equal("AUDIT:COLONNA:componente:componente03", ordineDopoDrag[2]);
                Assert.Equal("AUDIT:COLONNA:componente:componente01", ordineDopoDrag[3]);
                Assert.NotEqual(string.Join("|", ordineIniziale), string.Join("|", ordineDopoDrag));

                var layoutDopoColonne = ViewStateService.LeggiPerOperatore(ChiaveLayout);
                Assert.False(string.IsNullOrWhiteSpace(layoutDopoColonne), "il layout non e' stato salvato al rilascio del drag");
                using (var json = JsonDocument.Parse(layoutDopoColonne!))
                {
                    Assert.Equal(larghezzaIniziale + 34, json.RootElement.GetProperty("LarghezzeEcu").GetProperty("Componente01").GetDouble(), 1);
                    Assert.Equal(larghezzaRequisitoIniziale + 24, json.RootElement.GetProperty("LarghezzaRequisito").GetDouble(), 1);
                    Assert.Equal("Componente02", json.RootElement.GetProperty("OrdineEcu")[0].GetString());
                }

                var handleRiga = Riga(vista, "ST0001");
                Assert.True(handleRiga.Focusable, "la riga non e' raggiungibile da tastiera");
                var jsonPrimaAltezza = ViewStateService.LeggiPerOperatore(ChiaveLayout);
                Drag(vista, handleRiga, new Point(handleRiga.Bounds.Width / 2, handleRiga.Bounds.Height - 1), 0, 36, perdiCattura: true, verificaSalvataggioIntermedio: () =>
                    Assert.Equal(jsonPrimaAltezza, ViewStateService.LeggiPerOperatore(ChiaveLayout)));
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(62, Riga(vista, "ST0001").Height, 1);
                var jsonDopoAltezza = ViewStateService.LeggiPerOperatore(ChiaveLayout);
                Assert.NotEqual(jsonPrimaAltezza, jsonDopoAltezza);

                var altezzaPrimaTasto = Riga(vista, "ST0001").Height;
                handleRiga.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = handleRiga });
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Equal(altezzaPrimaTasto + 8, Riga(vista, "ST0001").Height, 1);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(handleRiga)), "la riga non ha un nome accessibile");

                var scroll = vista.FindControl<ScrollViewer>("MatriceScroll")!;
                scroll.Offset = new Vector(scroll.Offset.X, scroll.Extent.Height);
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(TestoVisibile(vista, "ST1700"), "lo scorrimento con righe variabili non raggiunge l'ultima riga");
                Assert.False(TestoVisibile(vista, "ST0001"), "la virtualizzazione conserva le righe fuori viewport");
                Assert.True(area.GetVisualDescendants().Count() < 1000, "il resize ha disattivato la virtualizzazione");

                sw.Stop();
                Console.WriteLine($"AUDIT_RESIZE_SCALE requisiti={vm.Righe.Count} componente={vm.EcuColonne.Count} controlli_iniziali={controlliIniziali} interazione_ms={sw.Elapsed.TotalMilliseconds:0}");

                finestra.Hide();
                var vistaRipristinata = new AuditView { DataContext = vm };
                var finestraRipristinata = new Window { Width = 1280, Height = 672, Content = vistaRipristinata };
                try
                {
                    LayoutHeadlessSessione.Mostra(finestraRipristinata);
                    LayoutHeadlessSessione.Assesta(finestraRipristinata);
                    Assert.Equal(larghezzaIniziale + 34, Intestazione(vistaRipristinata, "AUDIT:COLONNA:componente:componente01").Width, 1);
                    Assert.Equal(larghezzaRequisitoIniziale + 24, Intestazione(vistaRipristinata, "AUDIT:COLONNA:REQUISITO").Width, 1);
                    Assert.Equal(70, Riga(vistaRipristinata, "ST0001").Height, 1);
                    Assert.Equal("AUDIT:COLONNA:componente:componente02", HeaderTags(vistaRipristinata).ElementAt(1));
                }
                finally
                {
                    finestraRipristinata.Hide();
                }
            }
            finally
            {
                finestra.Hide();
            }
        });
    }

    private static IEnumerable<string> Id(Visual radice) => radice.GetVisualDescendants().OfType<Control>()
        .Select(c => AutomationProperties.GetAutomationId(c)).OfType<string>();

    private static Border Intestazione(AuditView vista, string tag) => vista.FindControl<StackPanel>("MatriceIntestazione")!
        .Children.OfType<Border>().Single(b => Equals(b.Tag, tag));

    private static Control Riga(AuditView vista, string requisitoId) => vista.FindControl<ItemsControl>("MatriceRighe")!
        .GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == "Audit_Matrice_Riga_" + requisitoId);

    private static Control Cella(AuditView vista, string requisitoId, string tagCella)
    {
        var riga = Riga(vista, requisitoId);
        return riga.GetVisualDescendants().OfType<Control>().Single(c => Equals(c.Tag, tagCella));
    }

    private static IEnumerable<string> HeaderTags(AuditView vista) => vista.FindControl<StackPanel>("MatriceIntestazione")!
        .Children.OfType<Border>().Select(b => (string)b.Tag!);

    private static void Drag(AuditView vista, Control superficie, Point posizioneInizialeLocale, double deltaX, double deltaY,
        bool perdiCattura = false, Action? verificaSalvataggioIntermedio = null)
    {
        var puntatore = new Pointer(1, PointerType.Mouse, true);
        var inizio = superficie.TranslatePoint(posizioneInizialeLocale, vista)
            ?? throw new InvalidOperationException("La superficie ridimensionabile non e' collegata alla matrice visuale.");
        var proprietàPremute = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        superficie.RaiseEvent(new PointerPressedEventArgs(superficie, puntatore, vista, inizio, 1, proprietàPremute, KeyModifiers.None, 1));
        Assert.Same(superficie, puntatore.Captured);
        var proprietàTrascinamento = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        var destinazione = new Point(inizio.X + deltaX, inizio.Y + deltaY);
        superficie.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, superficie, puntatore, vista, destinazione, 2, proprietàTrascinamento, KeyModifiers.None));
        verificaSalvataggioIntermedio?.Invoke();
        if (perdiCattura)
        {
            puntatore.Capture(null);
            var altezza = Riga(vista, "ST0001").Bounds.Height;
            var altroPunto = new Point(destinazione.X, destinazione.Y + deltaY);
            superficie.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, superficie, puntatore, vista,
                altroPunto, 3, proprietàTrascinamento, KeyModifiers.None));
            Assert.Equal(altezza, Riga(vista, "ST0001").Bounds.Height, 1);
        }
        else
        {
            var proprietàRilascio = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
            superficie.RaiseEvent(new PointerReleasedEventArgs(superficie, puntatore, vista, destinazione, 3, proprietàRilascio, KeyModifiers.None, MouseButton.Left));
        }
        Assert.Null(puntatore.Captured);
    }

    private static Point PosizioneCentrale(Control control, Visual coordinateRoot) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), coordinateRoot)
        ?? throw new InvalidOperationException("La superficie non e' collegata alla matrice visuale.");

    private static bool TestoVisibile(Visual vista, string testo) =>
        vista.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == testo);

    private sealed class StatoAudit : IDisposable
    {
        public StatoAudit(AppState state, string dir) { State = state; Dir = dir; }
        public AppState State { get; }
        private string Dir { get; }
        public void Dispose()
        {
            State.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static StatoAudit CreaStato(int requisiti, int ecuN)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_AuditMatriceResize_" + Guid.NewGuid().ToString("N"));
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var nomiEcu = Enumerable.Range(1, ecuN).Select(i => $"Componente{i:00}").ToList();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Veicolo-A" }, new() { Nome = "Veicolo-B" } });
        state.Master.Componente.Save(nomiEcu.Select(n => new Componente { Nome = n, VeicoliAssociati = { "Veicolo-A", "Veicolo-B" } }).ToList());
        state.Master.Requisiti.Save(Enumerable.Range(1, requisiti)
            .Select(i => new Requisito { Id = $"ST{i:0000}", Testo = $"Requisito di prova {i}" }).ToList());
        return new StatoAudit(state, dir);
    }
}
