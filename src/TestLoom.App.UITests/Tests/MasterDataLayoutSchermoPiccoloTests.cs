using System.Drawing;
using System.Runtime.InteropServices;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Regressione sul clipping delle pagine master su schermi bassi (Panasonic G2: 1920x1200 al 150% =
/// 1280x800 logici, area di lavoro 1280x776). Con expander Categorie/Gamme e Unione aperti, coppie sospette
/// e un catalogo lungo, la griglia principale (GridPrincipale) e la paginazione devono restare interamente
/// dentro la finestra e la griglia deve avere un'altezza minima utile. Lo stesso controllo alla dimensione
/// di default garantisce che il test fallisca solo per clipping reale.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Layout")]
public sealed class MasterDataLayoutSchermoPiccoloTests : UiTestBase
{
    private const int LogicoLarghezzaG2 = 1280;
    private const int LogicoAltezzaG2 = 776;
    private const int AltezzaMinimaGrigliaLogica = 100;
    private readonly ITestOutputHelper _out;

    public MasterDataLayoutSchermoPiccoloTests(ITestOutputHelper output) : base(CreaWorkspace()) { _out = output; }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(MasterDataLayoutSchermoPiccoloTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var categorie = state.Master.Categorie.Load();
        for (var i = 1; i <= 12; i++) categorie.Add(new Categoria { Nome = $"Categoria-{i:00}", ModificatoDa = "UITests" });
        state.Master.Categorie.Save(categorie);

        var funz = state.Master.Funzionalita.Load();
        for (var i = 1; i <= 30; i++) funz.Add(new Funzionalita { Nome = $"Funz-Layout-{i:00}", ModificatoDa = "UITests" });
        // Coppie sospette: stesso nome a meno della punteggiatura (le chiavi business sono case-insensitive).
        for (var i = 1; i <= 4; i++)
        {
            funz.Add(new Funzionalita { Nome = $"Cruise Control {i}", ModificatoDa = "UITests" });
            funz.Add(new Funzionalita { Nome = $"Cruise-Control {i}", ModificatoDa = "UITests" });
        }
        state.Master.Funzionalita.Save(funz);

        var gamme = state.Master.Gamma.Load();
        for (var i = 1; i <= 12; i++) gamme.Add(new Gamma { Nome = $"Gamma-{i:00}", ModificatoDa = "UITests" });
        state.Master.Gamma.Save(gamme);

        var veicoli = state.Master.Veicoli.Load();
        for (var i = 1; i <= 30; i++) veicoli.Add(new Veicolo { Nome = $"Veic-Layout-{i:00}", GammaNome = "Gamma-01", ModificatoDa = "UITests" });
        for (var i = 1; i <= 4; i++)
        {
            veicoli.Add(new Veicolo { Nome = $"Daily MY2{i}", GammaNome = "Gamma-01", ModificatoDa = "UITests" });
            veicoli.Add(new Veicolo { Nome = $"Daily-MY2{i}", GammaNome = "Gamma-01", ModificatoDa = "UITests" });
        }
        state.Master.Veicoli.Save(veicoli);
        return workspace;
    }

    [Fact]
    public void Funzionalita_NonTagliaGrigliaEPaginazione_SuSchermoG2()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var impostazioni = new MainShellPage(window).GoToImpostazioni();
            var pagina = impostazioni.VaiAFunzionalita();
            VerificaPagina(window, pagina, "Funz", "FunzCategorieExpander", "FunzUnioneExpander", "Funz-Layout-01", "FunzTextDividiIn", "Categoria-01", "Cruise Control");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Funzionalita_NonTagliaGrigliaEPaginazione_SuSchermoG2), ex); }
    }

    [Fact]
    public void Veicoli_NonTagliaGrigliaEPaginazione_SuSchermoG2()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var impostazioni = new MainShellPage(window).GoToImpostazioni();
            var pagina = impostazioni.VaiAVeicoli();
            VerificaPagina(window, pagina, "Veic", "VeicGammeExpander", "VeicoloUnioneExpander", "Daily-MY21", "VeicoloTextDividiIn", "Gamma-01", "Daily");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Veicoli_NonTagliaGrigliaEPaginazione_SuSchermoG2), ex); }
    }

    private void VerificaPagina(Window window, MasterDataEditorPage pagina, string prefisso, string expanderCategorie, string expanderUnione, string vocePerSelezione, string dividiId, string vocheAnnidata, string prefissoRigaVisibile)
    {
        var scala = ScalaCoordinateUia(window);
        _out.WriteLine($"Scala coordinate UIA: {scala:0.00}");

        // 1) Dimensione corrente (tipicamente grande): ripristina se massimizzata, poi misura.
        if (window.Patterns.Window.TryGetPattern(out var wp) && wp.WindowVisualState.Value != WindowVisualState.Normal)
            wp.SetWindowVisualState(WindowVisualState.Normal);
        EspandiTutto(window, pagina, expanderCategorie, expanderUnione, vocePerSelezione);
        VerificaDentroFinestra(window, pagina, prefisso, scala, "default");

        // 2) Area di lavoro del G2 sulla finestra nativa (rettangolo esterno, come in SchermoPiccolo670UiTests): il
        // BoundingRectangle UIA esclude i bordi DWM e non e' confrontabile con la dimensione richiesta.
        var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("handle nativo della finestra principale non disponibile");
        ShowWindow(hwnd, SwRestore);
        var scalaNativa = scala; // stessa scala della misura UIA: 1.0 se il processo non e' DPI-aware, altrimenti DPI della finestra / 96
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)Math.Round(LogicoLarghezzaG2 * scalaNativa), (int)Math.Round(LogicoAltezzaG2 * scalaNativa), SwpNoZOrder | SwpNoActivate);
        Wait.ForCondition(() =>
        {
            GetWindowRect(hwnd, out var rettangolo);
            return Math.Abs((rettangolo.Bottom - rettangolo.Top) - LogicoAltezzaG2 * scalaNativa) < 3 * scalaNativa;
        }, "la finestra raggiunge l'altezza dell'area di lavoro del G2");
        Wait.ForCondition(() => GrigliaDentro(window, pagina, scala), "la griglia principale rientra nella finestra G2");
        VerificaDentroFinestra(window, pagina, prefisso, scala, "G2");

        // 3) Zona alta: con SOLO Unione aperto, Rinomina/Dividi devono essere visibili senza scorrere; con SOLO
        //    Categorie/Gamme aperto, almeno una riga della griglia annidata deve essere visibile (finding 6.4.5).
        var unione = ElementFinders.WaitById(window, expanderUnione).Patterns.ExpandCollapse.Pattern;
        var annidato = ElementFinders.WaitById(window, expanderCategorie).Patterns.ExpandCollapse.Pattern;
        annidato.Collapse();
        unione.Expand();
        // Riseleziona la riga (la selezione fatta all'inizio puo' essere persa dai cambi di layout): senza
        // selezione il pannello Unione mostra solo il testo "Seleziona una riga".
        var riga = TrovaRigaGrid(pagina.Grid, prefissoRigaVisibile, window.BoundingRectangle.Bottom);
        _out.WriteLine($"[G2] riga '{prefissoRigaVisibile}*' trovata={riga != null} {(riga == null ? "" : Fmt(riga.BoundingRectangle))}");
        SelezionaRiga(pagina.Grid, prefissoRigaVisibile);
        Wait.ForCondition(() => VisibileSopraGriglia(window, pagina, ElementFinders.TryFindById(window, dividiId), scala), "il campo Dividi in e' visibile senza scorrere (Unione aperto)");
        var dividi = ElementFinders.TryFindById(window, dividiId)!.BoundingRectangle;
        _out.WriteLine($"[G2] {dividiId}={Fmt(dividi)} griglia={Fmt(pagina.Grid.BoundingRectangle)}");
        unione.Collapse();
        annidato.Expand();
        Wait.ForCondition(() => VisibileSopraGriglia(window, pagina, TrovaTesto(window, vocheAnnidata), scala), "almeno una riga annidata e' visibile (Categorie/Gamme aperto)");
        Wait.ForCondition(() => GrigliaDentro(window, pagina, scala), "la griglia principale resta nella finestra con l'annidato aperto");
    }

    private static bool VisibileSopraGriglia(Window window, MasterDataEditorPage pagina, AutomationElement? e, double scala)
    {
        if (e == null || e.Properties.IsOffscreen.ValueOrDefault) return false;
        var w = window.BoundingRectangle;
        var b = e.BoundingRectangle;
        var g = pagina.Grid.BoundingRectangle;
        return b.Height > 0 && b.Top >= w.Top && b.Bottom <= g.Top + 1 && b.Right <= w.Right + 1;
    }

    private static void EspandiTutto(Window window, MasterDataEditorPage pagina, string expanderCategorie, string expanderUnione, string voce)
    {
        var griglia = pagina.Grid;
        Wait.ForCondition(() => TrovaRigaGrid(griglia, voce, double.PositiveInfinity) != null, "la voce seminata compare in griglia");
        SelezionaRiga(griglia, voce);
        ElementFinders.WaitById(window, expanderCategorie).Patterns.ExpandCollapse.Pattern.Expand();
        ElementFinders.WaitById(window, expanderUnione).Patterns.ExpandCollapse.Pattern.Expand();
    }

    private static AutomationElement? TrovaRigaGrid(AutomationElement grid, string testo, double limiteBottom) =>
        grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .FirstOrDefault(riga => riga.BoundingRectangle.Height > 0 && riga.BoundingRectangle.Bottom <= limiteBottom &&
                riga.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Any(cella => cella.Name?.StartsWith(testo, StringComparison.OrdinalIgnoreCase) == true));

    private static void SelezionaRiga(AutomationElement grid, string testo)
    {
        // Avalonia DataGrid espone le righe come DataItem ma non pubblica SelectionItem. Selezioniamo in modo
        // deterministico via tastiera: indice della riga nell'ordine UIA, focus sulla griglia, Home e frecce.
        // Evita click coordinate dipendenti dalla risoluzione/posizione della finestra.
        var righe = grid.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem));
        var indice = Array.FindIndex(righe, riga => riga.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(cella => cella.Name?.StartsWith(testo, StringComparison.OrdinalIgnoreCase) == true));
        if (indice < 0) throw new InvalidOperationException($"La riga '{testo}' non è presente nella griglia UIA.");

        grid.Focus();
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.HOME);
        for (var i = 0; i < indice; i++) FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
    }

    private static bool GrigliaDentro(Window window, MasterDataEditorPage pagina, double scala)
    {
        var w = window.BoundingRectangle;
        var g = pagina.Grid.BoundingRectangle;
        return g.Bottom <= w.Bottom + 1 && g.Height >= AltezzaMinimaGrigliaLogica * scala;
    }

    private void VerificaDentroFinestra(Window window, MasterDataEditorPage pagina, string prefisso, double scala, string contesto)
    {
        var w = window.BoundingRectangle;
        var g = pagina.Grid.BoundingRectangle;
        _out.WriteLine($"[{contesto}] finestra={Fmt(w)} griglia={Fmt(g)}");
        Assert.True(g.Top >= w.Top && g.Left >= w.Left && g.Right <= w.Right + 1 && g.Bottom <= w.Bottom + 1,
            $"[{contesto}] GridPrincipale {Fmt(g)} esce dalla finestra {Fmt(w)}.");
        Assert.True(g.Height >= AltezzaMinimaGrigliaLogica * scala,
            $"[{contesto}] GridPrincipale troppo bassa: {g.Height:0} (minimo {AltezzaMinimaGrigliaLogica * scala:0}).");

        foreach (var id in new[] { $"{prefisso}_PaginaPrec_Button", $"{prefisso}_PaginaSucc_Button" })
        {
            var bottone = ElementFinders.TryFindById(window, id);
            if (bottone == null) continue;
            var b = bottone.BoundingRectangle;
            _out.WriteLine($"[{contesto}] {id}={Fmt(b)}");
            Assert.True(b.Top >= w.Top && b.Bottom <= w.Bottom + 1 && b.Right <= w.Right + 1,
                $"[{contesto}] {id} {Fmt(b)} esce dalla finestra {Fmt(w)}.");
        }
    }

    private static string Fmt(Rectangle r) => $"({r.Left},{r.Top} {r.Width}x{r.Height})";

    private static AutomationElement? TrovaTesto(AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(e => e.Name == testo);

    // Fattore tra pixel logici di Avalonia e coordinate UIA per QUESTO processo di test (dipende dalla DPI
    // awareness del processo): 1.0 se non DPI-aware, DPI di sistema/finestra altrimenti.
    private static double ScalaCoordinateUia(Window window)
    {
        try
        {
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
            return awareness switch
            {
                0 => 1.0,
                1 => GetDpiForSystem() / 96.0,
                _ => GetDpiForWindow(hwnd) / 96.0,
            };
        }
        catch { return 1.0; }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int comando);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr dopo, int x, int y, int cx, int cy, uint flag);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RettangoNativo rettangolo);

    private struct RettangoNativo { public int Left; public int Top; public int Right; public int Bottom; }

    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
}
