using TestLoom.App.Models;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Gate di completamento end-to-end, senza chiamare direttamente i comandi
/// del ViewModel: l'operatore salva un Test In corso, lo riapre, prova
/// "Concluso" e verifica sia l'UI sia i JSON dopo il secondo Salva.
/// 5 tipi x 7 casi = 35 processi reali indipendenti.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "TestNcEndToEnd")]
[Trait("Category", "LongE2E")]
public sealed class TestChiusuraNcMatriceFlaUiTests
{
    public static IEnumerable<object[]> Scenari()
    {
        foreach (var ambito in TestEsitiNcMatriceFlaUiTests.Ambiti)
        {
            // Conclusione legittima: un risultato conforme o N.A. non genera NC.
            yield return new object[] { ambito, Esito.Conforme, false, true };
            yield return new object[] { ambito, Esito.NonApplicabile, false, true };
            // Un'operazione ancora da eseguire non consente Concluso.
            yield return new object[] { ambito, Esito.DaEseguire, false, false };
            // Un NOK/deviation puo' essere salvato come In corso ma non concluso
            // se e' da classificare o ha generato una NC ancora aperta.
            foreach (var esito in new[] { Esito.NonConforme, Esito.ConformeConDeviazioni })
            {
                yield return new object[] { ambito, esito, false, false };
                yield return new object[] { ambito, esito, true, false };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenari))]
    public void DaInCorsoAConcluso_RispettaIlGateNcESegnalazioni(
        string ambito, Esito esito, bool creaNc, bool deveConcludersi)
    {
        using var workspace = TestEsitiNcMatriceFlaUiTests.WorkspacePerAmbito(ambito);
        using var app = TestLoomApplication.Launch(workspace);
        var caso = $"{ambito}/{esito}/nuovaNc={creaNc}/concluso={deveConcludersi}";
        try
        {
            var window = app.WaitForMainWindow();
            var editor = TestEsitiNcMatriceFlaUiTests.CreaTestDallaUi(
                app, ambito, "QA gate conclusione - " + caso);
            TestEsitiNcMatriceFlaUiTests.ImpostaEsito(editor, 0, esito);
            if (creaNc)
                TestEsitiNcMatriceFlaUiTests.CreaNc(app, editor);

            editor.GoToTab(4);
            editor.Save();
            TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport.ConfermaSeCompare(app, TimeSpan.FromSeconds(6));   // avviso soft "Funzionalita' mancante"
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Title_Text"),
                "Test In corso salvato prima del tentativo di conclusione",
                TimeSpan.FromSeconds(35));

            var percorso = Assert.Single(TestCoverageSeed.FileTestCase(workspace));
            var prima = TestCoverageSeed.LeggiTestCase(percorso);
            Assert.Equal(StatoLavoroTest.InCorso, prima.StatoLavoro);
            Assert.Equal(esito, Assert.Single(Assert.Single(prima.Esecuzioni).Condizioni).Esito);

            var problematico = esito is Esito.NonConforme or Esito.ConformeConDeviazioni;
            var ncAttese = creaNc ? 1 : 0;
            var segAttese = problematico ? 1 : 0;
            Assert.Equal(ncAttese, TestCoverageSeed.FileNonConformita(workspace).Count);
            Assert.Equal(segAttese, TestCoverageSeed.FileSegnalazioni(workspace).Count);

            // Riapertura effettiva: il secondo salvataggio passa dal lock, dalle
            // validazioni e dalla stessa transazione del pulsante UI.
            var riaperto = new TestListPage(window, window).OpenOnlyRow();
            riaperto.EnterEditMode();
            riaperto.SetStatoLavoro("Concluso");
            riaperto.Save();
            TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport.ConfermaSeCompare(app, TimeSpan.FromSeconds(6));   // avviso soft "Funzionalita' mancante"

            if (deveConcludersi)
            {
                Wait.ForCondition(() => ElementFinders.ExistsById(window, "TestList_Title_Text"),
                    "Test conforme/NA concluso", TimeSpan.FromSeconds(35));
                Wait.ForCondition(() =>
                    TestCoverageSeed.LeggiTestCase(percorso).StatoLavoro == StatoLavoroTest.Concluso,
                    "StatoLavoro Concluso persistito");
                Assert.Empty(TestCoverageSeed.FileNonConformita(workspace));
                Assert.Empty(TestCoverageSeed.FileSegnalazioni(workspace));
                Assert.Equal(esito,
                    TestCoverageSeed.LeggiTestCase(percorso).Esecuzioni[^1].Condizioni.Single().Esito);
            }
            else
            {
                // Il salvataggio rifiutato resta nell'editor, non pubblica lo
                // Stato Concluso e non elimina le NC/segnalazioni precedenti.
                Wait.ForCondition(() => riaperto.HasValidationErrors,
                    "gate di chiusura: Test con esito incompleto/NC aperta o pending",
                    TimeSpan.FromSeconds(25));
                Assert.False(ElementFinders.ExistsById(window, "TestList_Title_Text"));
                Assert.Equal(StatoLavoroTest.InCorso,
                    TestCoverageSeed.LeggiTestCase(percorso).StatoLavoro);
                Assert.Equal(ncAttese, TestCoverageSeed.FileNonConformita(workspace).Count);
                Assert.Equal(segAttese, TestCoverageSeed.FileSegnalazioni(workspace).Count);
                if (problematico)
                {
                    var seg = TestCoverageSeed.LeggiSegnalazione(
                        Assert.Single(TestCoverageSeed.FileSegnalazioni(workspace)));
                    Assert.Equal(creaNc ? StatoSegnalazione.Classificata :
                        StatoSegnalazione.DaClassificare, seg.Stato);
                    if (creaNc)
                    {
                        var nc = TestCoverageSeed.LeggiNonConformita(
                            Assert.Single(TestCoverageSeed.FileNonConformita(workspace)));
                        Assert.Equal(StatoAnomalia.Confermata, nc.StatoWorkflow);
                        Assert.Equal(nc.Uid, seg.AnomaliaUid);
                    }
                    else Assert.Null(seg.AnomaliaUid);
                }
            }
        }
        catch (Exception ex)
        {
            var dir = FailureArtifacts.Capture(app,
                nameof(DaInCorsoAConcluso_RispettaIlGateNcESegnalazioni),
                ex, step: caso);
            throw new Exception($"Gate Concluso fallito per {caso}. Diagnostica: {dir}", ex);
        }
    }
}
