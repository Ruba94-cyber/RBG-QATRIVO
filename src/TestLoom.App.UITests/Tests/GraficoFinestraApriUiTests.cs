using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Finestra "Apri" di un grafico della Home (6.7.2: "Apri mostra il grafico nella finestra, non piu' su un'area bianca").
/// Workspace demo: il grafico ha dati reali. Il test verifica che l'immagine della finestra abbia un'area non nulla e
/// contenuto reale (colori distinti e pixel non bianchi, stesso controllo di <see cref="GraficiRender670UiTests"/>),
/// che si ridisegni quando la finestra viene ridimensionata (1200x700 logici) e che Esc la chiuda.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Grafici")]
[Trait("Area", "Grafici")]
public sealed class GraficoFinestraApriUiTests : UiTestBase
{
    /// <summary>Misura di ridimensionamento in pixel logici: entra nell'area di lavoro sia dello schermo di sviluppo
    /// 2534x1481 px a 200% (1267x740 logici, dove Windows limita la finestra grafico a circa 2490 px) sia del G2 (1280x776).</summary>
    private const int LarghezzaRidimensionata = 1200;
    private const int AltezzaRidimensionata = 700;
    /// <summary>Un grafico reale ha decine di colori (antialiasing, testo, barre); una zona vuota ne ha uno o due.</summary>
    private const int MinimoColoriDistinti = 16;
    /// <summary>Pixel non bianchi nel campionamento: un grafico disegnato ne ha migliaia, una zona bianca nessuno.</summary>
    private const int MinimoPixelNonBianchi = 50;
    /// <summary>Crescita minima della larghezza dell'immagine dopo il resize a 1280 logici (la finestra parte da 980).</summary>
    private const int CrescitaMinimaPixel = 100;
    private static readonly TimeSpan AttesaRender = TimeSpan.FromSeconds(20);

    public GraficoFinestraApriUiTests() : base(TestWorkspace.CreateDemoCompleta(nameof(GraficoFinestraApriUiTests))) { }

    [Fact]
    public void Apri_DallaCardHome_MostraUnGraficoConContenutoSiRidisegnaEEscChiude()
    {
        try
        {
            FinestraNativa.AbilitaDpiAware();
            var window = App.WaitForMainWindow(TimeSpan.FromSeconds(30));
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale visibile", TimeSpan.FromSeconds(30));
            var shell = new MainShellPage(window, App);
            shell.DismissNotificationsIfShown();
            var home = shell.GoToHome();
            var idGrafico = ScegliGraficoVisibile(home);

            home.ApriGraficoButton(idGrafico).SafeInvoke();
            var finestra = AttendiFinestraGrafico(window);
            var prima = AttendiContenuto(finestra, "all'apertura", larghezzaMinima: 0);

            try { FinestraNativa.RidimensionaLogici(finestra, LarghezzaRidimensionata, AltezzaRidimensionata); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"{ex.Message} Rettangolo nativo dopo il tentativo: {FinestraNativa.RettangoloNativo(finestra)}.", ex);
            }
            var dopo = AttendiContenuto(finestra, "dopo il ridimensionamento", larghezzaMinima: prima.Rettangolo.Width + CrescitaMinimaPixel);
            Assert.True(dopo.Rettangolo.Width >= prima.Rettangolo.Width + CrescitaMinimaPixel,
                $"Il grafico non si e' ridisegnato alla nuova dimensione: prima {prima}, dopo {dopo}.");

            finestra.Focus();
            FlaUI.Core.Input.Keyboard.Type(VirtualKeyShort.ESCAPE);
            // Chiusura verificata per contenuto tra le finestre di primo livello, come per gli altri dialoghi.
            Wait.ForCondition(() => App.IsWindowClosedByContent("GraficoFinestra_Immagine"),
                "la finestra grafico si chiude con Esc", TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Apri_DallaCardHome_MostraUnGraficoConContenutoSiRidisegnaEEscChiude), ex); }
    }

    /// <summary>La finestra grafico è posseduta dalla principale: in UI Automation compare come discendente della finestra
    /// principale, non come finestra di primo livello. Cercarla con WaitForWindowByContent restituirebbe la principale
    /// (che contiene l'immagine nel proprio sottoalbero), e il ridimensionamento colpirebbe la finestra sbagliata. Si
    /// cerca quindi la finestra per il suo AutomationId ("GraficoFinestra_Window").</summary>
    private static Window AttendiFinestraGrafico(Window principale) =>
        Wait.ForElement(
            () => principale.FindFirstDescendant(cf => cf.ByAutomationId("GraficoFinestra_Window").And(cf.ByControlType(ControlType.Window))),
            "finestra grafico 'Apri' (GraficoFinestra_Window)", TimeSpan.FromSeconds(20)).AsWindow();

    /// <summary>Primo grafico della Home già visibile; altrimenti seleziona "donut". Ogni id ha la sua card con "Apri".</summary>
    private static string ScegliGraficoVisibile(HomePage home)
    {
        foreach (var id in new[] { "copertura", "andamento", "donut" })
            if (home.GraficoVisibile(id)) return id;
        home.SelezionaGrafico("donut");
        return "donut";
    }

    /// <summary>Attende che l'immagine della finestra grafico sia più larga di <paramref name="larghezzaMinima"/> e
    /// contenga un grafico reale (non una zona vuota). In caso di timeout riporta l'ultima misura e lo stato di
    /// "Grafico non disponibile".</summary>
    private static ImmaginePixel.Misura AttendiContenuto(Window finestra, string momento, int larghezzaMinima)
    {
        var ultima = default(ImmaginePixel.Misura);
        try
        {
            Wait.ForCondition(() =>
            {
                var img = ElementFinders.TryFindById(finestra, "GraficoFinestra_Immagine");
                if (img == null || img.BoundingRectangle.Width <= larghezzaMinima || img.BoundingRectangle.Height <= 0) return false;
                ultima = ImmaginePixel.Di(img);
                return ultima.Colori >= MinimoColoriDistinti && ultima.NonBianchi >= MinimoPixelNonBianchi
                       && ultima.Rettangolo.Width > larghezzaMinima;
            }, $"grafico con contenuto {momento}", AttesaRender);
        }
        catch (TimeoutException ex)
        {
            var nonDisponibile = ElementFinders.ExistsById(finestra, "GraficoFinestra_NonDisponibile");
            throw new TimeoutException(
                $"{ex.Message} Ultima misura {momento}: {ultima} (minimo {MinimoColoriDistinti} colori, " +
                $"{MinimoPixelNonBianchi} pixel non bianchi, larghezza > {larghezzaMinima}). " +
                (nonDisponibile ? "La finestra mostra 'Grafico non disponibile'." : "Il messaggio di grafico non disponibile non e' visibile.") +
                $" Finestra UIA {finestra.BoundingRectangle}, rettangolo nativo {FinestraNativa.RettangoloNativo(finestra)}.", ex);
        }
        return ultima;
    }
}
