using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI della ricerca e della paginazione su un catalogo master data grande (COPERTURA_FLAUI.md, sezione
/// Master / Impostazioni: "search/paging in large catalogs"). Il catalogo componente ha 231 voci sintetiche: una pagina del
/// catalogo contiene 200 voci (DimensionePaginaMaster). La griglia virtualizza le righe, quindi le asserzioni usano
/// le righe realizzate, il numero di pagina visibile e l'esito della ricerca, non il totale delle righe.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Navigation")]
[Trait("Category", "Regression")]
public sealed class MasterWuReportCoverageCatalogoUiTests : UiTestBase
{
    private const int VociSintetiche = 230;
    private const string ChiaveUnica = "Componente-PAG-215";

    public MasterWuReportCoverageCatalogoUiTests() : base(CreaFixture()) { }

    /// <summary>Catalogo componente con il fixture di base (componente-QA-01) più 230 voci sintetiche componente-PAG-001…230.</summary>
    private static TestWorkspace CreaFixture()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterWuReportCoverageCatalogoUiTests));
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var componente = state.Master.Componente.Load();
        for (var i = 1; i <= VociSintetiche; i++)
            componente.Add(new Componente { Nome = $"Componente-PAG-{i:000}", ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);
        return ws;
    }

    private EcuPage ApriCatalogoEcu()
    {
        var window = App.WaitForMainWindow();
        var componente = new MainShellPage(window).GoToImpostazioni().VaiAEcu();
        Wait.ForCondition(() => componente.PaginaCorrente == "1", "catalogo componente alla prima pagina");
        return new EcuPage(componente);
    }

    /// <summary>Alias di lettura del Page Object: rende le asserzioni dei test più leggibili.</summary>
    private sealed record EcuPage(MasterDataEditorPage Pagina)
    {
        public IReadOnlyList<string> Righe => Pagina.ChiaviVisibili();
    }

    [Fact]
    public void Ricerca_FiltraSuUnaSolaVoce_ESenzaCorrispondenze_EPoiTornaAlCatalogo()
    {
        try
        {
            var catalogo = ApriCatalogoEcu();

            catalogo.Pagina.Cerca(ChiaveUnica);
            Wait.ForCondition(() => catalogo.Righe.SequenceEqual(new[] { ChiaveUnica }), $"ricerca '{ChiaveUnica}': una sola riga");

            catalogo.Pagina.Cerca("NON-ESISTE-QA");
            Wait.ForCondition(() => catalogo.Righe.Count == 0, "ricerca senza corrispondenze: nessuna riga");

            catalogo.Pagina.Cerca(string.Empty);
            Wait.ForCondition(() => catalogo.Righe.Count > 0 && catalogo.Pagina.PaginaCorrente == "1",
                "ricerca azzerata: torna il catalogo dalla prima pagina");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Ricerca_FiltraSuUnaSolaVoce_ESenzaCorrispondenze_EPoiTornaAlCatalogo), ex);
        }
    }

    [Fact]
    public void Paginazione_PaginaSuccessivaCambiaLeRighe_EPrecedenteTorna()
    {
        try
        {
            var catalogo = ApriCatalogoEcu();
            var pagina1 = catalogo.Righe.ToHashSet();
            Assert.NotEmpty(pagina1);

            catalogo.Pagina.PaginaSuccessiva();
            Wait.ForCondition(() => catalogo.Pagina.PaginaCorrente == "2", "pagina 2 del catalogo componente");
            var pagina2 = catalogo.Righe.ToHashSet();
            Assert.NotEmpty(pagina2);
            Assert.Empty(pagina1.Intersect(pagina2)); // le righe realizzate sulle due pagine sono voci diverse

            catalogo.Pagina.PaginaPrecedente();
            Wait.ForCondition(() => catalogo.Pagina.PaginaCorrente == "1", "ritorno alla pagina 1");
            Wait.ForCondition(() => catalogo.Righe.Intersect(pagina1).Any(), "la pagina 1 mostra di nuovo le sue righe");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Paginazione_PaginaSuccessivaCambiaLeRighe_EPrecedenteTorna), ex);
        }
    }
}
