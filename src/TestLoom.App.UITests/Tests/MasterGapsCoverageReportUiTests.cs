using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Copertura FlaUI del Report non ancora esercitata: dialog "Ordina righe" (conferma con ordine salvato nel config e
/// riflesso alla riapertura, annullamento senza scritture), dialog "Ordina colonne" (conferma con ordine rispettato dalla
/// matrice) e cambio dell'asse X della matrice. Il seme e' diretto via AppState: 2 componente, 2 Veicoli, 2 Funzionalita' in
/// due categorie presenti nel filtro di default, 2 Test con esito diverso.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
public sealed class MasterGapsCoverageReportUiTests : UiTestBase
{
    private const string Componente1 = "Componente-R1";
    private const string Componente2 = "Componente-R2";
    private const string Veicolo1 = "Veicolo-R1";
    private const string Veicolo2 = "Veicolo-R2";
    private const string Funz1 = "Funzionalita-R1";
    private const string Funz2 = "Funzionalita-R2";
    private const string CatBase = "Diag. base";
    private const string CatAvanzata = "Diagnostica avanzata";

    public MasterGapsCoverageReportUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(MasterGapsCoverageReportUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);

        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = Componente1, VeicoliAssociati = { Veicolo1 }, ModificatoDa = "UITests" },
            new() { Nome = Componente2, VeicoliAssociati = { Veicolo2 }, ModificatoDa = "UITests" },
        });
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = Veicolo1, ModificatoDa = "UITests" },
            new() { Nome = Veicolo2, ModificatoDa = "UITests" },
        });
        state.Master.Funzionalita.Save(new List<TestLoom.App.Models.Funzionalita>
        {
            new() { Nome = Funz1, Categorie = { CatBase }, ModificatoDa = "UITests" },
            new() { Nome = Funz2, Categorie = { CatAvanzata }, ModificatoDa = "UITests" },
        });
        SalvaTest(state, "TC-R-0001", Componente1, Veicolo1, Funz1, Esito.Conforme);
        SalvaTest(state, "TC-R-0002", Componente2, Veicolo2, Funz2, Esito.NonConforme);
        return ws;
    }

    private static void SalvaTest(AppState state, string id, string componente, string veicolo, string funz, Esito esito)
    {
        var tc = new TestCase { Id = id, Componente = componente, Veicolo = veicolo, Funzionalita = { funz } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now,
            Condizioni = { new Condizione { Funzionalita = funz, Esito = esito } },
        });
        state.TestCases.Save(tc, true);
    }

    private ReportPage ApriReport(Window window) => new MainShellPage(window, App).GoToReport();

    /// <summary>Voci in ordine nella lista del dialogo "Ordina".</summary>
    private static List<string> VociLista(Window dialogo) =>
        ElementFinders.WaitListBoxById(dialogo, "OrdinaValori_Lista").Items.Select(i => i.Text).ToList();

    /// <summary>Ordine salvato nel config.json per l'operatore attivo e la dimensione indicata (null se assente).</summary>
    private IReadOnlyList<string>? OrdineSalvato(string dimensione) =>
        OrdineAssiReportService.Leggi(new AppConfigService(Workspace.ConfigPath).Load(), RuoloOperatore.Master, dimensione);

    [Fact]
    public void OrdinaRighe_Conferma_SalvaLOrdineNelConfigERiflettelolaRiapertura()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);

            // Asse righe della matrice = categorie di funzionalita' (l'asse colonne e' il componente).
            var dialogo = pagina.ApriDialogoOrdine(righe: true);
            Assert.Equal(new[] { CatBase, CatAvanzata }, VociLista(dialogo));
            pagina.SpostaSuNelDialogo(dialogo, CatAvanzata);
            pagina.ConfermaDialogoOrdine(dialogo);

            Wait.ForCondition(() => OrdineSalvato(DimensioniMatrice.CategoriaFunzionalita)?.SequenceEqual(new[] { CatAvanzata, CatBase }) == true,
                "ordine delle righe (categorie) salvato in config.json");
            var riaperto = pagina.ApriDialogoOrdine(righe: true);
            Assert.Equal(new[] { CatAvanzata, CatBase }, VociLista(riaperto));
            pagina.AnnullaDialogoOrdine(riaperto);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OrdinaRighe_Conferma_SalvaLOrdineNelConfigERiflettelolaRiapertura), ex);
        }
    }

    [Fact]
    public void OrdinaRighe_Annulla_NonScriveNelConfigENonCambiaLOrdine()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);

            var dialogo = pagina.ApriDialogoOrdine(righe: true);
            pagina.SpostaSuNelDialogo(dialogo, CatAvanzata);
            pagina.AnnullaDialogoOrdine(dialogo);

            Assert.Null(OrdineSalvato(DimensioniMatrice.CategoriaFunzionalita));
            var riaperto = pagina.ApriDialogoOrdine(righe: true);
            Assert.Equal(new[] { CatBase, CatAvanzata }, VociLista(riaperto));
            pagina.AnnullaDialogoOrdine(riaperto);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OrdinaRighe_Annulla_NonScriveNelConfigENonCambiaLOrdine), ex);
        }
    }

    [Fact]
    public void OrdinaColonne_Conferma_SalvaLOrdineELaMatriceLoRispetta()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var pagina = ApriReport(window);

            // Asse colonne della matrice = componente: "Ordina colonne" riordina i componenti e la matrice deve rispettarlo.
            var dialogo = pagina.ApriDialogoOrdine(righe: false);
            Assert.Equal(new[] { Componente1, Componente2 }, VociLista(dialogo));
            pagina.SpostaSuNelDialogo(dialogo, Componente2);
            pagina.ConfermaDialogoOrdine(dialogo);

            Wait.ForCondition(() => OrdineSalvato(DimensioniMatrice.Componente)?.SequenceEqual(new[] { Componente2, Componente1 }) == true,
                "ordine delle colonne (componente) salvato in config.json");
            Assert.Equal(new[] { Componente2, Componente1 }, pagina.OrdineColonne(Componente1, Componente2));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(OrdinaColonne_Conferma_SalvaLOrdineELaMatriceLoRispetta), ex);
        }
    }

}
