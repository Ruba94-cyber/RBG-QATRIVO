using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using Wait = TestLoom.App.UITests.Infrastructure.Wait;

namespace TestLoom.App.UITests.Tests;

/// <summary>Piano 6.5.0 punto 5: selettore del tipo di rappresentazione dei grafici della Home. Copre in UI
/// reale i tipi offerti per dato (categorico vs temporale), il cambio tipo che aggiorna il grafico visibile,
/// la preferenza locale che sopravvive al riavvio e il fatto che NON finisca nella cartella condivisa.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Regression")]
public sealed class HomeGraficiTipoUiTests : UiTestBase
{
    public HomeGraficiTipoUiTests() : base(TestWorkspace.CreateLoggedIn(nameof(HomeGraficiTipoUiTests), seedTestFixtures: false)) { }

    [Fact]
    public void Selettori_OffronoSoloITipiCompatibiliConIlDato()
    {
        try
        {
            var home = new MainShellPage(App.WaitForMainWindow()).GoToHome();

            Assert.Equal(new[] { "Barre verticali", "Barre orizzontali", "Torta", "Treemap" }, home.TipiOfferti("copertura"));
            Assert.Equal(new[] { "Linea", "Area" }, home.TipiOfferti("andamento"));
            Assert.Equal(new[] { "Ciambella", "Torta", "Barre verticali", "Barre orizzontali", "Treemap", "Linea", "Area" }, home.TipiOfferti("donut"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Selettori_OffronoSoloITipiCompatibiliConIlDato), ex); }
    }

    [Fact]
    public void SelettoreTipo_ECheckBox_HannoAutomationIdStabiliEValoriPredefiniti()
    {
        try
        {
            var home = new MainShellPage(App.WaitForMainWindow()).GoToHome();
            foreach (var id in new[] { "copertura", "andamento", "donut" })
            {
                Assert.NotNull(home.GraficoCheckBox(id));
                Assert.NotNull(home.SelettoreTipo(id));
            }
            Assert.Equal("Ciambella", home.TipoSelezionatoNelSelettore("donut"));
            Assert.Equal("Barre verticali", home.TipoSelezionatoNelSelettore("copertura"));
            Assert.Equal("Linea", home.TipoSelezionatoNelSelettore("andamento"));
            Wait.ForCondition(() => home.TipoRenderizzato("donut") == "Ciambella", "il grafico predefinito (donut) e' reso come Ciambella");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(SelettoreTipo_ECheckBox_HannoAutomationIdStabiliEValoriPredefiniti), ex); }
    }

    [Fact]
    public void CambioTipo_AggiornaIlGraficoVisibile_PerCategoricoETemporale()
    {
        try
        {
            var home = new MainShellPage(App.WaitForMainWindow()).GoToHome();

            home.ImpostaTipo("donut", "Treemap");
            Wait.ForCondition(() => home.TipoRenderizzato("donut") == "Treemap", "il donut diventa Treemap");
            home.ImpostaTipo("donut", "Barre orizzontali");
            Wait.ForCondition(() => home.TipoRenderizzato("donut") == "BarreOrizzontali", "il donut diventa Barre orizzontali");

            home.SelezionaGrafico("copertura");
            Wait.ForCondition(() => home.TipoRenderizzato("copertura") == "BarreVerticali", "copertura parte come Barre verticali");
            home.ImpostaTipo("copertura", "Torta");
            Wait.ForCondition(() => home.TipoRenderizzato("copertura") == "Torta", "copertura diventa Torta");

            home.SelezionaGrafico("andamento");
            Wait.ForCondition(() => home.TipoRenderizzato("andamento") == "Linea", "andamento parte come Linea");
            home.ImpostaTipo("andamento", "Area");
            Wait.ForCondition(() => home.TipoRenderizzato("andamento") == "Area", "andamento diventa Area");
            // il cambio tipo di un grafico non altera gli altri
            Assert.Equal("BarreOrizzontali", home.TipoRenderizzato("donut"));
            Assert.Equal("Torta", home.TipoRenderizzato("copertura"));
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CambioTipo_AggiornaIlGraficoVisibile_PerCategoricoETemporale), ex); }
    }

    [Fact]
    public void Preferenza_SopravvivAlRiavvio_ENonEsceDalPcLocale()
    {
        try
        {
            var home = new MainShellPage(App.WaitForMainWindow()).GoToHome();
            home.ImpostaTipo("donut", "Treemap");
            Wait.ForCondition(() => home.TipoRenderizzato("donut") == "Treemap", "il donut diventa Treemap");
            home.SelezionaGrafico("andamento");
            home.ImpostaTipo("andamento", "Area");
            Wait.ForCondition(() => home.TipoRenderizzato("andamento") == "Area", "andamento diventa Area");

            // La preferenza e' salvata nel config LOCALE del PC, non nella cartella condivisa.
            Wait.ForCondition(() => File.ReadAllText(Workspace.ConfigPath).Contains("Home.TipiGrafico"),
                "preferenza tipo grafico scritta nel config locale");
            App.Dispose();
            var sharedJson = Directory.GetFiles(Workspace.WorkspaceDir, "*.json", SearchOption.AllDirectories);
            Assert.DoesNotContain(sharedJson, f => File.ReadAllText(f).Contains("Home.TipiGrafico"));

            using var app2 = TestLoomApplication.Launch(Workspace);
            var home2 = new MainShellPage(app2.WaitForMainWindow()).GoToHome();
            Wait.ForCondition(() => home2.TipoSelezionatoNelSelettore("donut") == "Treemap", "il selettore ricorda Treemap");
            Wait.ForCondition(() => home2.TipoRenderizzato("donut") == "Treemap", "il donut e' reso come Treemap dopo il riavvio");
            Wait.ForCondition(() => home2.TipoRenderizzato("andamento") == "Area", "l'andamento e' reso come Area dopo il riavvio");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(Preferenza_SopravvivAlRiavvio_ENonEsceDalPcLocale), ex); }
    }
}
