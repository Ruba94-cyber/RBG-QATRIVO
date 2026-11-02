using System.Runtime.InteropServices;
using System.Text.Json;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;

namespace TestLoom.App.UITests.Helpers;

/// <summary>
/// Supporto condiviso dalle classi ShellHomeSettingsCoverage*UiTests: sola lettura di proprieta' UIA, attese con
/// timeout esplicito, azioni via AutomationId. Il ridimensionamento usa ShowWindow/SetWindowPos sull'handle della
/// finestra (TransformPattern.Resize non cambia la dimensione di questa finestra), mai click di coordinate.
/// </summary>
internal static class ShellHomeSettingsCoverageSupport
{
    public const string NotificaMenuId = "Main_Notifiche_MenuItem";

    /// <summary>Elemento presente, non fuori schermo e con un'area reale (un controllo nascosto da IsVisible resta
    /// spesso nell'albero UIA con dimensioni nulle).</summary>
    public static bool VisibileInUia(AutomationElement? e)
    {
        if (e == null) return false;
        try
        {
            if (e.Properties.IsOffscreen.ValueOrDefault) return false;
            var r = e.BoundingRectangle;
            return r.Width > 1 && r.Height > 1;
        }
        catch { return false; }
    }

    public static AutomationElement? VisibilePerId(AutomationElement scope, string automationId) =>
        VisibileInUia(ElementFinders.TryFindById(scope, automationId)) ? ElementFinders.TryFindById(scope, automationId) : null;

    public static bool IdVisibile(AutomationElement scope, string automationId) => VisibilePerId(scope, automationId) != null;

    /// <summary>Testo (TextBlock/Label) con nome esatto e visibile.</summary>
    public static AutomationElement? TestoVisibile(AutomationElement scope, string testo) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(e => e.Name == testo && VisibileInUia(e));

    public static bool TestoContieneVisibile(AutomationElement scope, string frammento) =>
        scope.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(e => e.Name != null && e.Name.Contains(frammento, StringComparison.Ordinal) && VisibileInUia(e));

    /// <summary>Vero se l'elemento sta per intero dentro i bordi della finestra (tolleranza di 1 px).</summary>
    public static bool DentroFinestra(Window finestra, AutomationElement? e)
    {
        if (!VisibileInUia(e)) return false;
        var w = finestra.BoundingRectangle;
        var b = e!.BoundingRectangle;
        return b.Left >= w.Left - 1 && b.Top >= w.Top - 1 && b.Right <= w.Right + 1 && b.Bottom <= w.Bottom + 1;
    }

    public static void PortaInPrimoPiano(Window finestra)
    {
        var hwnd = finestra.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        Wait.TryForCondition(() => GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(3));
        try { finestra.Focus(); } catch { }
    }

    /// <summary>Combinazione di tasti (es. Ctrl+K) sulla finestra in primo piano.</summary>
    public static void PremiCombinazione(Window finestra, params VirtualKeyShort[] tasti)
    {
        PortaInPrimoPiano(finestra);
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(tasti);
    }

    /// <summary>Come <see cref="PremiCombinazione"/> ma senza riportare il fuoco sulla finestra: il controllo che ha il
    /// fuoco della pagina riceve il tasto per primo, come farebbe un utente reale.</summary>
    public static void PremiCombinazioneSenzaRifocalizzare(Window finestra, params VirtualKeyShort[] tasti)
    {
        var hwnd = finestra.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        Wait.TryForCondition(() => GetForegroundWindow() == hwnd, TimeSpan.FromSeconds(3));
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(tasti);
    }

    public static void ApriRicercaGlobaleConCtrlK(Window finestra)
    {
        PremiCombinazione(finestra, VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        Wait.ForCondition(() => VisibileInUia(ElementFinders.TryFindById(finestra, "Main_RicercaGlobale_TextBox")),
            "casella della ricerca globale visibile dopo Ctrl+K", TimeSpan.FromSeconds(10));
    }

    /// <summary>Risultato della ricerca globale che contiene il testo indicato (uno dei controlli Main_RicercaGlobale_Risultato_Button).</summary>
    public static AutomationElement? RisultatoRicerca(Window finestra, string testoContenuto) =>
        finestra.FindAllDescendants(cf => cf.ByAutomationId("Main_RicercaGlobale_Risultato_Button"))
            .FirstOrDefault(b => VisibileInUia(b) && b.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                .Any(t => t.Name != null && t.Name.Contains(testoContenuto, StringComparison.Ordinal)));

    /// <summary>Riga di una notifica (identificata dal titolo) e il suo pulsante. Risale al massimo di tre livelli dal
    /// titolo: oltre, il contenitore comprende altre righe e il pulsante potrebbe appartenere a una notifica diversa.</summary>
    public static AutomationElement? BottoneRigaNotifica(Window finestra, string titolo, string automationId)
    {
        var titoloEl = finestra.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).FirstOrDefault(e => e.Name == titolo);
        if (titoloEl == null) return null;
        var nodo = titoloEl.Parent;
        for (var livello = 0; livello < 3 && nodo != null; livello++, nodo = nodo.Parent)
        {
            var bottone = nodo.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (bottone != null) return bottone;
        }
        return null;
    }

    /// <summary>Apre il centro notifiche: al login puo' essere gia' aperto (notifiche non lette all'avvio).</summary>
    public static void ApriCentroNotifiche(Window finestra)
    {
        if (VisibileInUia(ElementFinders.TryFindById(finestra, "Main_NotificationsClose_Button"))) return;
        ElementFinders.WaitButtonById(finestra, "Main_AccountMenu_Button").SafeInvoke();
        ElementFinders.WaitById(finestra, NotificaMenuId).SafeInvoke();
        Wait.ForCondition(() => VisibileInUia(ElementFinders.TryFindById(finestra, "Main_NotificationsClose_Button")),
            "centro notifiche aperto", TimeSpan.FromSeconds(10));
    }

    /// <summary>Se compare una finestra di conferma con il pulsante "Si" (Confirm_Yes_Button) entro il timeout, la conferma.</summary>
    public static bool ConfermaSeCompare(TestLoomApplication app, TimeSpan timeout)
    {
        if (!Wait.TryForCondition(() => !app.IsWindowClosedByContent("Confirm_Yes_Button"), timeout)) return false;
        new ConfirmDialogPage(app.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
        Wait.ForCondition(() => app.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura conferma modale");
        return true;
    }

    /// <summary>Proprieta' di un oggetto JSON senza distinguere maiuscole/minuscole (il serializzatore usa PascalCase).</summary>
    public static JsonElement? Proprieta(JsonElement oggetto, string nome)
    {
        foreach (var p in oggetto.EnumerateObject())
            if (string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    public static JsonDocument LeggiJson(string percorso) => JsonDocument.Parse(File.ReadAllText(percorso));

    /// <summary>Semina il layout della Home del team (_ditm/ui/home-team-layout.json, revisione 1, "kpi" nascosto)
    /// usando lo stesso serializzatore del servizio. Va chiamata PRIMA dell'avvio di TestLoom.</summary>
    public static string SeminaLayoutTeam(TestWorkspace ws, string pubblicatoDa)
    {
        var percorso = HomeTeamLayoutService.PercorsoFile(new DataPaths(ws.WorkspaceDir));
        Directory.CreateDirectory(Path.GetDirectoryName(percorso)!);
        JsonIo.WriteAtomic(percorso, new HomeTeamLayoutDocumento
        {
            Schema = HomeTeamLayoutService.SchemaCorrente,
            Revisione = 1,
            AggiornatoDa = pubblicatoDa,
            AggiornatoUtc = DateTime.UtcNow,
            Sezioni = new List<HomeTeamLayoutSezione>
            {
                new() { Id = "riepilogo", Visibile = true },
                new() { Id = "avvisi", Visibile = true },
                new() { Id = "kpi", Visibile = false },
                new() { Id = "grafici", Visibile = true },
                new() { Id = "liste", Visibile = true },
                new() { Id = "recenti", Visibile = true },
            },
        });
        return percorso;
    }

    /// <summary>
    /// Imposta la dimensione ESTERNA della finestra (cornice inclusa) in unita' logiche di Avalonia, nella stessa scala
    /// delle coordinate UIA del processo di test. La finestra di TestLoom parte massimizzata: si ripristina lo stato normale
    /// con ShowWindow e poi si applica SetWindowPos. TransformPattern.Resize non e' usato: su questa finestra non cambia
    /// la dimensione (verificato: la misura restava quella massimizzata). Il minimo di contenuto (MinWidth/MinHeight) puo'
    /// imporre una misura maggiore: la misura reale viene restituita e asserita dal chiamante, non assunta.
    /// </summary>
    public static (int Larghezza, int Altezza) PortaFinestra(Window finestra, int larghezzaLogica, int altezzaLogica)
    {
        var hwnd = finestra.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("handle nativo della finestra non disponibile");
        var scala = ScalaCoordinateUia(finestra);
        var massimizzata = finestra.BoundingRectangle;

        ShowWindow(hwnd, SwRestore);
        if (!Wait.TryForCondition(() => !IsZoomed(hwnd), TimeSpan.FromSeconds(10)))
            throw new TimeoutException("La finestra non esce dallo stato massimizzato.");

        var larghezzaUia = (int)Math.Round(larghezzaLogica * scala);
        var altezzaUia = (int)Math.Round(altezzaLogica * scala);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, larghezzaUia, altezzaUia, SwpNoZOrder | SwpNoActivate);

        // Misura stabile per alcuni campioni consecutivi e diversa da quella massimizzata.
        var ultima = (Larghezza: -1, Altezza: -1);
        var stabile = 0;
        Wait.TryForCondition(() =>
        {
            var r = finestra.BoundingRectangle;
            var misura = (Larghezza: r.Width, Altezza: r.Height);
            stabile = misura == ultima ? stabile + 1 : 0;
            ultima = misura;
            return stabile >= 4 && (r.Width != massimizzata.Width || r.Height != massimizzata.Height);
        }, TimeSpan.FromSeconds(15));

        var attuale = finestra.BoundingRectangle;
        return ((int)Math.Round(attuale.Width / scala), (int)Math.Round(attuale.Height / scala));
    }

    /// <summary>Geometria per la diagnostica dei test di layout: area di lavoro del monitor principale (senza barra delle
    /// applicazioni) in pixel UIA, fattore logico->UIA di questo processo e rettangolo della finestra.</summary>
    public static string DescriviGeometria(Window finestra)
    {
        var scala = ScalaCoordinateUia(finestra);
        var area = AreaDiLavoroUia();
        var r = finestra.BoundingRectangle;
        return $"area di lavoro {area.Right - area.Left}x{area.Bottom - area.Top} px UIA (origine {area.Left},{area.Top}), " +
               $"scala logica->UIA {scala:0.###}, finestra {r.Left:0},{r.Top:0} {r.Width:0}x{r.Height:0} px UIA";
    }

    /// <summary>Area di lavoro del monitor principale in unita' logiche di Avalonia (stessa scala di <see cref="PortaFinestra"/>).</summary>
    public static (double Larghezza, double Altezza) AreaDiLavoroLogica(Window finestra)
    {
        var scala = ScalaCoordinateUia(finestra);
        var area = AreaDiLavoroUia();
        return ((area.Right - area.Left) / scala, (area.Bottom - area.Top) / scala);
    }

    /// <summary>Dimensione ESTERNA della finestra, cornice inclusa (quella impostata con SetWindowPos), in unita' logiche.</summary>
    public static (int Larghezza, int Altezza) DimensioniEsterne(Window finestra)
    {
        var hwnd = finestra.FrameworkAutomationElement.NativeWindowHandle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("handle nativo della finestra non disponibile");
        if (!GetWindowRect(hwnd, out var rettangolo)) throw new InvalidOperationException("GetWindowRect non riuscita");
        var scala = ScalaCoordinateUia(finestra);
        return ((int)Math.Round((rettangolo.Right - rettangolo.Left) / scala), (int)Math.Round((rettangolo.Bottom - rettangolo.Top) / scala));
    }

    /// <summary>Area di lavoro del monitor principale (esclusa la barra delle applicazioni), nelle coordinate del processo.</summary>
    private static RettangoNativo AreaDiLavoroUia()
    {
        if (!SystemParametersInfo(SpiGetWorkArea, 0, out var area, 0))
            throw new InvalidOperationException("SystemParametersInfo(SPI_GETWORKAREA) non riuscita");
        return area;
    }

    // Fattore tra pixel logici di Avalonia e coordinate UIA per questo processo di test (DPI awareness).
    private static double ScalaCoordinateUia(Window finestra)
    {
        try
        {
            var hwnd = finestra.Properties.NativeWindowHandle.ValueOrDefault;
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
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out RettangoNativo pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct RettangoNativo
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RettangoNativo rettangolo);

    private const uint SpiGetWorkArea = 0x0030;
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
}
