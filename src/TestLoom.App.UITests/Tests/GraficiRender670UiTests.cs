using System.Drawing;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;

namespace TestLoom.App.UITests.Tests;

/// <summary>Grafici renderizzati sulla Home (workspace demo): ogni card deve mostrare un'immagine con contenuto,
/// non una zona vuota. Il controllo e' sul contenuto reale dell'immagine (colori distinti nella cattura dell'elemento
/// UIA), non solo sulla presenza dell'elemento. Attese limitate a <see cref="AttesaRender"/> per card; prima al lancio,
/// poi dopo aver portato la finestra a 1280x776 logici (schermo G2), dove la card ricalcola il grafico alla nuova
/// larghezza. In caso di fallimento il messaggio riporta le misure e le ultime righe diagnostiche di crash.log.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Grafici")]
[Trait("Area", "Grafici")]
public sealed class GraficiRender670UiTests : UiTestBase
{
    private const int LarghezzaG2 = 1280;
    private const int AltezzaG2 = 776;
    /// <summary>Un grafico reale ha antialiasing, testo e barre: decine di colori. Una zona vuota ne ha uno o due.</summary>
    private const int MinimoColoriDistinti = 16;
    private static readonly TimeSpan AttesaRender = TimeSpan.FromSeconds(20);

    public GraficiRender670UiTests() : base(TestWorkspace.CreateDemoCompleta(nameof(GraficiRender670UiTests))) { }

    [Fact]
    public void Home_GraficiRenderizzatiAllAvvioEDopoResizeG2()
    {
        try
        {
            var f = Prepara();
            var shell = new MainShellPage(f, App);
            shell.DismissNotificationsIfShown();
            shell.NavigateTo("home");
            VerificaGrafici(f, "Home", "avvio");
            FinestraNativa.RidimensionaLogici(f, LarghezzaG2, AltezzaG2);
            VerificaGrafici(f, "Home", "dopo resize G2");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Home_GraficiRenderizzatiAllAvvioEDopoResizeG2), ex); }
    }

    // ------------------------------------------------------------------ verifica

    private void VerificaGrafici(Window f, string pagina, string momento)
    {
        var id = new List<string>();
        var attesaCard = Wait.TryForCondition(() => (id = TrovaIdGrafici(f, pagina)).Count >= 1, AttesaRender);
        if (!attesaCard) throw new TimeoutException($"{pagina} ({momento}): nessun grafico presente entro {AttesaRender.TotalSeconds:0}s.");

        var scadenza = DateTime.UtcNow + AttesaRender;
        var misure = id.ToDictionary(a => a, _ => new ImmaginePixel.Misura());
        var pronti = new HashSet<string>();
        while (pronti.Count < id.Count && DateTime.UtcNow < scadenza)
        {
            foreach (var automationId in id.Where(a => !pronti.Contains(a)))
            {
                var img = TrovaImmagine(f, automationId);
                if (img == null) continue;
                PortaInVista(f, img);
                misure[automationId] = ImmaginePixel.Di(img);
                if (misure[automationId].Colori >= MinimoColoriDistinti) pronti.Add(automationId);
            }
            if (pronti.Count < id.Count) Wait.TryForCondition(() => false, TimeSpan.FromMilliseconds(400));
        }
        var problemi = id.Where(a => !pronti.Contains(a))
            .Select(a => $"{a}: {misure[a]} (minimo {MinimoColoriDistinti} colori)")
            .ToList();
        Assert.True(problemi.Count == 0,
            $"{pagina} ({momento}): grafici vuoti su {problemi.Count}/{id.Count} card:\n- " + string.Join("\n- ", problemi) +
            "\nUltime diagnostiche crash.log:\n" + UltimeDiagnostiche());
    }

    /// <summary>AutomationId completi "{Pagina}_Chart_{id}" delle immagini della pagina (una per card).</summary>
    private static List<string> TrovaIdGrafici(Window f, string pagina)
    {
        var prefisso = pagina + "_Chart_";
        return f.FindAllDescendants(cf => cf.ByControlType(ControlType.Image))
            .Select(e => SafeId(e))
            .Where(s => s.StartsWith(prefisso, StringComparison.Ordinal))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private static AutomationElement? TrovaImmagine(Window f, string automationId) =>
        ElementFinders.TryFindById(f, automationId);

    /// <summary>Porta l'immagine dentro l'area visibile della finestra (rotella), con attesa limitata.</summary>
    private static void PortaInVista(Window f, AutomationElement img)
    {
        for (var i = 0; i < 6; i++)
        {
            var r = img.BoundingRectangle;
            var w = f.BoundingRectangle;
            if (r.Top >= w.Top && r.Bottom <= w.Bottom) return;
            var prima = r.Top;
            Mouse.MoveTo(new Point(w.Left + w.Width / 2, w.Top + w.Height / 2));
            Mouse.Scroll(r.Bottom > w.Bottom ? -3 : 3);
            Wait.TryForCondition(() => img.BoundingRectangle.Top != prima, TimeSpan.FromSeconds(1));
        }
    }

    private static string UltimeDiagnostiche()
    {
        try
        {
            var percorso = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TestLoom", "crash.log");
            if (!File.Exists(percorso)) return "(crash.log assente)";
            var righe = File.ReadLines(percorso)
                .Where(r => r.Contains("grafic", StringComparison.OrdinalIgnoreCase) || r.Contains("render", StringComparison.OrdinalIgnoreCase))
                .TakeLast(8)
                .ToList();
            return righe.Count == 0 ? "(nessuna voce su grafici o rendering)" : string.Join("\n", righe);
        }
        catch (Exception ex) { return "(lettura crash.log non riuscita: " + ex.Message + ")"; }
    }

    // ------------------------------------------------------------------ finestra G2

    private Window Prepara()
    {
        FinestraNativa.AbilitaDpiAware();
        var f = App.WaitForMainWindow(TimeSpan.FromSeconds(30));
        Wait.ForCondition(() => MainShellPage.IsShown(f), "shell principale visibile", TimeSpan.FromSeconds(30));
        return f;
    }

    private static string SafeId(AutomationElement e)
    {
        try { return e.Properties.AutomationId.ValueOrDefault ?? ""; }
        catch { return ""; }
    }
}
