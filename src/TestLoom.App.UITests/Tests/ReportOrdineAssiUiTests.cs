using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Ordine personalizzato delle colonne della matrice Report: dialogo "Ordina colonne…", sposta su un
/// valore, OK, e l'intestazione cambia posizione (default alfabetico: componente-A, componente-B).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Report")]
public sealed class ReportOrdineAssiUiTests : IDisposable
{
    private const string EcuA = "Componente-ORD-A";
    private const string EcuB = "Componente-ORD-B";
    private const string Categoria = "Diag. base";

    private readonly TestWorkspace _workspace;

    public ReportOrdineAssiUiTests()
    {
        _workspace = TestWorkspace.CreateLoggedIn(nameof(ReportOrdineAssiUiTests), seedTestFixtures: false);
        var configService = new AppConfigService(_workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = EcuA, VeicoliAssociati = { "Veicolo-ORD" }, ModificatoDa = "UITests" },
            new() { Nome = EcuB, VeicoliAssociati = { "Veicolo-ORD" }, ModificatoDa = "UITests" }
        });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Veicolo-ORD", ModificatoDa = "UITests" } });
        state.Master.Funzionalita.Save(new List<TestLoom.App.Models.Funzionalita>
        {
            new() { Nome = "Funz ORD", Categorie = { Categoria }, ModificatoDa = "UITests" }
        });
        foreach (var (id, componente) in new[] { ("TC-ORD-A", EcuA), ("TC-ORD-B", EcuB) })
        {
            var tc = new TestCase { Id = id, Componente = componente, Veicolo = "Veicolo-ORD", Funzionalita = { "Funz ORD" } };
            tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.Now, Condizioni = { new Condizione { Funzionalita = "Funz ORD", Esito = Esito.Conforme } } });
            state.TestCases.Save(tc, true);
        }
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void SpostaSuUnaColonna_LIntestazioneCambiaPosizione()
    {
        TestLoomApplication? app = null;
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var window = app.WaitForMainWindow();
            Wait.ForCondition(() => MainShellPage.IsShown(window), "shell principale");
            var report = new MainShellPage(window, app).GoToReport();

            Wait.ForCondition(() => report.CellaVisibile(Categoria, EcuA), "matrice popolata");
            Assert.Equal(new[] { EcuA, EcuB }, report.OrdineColonne(EcuA, EcuB));

            report.SpostaSu(righe: false, EcuB);

            Wait.ForCondition(() => report.OrdineColonne(EcuA, EcuB).SequenceEqual(new[] { EcuB, EcuA }), "colonne riordinate");
        }
        catch (Exception ex)
        {
            FailureArtifacts.Capture(app, nameof(SpostaSuUnaColonna_LIntestazioneCambiaPosizione), ex);
            throw;
        }
        finally { app?.Dispose(); }
    }
}
