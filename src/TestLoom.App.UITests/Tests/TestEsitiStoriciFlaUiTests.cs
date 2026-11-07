using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// I due Esito storici non sono piu' selezionabili nell'editor di una
/// NUOVA prova, ma vecchi archivi possono contenerli ancora. Questa suite
/// non cambia la UI o il dominio: crea il JSON con i repository di test,
/// poi apre veramente la pagina Test tramite FlaUI e verifica che la lettura
/// non converta gli esiti, non crei NC e non alteri i file.
/// 5 tipi x 2 esiti storici = 10 casi di compatibilita' in sola lettura.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "TestNcEndToEnd")]
[Trait("Category", "LongE2E")]
public sealed class TestEsitiStoriciFlaUiTests
{
    public static IEnumerable<object[]> Scenari()
    {
        foreach (var ambito in TestEsitiNcMatriceFlaUiTests.Ambiti)
        {
            yield return new object[] { ambito, Esito.NonEseguitoNA };
            yield return new object[] { ambito, Esito.DaRivedere };
        }
    }

    [Theory]
    [MemberData(nameof(Scenari))]
    public void AperturaTestLegacy_NonModificaEsitoENonGeneraNc(
        string ambito, Esito esitoStorico)
    {
        using var workspace = TestEsitiNcMatriceFlaUiTests.WorkspacePerAmbito(ambito);
        var caso = $"{ambito}/legacy/{esitoStorico}";
        Guid uid;
        string idTest;
        using (var state = new AppState(new AppConfigService(workspace.ConfigPath),
                   runBackgroundMaintenance: false))
        {
            var tipo = state.Master.TestTemplate.Load().Single(t => t.Nome == ambito);
            var tc = TestCoverageSeed.SeedTest(state,
                "TC-QA-LEGACY-01", "QA risultato storico - " + caso,
                tipo.Uid, TestWorkspace.DefaultEcuName, esitoStorico);
            uid = tc.Uid;
            idTest = tc.Id;
        }

        var fileTest = Assert.Single(TestCoverageSeed.FileTestCase(workspace));
        var hashPrima = TestCoverageSeed.HashFile(fileTest);
        using var app = TestLoomApplication.Launch(workspace);
        try
        {
            var window = app.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToTestList().OpenOnlyRow();
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1,
                "operazione legacy esposta nella UI in sola lettura");

            // Nessuna navigazione in sola lettura deve generare automaticamente
            // una segnalazione o una NC, né riscrivere la fotografia originale.
            Assert.False(editor.HaSegnalazioniDaClassificare);
            var letto = TestCoverageSeed.LeggiTestCase(fileTest);
            Assert.Equal(idTest, letto.Id);
            Assert.Equal(uid, letto.Uid);
            Assert.Equal(esitoStorico,
                Assert.Single(Assert.Single(letto.Esecuzioni).Condizioni).Esito);
            Assert.Equal(hashPrima, TestCoverageSeed.HashFile(fileTest));
            Assert.Empty(TestCoverageSeed.FileSegnalazioni(workspace));
            Assert.Empty(TestCoverageSeed.FileNonConformita(workspace));
        }
        catch (Exception ex)
        {
            var dir = FailureArtifacts.Capture(app,
                nameof(AperturaTestLegacy_NonModificaEsitoENonGeneraNc), ex, step: caso);
            throw new Exception($"Lettura legacy fallita per {caso}. Diagnostica: {dir}", ex);
        }
    }
}
