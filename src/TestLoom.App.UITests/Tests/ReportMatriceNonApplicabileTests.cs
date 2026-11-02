using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura end-to-end della marcatura manuale "N.A." per cella della matrice Report (Addendum piano
/// 6.4.0, punto 4: "nella matrice che vado a creare nei report devo poter mettere anche NA nel caso in cui non
/// sia applicabile"). Workspace seminato direttamente via AppState (stessa tecnica di AuditPropagazioneTests),
/// mai pilotando la UI per la semina. Assi di default della matrice (Componente × Categoria di Funzionalità) restano
/// quelli scelti al primo accesso alla pagina: nessuna interazione con le ComboBox degli assi è necessaria per
/// questo scenario, la Categoria "Diag. base" seminata rientra già nel filtro di default della pagina Report
/// (vedi ReportViewModel, defaultCategorie).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
public sealed class ReportMatriceNonApplicabileTests : IDisposable
{
    private const string Componente = "Componente-E2E-NA";
    private const string Veicolo = "Veicolo-E2E-NA";
    private const string Funzionalita = "Lettura guasto E2E";
    private const string Categoria = "Diag. base";

    /// <summary>Testo della cella in Stato % (distribuzione su tutte le categorie, 6.6.1): un solo Test Conforme = 100% OK.</summary>
    private const string DistribuzioneIniziale = "OK 100% · NOK 0% · dev. 0% · da eseg. 0% · non test. 0% · ! 0%";

    private readonly TestWorkspace _workspace;

    public ReportMatriceNonApplicabileTests()
    {
        _workspace = TestWorkspace.CreateLoggedIn(nameof(ReportMatriceNonApplicabileTests), seedTestFixtures: false);
        SeedScenarioMatrice(_workspace);
    }

    public void Dispose() => _workspace.Dispose();

    /// <summary>Un solo Test con un'unica condizione Conforme: la cella (Categoria, Componente) parte quindi al 100% OK
    /// (distribuzione Stato %) — un punto di partenza chiaro e diverso da "N.A." per verificare che la marcatura lo
    /// sostituisca davvero, e che rimuoverla lo faccia ritornare.</summary>
    private static void SeedScenarioMatrice(TestWorkspace ws)
    {
        var configService = new AppConfigService(ws.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        state.Master.Componente.Save(new List<Componente> { new() { Nome = Componente, VeicoliAssociati = { Veicolo }, ModificatoDa = "UITests" } });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = Veicolo, ModificatoDa = "UITests" } });
        state.Master.Funzionalita.Save(new List<TestLoom.App.Models.Funzionalita>
        {
            new() { Nome = Funzionalita, Categorie = { Categoria }, ModificatoDa = "UITests" }
        });

        var tc = new TestCase { Id = "TC-E2E-NA", Componente = Componente, Veicolo = Veicolo, Funzionalita = { Funzionalita } };
        tc.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now,
            Condizioni = { new Condizione { Funzionalita = Funzionalita, Esito = Esito.Conforme } },
        });
        state.TestCases.Save(tc, true);
    }

    [Fact]
    public void Master_SegnaEPoiRimuoveNA_LaCellaMostraNAConNotaEPoiTornaAlValoreCalcolato()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = app.WaitForMainWindow();
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale dopo avvio con operatore già attivo");

            var shell = new MainShellPage(window, app);
            var report = shell.GoToReport();

            Wait.ForCondition(() => report.CellaVisibile(Categoria, Componente), "cella (Categoria, componente) popolata nella matrice");
            Assert.Equal(DistribuzioneIniziale, report.TestoCella(Categoria, Componente));

            const string nota = "Non applicabile su questa configurazione E2E";
            report.SegnaNA(Categoria, Componente, nota);

            Wait.ForCondition(() => report.TestoCella(Categoria, Componente) == "N.A.", "cella marcata N.A. dopo la conferma");

            report.RimuoviNA(Categoria, Componente);

            Wait.ForCondition(() => report.TestoCella(Categoria, Componente) == DistribuzioneIniziale, "cella tornata al valore calcolato dopo la rimozione");
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(Master_SegnaEPoiRimuoveNA_LaCellaMostraNAConNotaEPoiTornaAlValoreCalcolato), ex);
            throw;
        }
        finally
        {
            app?.Dispose();
        }
    }
}
