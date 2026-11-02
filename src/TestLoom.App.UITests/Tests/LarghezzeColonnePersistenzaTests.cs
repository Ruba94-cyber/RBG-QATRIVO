using System.Drawing;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;
using Xunit.Abstractions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Le larghezze colonna scelte dall'utente (trascinando il bordo destro dell'intestazione) devono
/// sopravvivere alla chiusura e alla riapertura dell'app (stesso workspace/config), anche con colonne Star.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Layout")]
public sealed class LarghezzeColonnePersistenzaTests : UiTestBase
{
    private const int DeltaStar = 80;
    private readonly ITestOutputHelper _out;

    public LarghezzeColonnePersistenzaTests(ITestOutputHelper output)
        : base(TestWorkspace.CreateLoggedIn(nameof(LarghezzeColonnePersistenzaTests))) { _out = output; }

    private static List<AutomationElement> Intestazioni(MasterDataEditorPage pagina) =>
        pagina.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .Where(h => h.BoundingRectangle.Width > 0).OrderBy(h => h.BoundingRectangle.Left).ToList();

    private static MasterDataEditorPage ApriEcu(Window window) => new MainShellPage(window).GoToImpostazioni().VaiAEcu();

    [Fact]
    public void LarghezzaColonnaRidimensionata_SopravvivaAlRiavvio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriEcu(window);
            Wait.ForCondition(() => Intestazioni(pagina).Count >= 4, "intestazioni Componenti presenti");
            var hs = Intestazioni(pagina);
            // Colonna a larghezza fissa (2a): ridimensionata di ~80 px trascinando il bordo destro.
            var h = hs[1];
            var prima = h.BoundingRectangle;
            _out.WriteLine($"colonne={string.Join(" ", hs.Select(x => x.BoundingRectangle.Width))}; target={h.Name} larghezza={prima.Width}");
            var y = prima.Top + prima.Height / 2;
            Mouse.Drag(new Point(prima.Right - 2, y), new Point(prima.Right - 2 + 80, y));
            Wait.ForCondition(() => Intestazioni(pagina)[1].BoundingRectangle.Width >= prima.Width + 50, "la colonna si allarga trascinando");
            var dopo = Intestazioni(pagina)[1].BoundingRectangle.Width;
            _out.WriteLine($"dopo drag larghezza={dopo}");

            App.Dispose();
            using var app2 = TestLoomApplication.Launch(Workspace);
            var window2 = app2.WaitForMainWindow();
            var pagina2 = ApriEcu(window2);
            Wait.ForCondition(() => Intestazioni(pagina2).Count >= 4, "intestazioni Componenti presenti dopo riavvio");
            Wait.ForCondition(() => Math.Abs(Intestazioni(pagina2)[1].BoundingRectangle.Width - dopo) <= 5, "larghezza ripristinata (+-5)");
            _out.WriteLine($"dopo riavvio larghezza={Intestazioni(pagina2)[1].BoundingRectangle.Width}");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(LarghezzaColonnaRidimensionata_SopravvivaAlRiavvio), ex); }
    }

    /// <summary>Colonna Star (Nome, MinWidth 160) allargata di 80 px (somma colonne > larghezza griglia): nessuna colonna deve
    /// scendere sotto la propria larghezza minima (la griglia scorre in orizzontale: l'ultima intestazione esce
    /// dal bordo destro) e la larghezza scelta deve sopravvivere al riavvio.</summary>
    [Fact]
    public void ColonnaStarAllargata_ScorrimentoOrizzontaleELarghezzaPersistente()
    {
        try
        {
            var window = App.WaitForMainWindow();
            if (window.Patterns.Window.TryGetPattern(out var wp) && wp.WindowVisualState.Value != WindowVisualState.Normal)
                wp.SetWindowVisualState(WindowVisualState.Normal);
            var pagina = ApriEcu(window);
            Wait.ForCondition(() => Intestazioni(pagina).Count >= 4, "intestazioni Componenti presenti");
            // scala UIA/logico ricavata dalla colonna a larghezza fissa (180 logici).
            var scala = Intestazioni(pagina)[1].BoundingRectangle.Width / 180.0;

            var hs = Intestazioni(pagina);
            var gridRect = pagina.Grid.BoundingRectangle;
            var h = hs[0];
            var prima = h.BoundingRectangle;
            _out.WriteLine($"scala={scala:0.00} colonne={string.Join(" ", hs.Select(x => x.BoundingRectangle.Width))} griglia={gridRect.Width}");
            var y = prima.Top + prima.Height / 2;
            Mouse.Drag(new Point(prima.Right - 2, y), new Point(prima.Right - 2 + (int)(DeltaStar * scala), y));
            Wait.ForCondition(() => Intestazioni(pagina)[0].BoundingRectangle.Width >= prima.Width + (DeltaStar - 30) * scala, "la colonna Star si allarga");
            hs = Intestazioni(pagina);
            var dopo = hs[0].BoundingRectangle.Width;
            _out.WriteLine($"dopo drag colonne={string.Join(" ", hs.Select(x => x.BoundingRectangle.Width))} (ultima Right={hs[^1].BoundingRectangle.Right} griglia Right={gridRect.Right})");
            Assert.True(hs[1].BoundingRectangle.Width >= 170 * scala, "la colonna a larghezza fissa non deve essere schiacciata");
            Assert.True(hs[0].BoundingRectangle.Width >= 160 * scala - 3, "la colonna Star non scende sotto MinWidth");
            Assert.True(hs[^1].BoundingRectangle.Right > gridRect.Right || ScorrimentoOrizzontaleVisibile(pagina),
                "con colonne piu' larghe del contenitore deve comparire lo scorrimento orizzontale");

            App.Dispose();
            using var app2 = TestLoomApplication.Launch(Workspace);
            var window2 = app2.WaitForMainWindow();
            var pagina2 = ApriEcu(window2);
            Wait.ForCondition(() => Intestazioni(pagina2).Count >= 4, "intestazioni Componenti presenti dopo riavvio");
            Wait.ForCondition(() => Math.Abs(Intestazioni(pagina2)[0].BoundingRectangle.Width - dopo) <= 5 * scala, "larghezza Star ripristinata (+-5)");
            _out.WriteLine($"dopo riavvio larghezza={Intestazioni(pagina2)[0].BoundingRectangle.Width} (prima {dopo})");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ColonnaStarAllargata_ScorrimentoOrizzontaleELarghezzaPersistente), ex); }
    }

    private static bool ScorrimentoOrizzontaleVisibile(MasterDataEditorPage pagina) =>
        pagina.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.ScrollBar))
            .Any(sb => sb.BoundingRectangle.Width > sb.BoundingRectangle.Height && !sb.Properties.IsOffscreen.ValueOrDefault);
}
