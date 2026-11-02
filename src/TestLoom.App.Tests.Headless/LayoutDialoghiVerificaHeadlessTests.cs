using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using static TestLoom.App.Tests.LayoutHeadlessMisure;

namespace TestLoom.App.Tests;

/// <summary>Tutte le finestre di dialogo (Category=LayoutHeadless) aperte in modo Compatto con l'area di lavoro di 1280x672:
/// 1232x624 DIP dopo il margine di <see cref="AdattamentoDialogo.Margine"/>. Il contenuto che non sta nell'area deve stare in uno
/// ScrollViewer; i pulsanti di conferma (Primary) non devono stare dentro lo scroll e devono restare nella finestra; lo sfondo deve
/// essere opaco. L'headless non espone la dimensione dello schermo: l'area viene applicata dopo l'apertura, come farebbe
/// AdattamentoDialogo su quello schermo. Dove il contenuto dipende dai dati (visibilita' dei pulsanti dai binding) la finestra
/// riceve il suo ViewModel; le finestre che ospitano un editor a pagina intera hanno un test dedicato.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public class LayoutDialoghiVerificaHeadlessTests
{
    /// <summary>Finestre che ospitano l'editor Test a pagina intera: barra e scroll sono dell'editor, quindi il loro controllo sta nel
    /// test dedicato con il TestCaseEditorViewModel reale, non in quello dei dialoghi.</summary>
    private static readonly HashSet<string> FinestreEditor = new() { "TestCaseEditWindow", "TestCaseViewerWindow" };

    [Fact]
    public void TutteLeFinestreDiDialogo_1280x672_ContenutoScorrePiediVisibiliEntroArea() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            using var fixture = CreaFixtureEditor();
            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1280, 672);
            var larghezzaArea = AdattamentoDialogo.LarghezzaMassima(1280, 1.0);
            var altezzaArea = AdattamentoDialogo.AltezzaMassima(672, 1.0);
            var violazioni = new List<string>();
            var tipi = FinestreDiDialogo().ToList();
            Assert.NotEmpty(tipi);
            foreach (var tipo in tipi)
            {
                Window? finestra = null;
                try
                {
                    finestra = (Window)Activator.CreateInstance(tipo)!;
                    if (DataContextPer(tipo, fixture.State) is { } dataContext) finestra.DataContext = dataContext;
                    finestra.MaxWidth = larghezzaArea;
                    finestra.MaxHeight = altezzaArea;
                    LayoutHeadlessSessione.Mostra(finestra);
                    // Opened ha riportato i tetti all'area dello schermo headless: qui li riportiamo a quella di 1280x672.
                    finestra.MaxWidth = larghezzaArea;
                    finestra.MaxHeight = altezzaArea;
                    if (finestra.Height > altezzaArea) finestra.Height = altezzaArea;
                    if (finestra.Width > larghezzaArea) finestra.Width = larghezzaArea;
                    LayoutHeadlessSessione.Assesta(finestra);
                    LayoutHeadlessSessione.Assesta(finestra);
                    ControllaFinestra(finestra, tipo.Name, larghezzaArea, altezzaArea, violazioni);
                }
                catch (Exception ex)
                {
                    violazioni.Add($"{tipo.Name}: eccezione {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    finestra?.Hide();
                }
            }
            Assert.True(violazioni.Count == 0,
                $"{violazioni.Count} problemi su {tipi.Count} finestre a 1280x672:\n" + string.Join("\n", violazioni));
        });

    /// <summary>L'editor Test a pagina intera in 1280x672: barra azioni dentro la finestra con il menu "Vai a" e scroll del contenuto.
    /// Il modo massimizzato (grande in compatto) viene riportato a 1280x672 dopo l'apertura, perche' l'headless non ha lo schermo.</summary>
    [Fact]
    public void FinestraEditorTest1280x672_BarraDentroFinestraEContenutoScorre() =>
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            LayoutAdattivo.ReimpostaStato();
            LayoutAdattivo.Aggiorna(1280, 672);
            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { });
            var finestra = new TestCaseEditWindow { DataContext = vm };
            try
            {
                LayoutHeadlessSessione.Mostra(finestra);
                finestra.WindowState = WindowState.Normal;
                finestra.Width = 1280;
                finestra.Height = 672;
                LayoutHeadlessSessione.Assesta(finestra);
                LayoutHeadlessSessione.Assesta(finestra);

                AsserisciDentroFinestra(Trova<Button>(finestra, "Editor_Cancel_Button"), finestra, "Torna alla lista");
                AsserisciDentroFinestra(Trova<Button>(finestra, "Editor_VaiA_Button"), finestra, "Vai a");
                // Lo scroll verticale dell'editor (EditorScroll): la barra ha un suo ScrollViewer orizzontale, che qui non conta.
                var scroll = finestra.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "EditorScroll");
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                    $"il contenuto dell'editor non scorre: {scroll.Extent.Height:0} <= {scroll.Viewport.Height:0}");
            }
            finally
            {
                finestra.Hide();
            }
        });

    /// <summary>ViewModel con cui la finestra si apre davvero: la visibilita' dei pulsanti dipende dai binding. Dove non serve
    /// nessun dato particolare la finestra si apre senza ViewModel.</summary>
    private static object? DataContextPer(Type tipo, AppState state)
    {
        if (tipo == typeof(AnomaliaDettaglioWindow))
        {
            var nc = new NonConformita { Id = "NC-2026-0001", Descrizione = "Errore di prova", Owner = "Mario" };
            return new AnomaliaDettaglioViewModel(state, nc, persistita: false);
        }
        return null;
    }

    private static void ControllaFinestra(Window finestra, string nome, double larghezzaArea, double altezzaArea, List<string> violazioni)
    {
        if (finestra.Bounds.Height > altezzaArea + 0.5 || finestra.Bounds.Width > larghezzaArea + 0.5)
            violazioni.Add($"{nome}: finestra {finestra.Bounds.Width:0}x{finestra.Bounds.Height:0} oltre l'area {larghezzaArea:0}x{altezzaArea:0}");

        if (finestra.Background is not ISolidColorBrush sfondo || sfondo.Color.A != 255)
            violazioni.Add($"{nome}: sfondo non opaco");

        var client = finestra.ClientSize;
        var haScroll = finestra.GetVisualDescendants().OfType<ScrollViewer>().Any();
        if (!haScroll && finestra.Content is Layoutable contenuto && contenuto.DesiredSize.Height > client.Height + 1)
            violazioni.Add($"{nome}: contenuto alto {contenuto.DesiredSize.Height:0} px senza ScrollViewer, area {client.Height:0} px");

        foreach (var pulsante in finestra.GetVisualDescendants().OfType<Button>()
                     .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("Primary")))
        {
            var id = AutomationProperties.GetAutomationId(pulsante);
            var etichetta = string.IsNullOrEmpty(id) ? pulsante.Content?.ToString() ?? "?" : id;
            if (pulsante.FindAncestorOfType<ScrollViewer>() != null)
            {
                violazioni.Add($"{nome}: pulsante di conferma '{etichetta}' dentro uno ScrollViewer");
                continue;
            }
            var r = Rettangolo(pulsante, finestra);
            if (r.Bottom > client.Height + 0.5 || r.Right > client.Width + 0.5)
                violazioni.Add($"{nome}: pulsante di conferma '{etichetta}' fuori dall'area ({r.Right:0}x{r.Bottom:0}; client {client.Width:0}x{client.Height:0}; finestra {finestra.Bounds.Width:0}x{finestra.Bounds.Height:0})");
        }
    }

    /// <summary>Finestre di dialogo della cartella Views con costruttore senza parametri (la finestra principale e gli editor a pagina
    /// intera esclusi).</summary>
    private static IEnumerable<Type> FinestreDiDialogo() =>
        typeof(ConfirmDialog).Assembly.GetTypes()
            .Where(t => t.Namespace == "TestLoom.App.Views" && typeof(Window).IsAssignableFrom(t) && !t.IsAbstract
                        && t != typeof(MainWindow) && !FinestreEditor.Contains(t.Name) && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);
}
