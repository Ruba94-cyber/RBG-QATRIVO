using System.Drawing;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;

namespace TestLoom.App.UITests.Tests;

/// <summary>Regressioni della revisione 6.4.6 sul layout colonne: colonna nascosta e riaffiorata mantiene la larghezza,
/// un click sul bordo dell'intestazione (senza trascinare) non salva nulla, un layout pre-6.4.6 non viene
/// riscritto come v2 dal solo caricamento.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Layout")]
public sealed class LayoutColonneRegressioniTests : UiTestBase
{
    private const string ChiaveEcu = TestWorkspace.DefaultOperatorName + "|Master|componente";

    public LayoutColonneRegressioniTests()
        : base(TestWorkspace.CreateLoggedIn(nameof(LayoutColonneRegressioniTests))) { }

    private static List<AutomationElement> Intestazioni(MasterDataEditorPage pagina) =>
        pagina.Grid.FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .Where(h => h.BoundingRectangle.Width > 0).OrderBy(h => h.BoundingRectangle.Left).ToList();

    private static MasterDataEditorPage ApriEcu(Window window) => new MainShellPage(window).GoToImpostazioni().VaiAEcu();

    private static void Massimizza(Window window)
    {
        if (window.Patterns.Window.TryGetPattern(out var wp) && wp.WindowVisualState.Value != WindowVisualState.Maximized)
            wp.SetWindowVisualState(WindowVisualState.Maximized);
    }

    private AppConfig LeggiConfig() => new AppConfigService(Workspace.ConfigPath).Load();

    private static void ClickVoceColonna(Window window, AutomationElement header, string nome)
    {
        var r = header.BoundingRectangle;
        var punto = new Point((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
        var voce = Wait.ForElement(() =>
        {
            Mouse.RightClick(punto);
            return window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(nome)));
        }, $"voce di menu colonna '{nome}'");
        voce.Click();
    }

    [Fact]
    public void ColonnaNascostaERimostrata_ConservaLaLarghezzaOriginale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            Massimizza(window);
            var pagina = ApriEcu(window);
            Wait.ForCondition(() => Intestazioni(pagina).Count >= 5, "intestazioni Componenti presenti");
            var hs = Intestazioni(pagina);
            var nome = hs[1].Name;
            var larghezzaOriginale = hs[1].BoundingRectangle.Width;
            ClickVoceColonna(window, hs[0], nome);
            Wait.ForCondition(() => Intestazioni(pagina).All(h => h.Name != nome), "colonna nascosta");

            App.Dispose();
            using var app2 = TestLoomApplication.Launch(Workspace);
            var window2 = app2.WaitForMainWindow();
            Massimizza(window2);
            var pagina2 = ApriEcu(window2);
            Wait.ForCondition(() => Intestazioni(pagina2).Count >= 3 && Intestazioni(pagina2).All(h => h.Name != nome), "colonna ancora nascosta dopo riavvio");
            ClickVoceColonna(window2, Intestazioni(pagina2)[0], nome);
            Wait.ForCondition(() => Intestazioni(pagina2).Any(h => h.Name == nome), "colonna rimostrata");
            var rimostrata = Intestazioni(pagina2).First(h => h.Name == nome).BoundingRectangle.Width;
            Assert.True(rimostrata >= larghezzaOriginale - 5, $"larghezza {rimostrata} < originale {larghezzaOriginale}");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ColonnaNascostaERimostrata_ConservaLaLarghezzaOriginale), ex); }
    }

    [Fact]
    public void ClickSulBordoDellIntestazione_SenzaTrascinare_NonSalvaIlLayout()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriEcu(window);
            Wait.ForCondition(() => Intestazioni(pagina).Count >= 4, "intestazioni Componenti presenti");
            var r = Intestazioni(pagina)[1].BoundingRectangle;
            Mouse.Click(new Point((int)r.Right - 3, (int)(r.Top + r.Height / 2)));
            Thread.Sleep(500);
            App.Dispose();
            Assert.False(LeggiConfig().LayoutTabelle.ContainsKey(ChiaveEcu), "un click sul bordo non deve salvare il layout");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ClickSulBordoDellIntestazione_SenzaTrascinare_NonSalvaIlLayout), ex); }
    }

    [Fact]
    public void LayoutPre646_NonVieneRiscrittoComeV2DalSoloCaricamento()
    {
        try
        {
            App.Dispose();
            var service = new AppConfigService(Workspace.ConfigPath);
            var config = service.Load();
            config.LayoutTabelle[ChiaveEcu] = new TableLayoutPreference
            {
                Versione = 0,
                Colonne =
                [
                    new() { Indice = 0, Ordine = 1, Larghezza = 40, Visibile = true },
                    new() { Indice = 1, Ordine = 0, Larghezza = 40, Visibile = true },
                    new() { Indice = 2, Ordine = 2, Larghezza = 40, Visibile = true },
                    new() { Indice = 3, Ordine = 3, Larghezza = 40, Visibile = true },
                    new() { Indice = 4, Ordine = 4, Larghezza = 40, Visibile = true },
                ]
            };
            service.Save(config);
            using (var app2 = TestLoomApplication.Launch(Workspace))
            {
                var pagina = ApriEcu(app2.WaitForMainWindow());
                Wait.ForCondition(() => Intestazioni(pagina).Count >= 4, "intestazioni Componenti presenti");
                Thread.Sleep(800);
            }
            Assert.Equal(0, LeggiConfig().LayoutTabelle[ChiaveEcu].Versione);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(LayoutPre646_NonVieneRiscrittoComeV2DalSoloCaricamento), ex); }
    }
}
