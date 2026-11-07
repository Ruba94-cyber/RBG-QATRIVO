using TestLoom.App.Models;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Un solo Test reale, quattro operazioni, tre segnalazioni, due NC distinte e
/// una esclusione motivata. Copre la coerenza dell'intera scrittura Test/NC/Seg
/// dopo il commit e la riapertura, per ciascuno dei 5 tipi di Test standard.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "TestNcEndToEnd")]
[Trait("Category", "LongE2E")]
public sealed class TestMultiNcFlaUiTests
{
    public static IEnumerable<object[]> Ambiti() =>
        TestEsitiNcMatriceFlaUiTests.Ambiti.Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(Ambiti))]
    public void QuattroOperazioni_DueNcDistinte_UnaEsclusa_RestaTuttoCorrelatoDopoRiapertura(string ambito)
    {
        using var workspace = TestEsitiNcMatriceFlaUiTests.WorkspacePerAmbito(ambito);
        using var app = TestLoomApplication.Launch(workspace);
        try
        {
            var window = app.WaitForMainWindow();
            var descrizione = "QA multi NC - " + ambito;
            var editor = TestEsitiNcMatriceFlaUiTests.CreaTestDallaUi(app, ambito, descrizione);

            editor.AggiungiCondizione();
            editor.AggiungiCondizione();
            editor.AggiungiCondizione();
            Wait.ForCondition(() => editor.NumeroCondizioni() == 4,
                "quattro operazioni indipendenti nello stesso Test");

            TestEsitiNcMatriceFlaUiTests.ImpostaEsito(editor, 0, Esito.NonConforme);
            TestEsitiNcMatriceFlaUiTests.ImpostaEsito(editor, 1, Esito.ConformeConDeviazioni);
            TestEsitiNcMatriceFlaUiTests.ImpostaEsito(editor, 2, Esito.NonConforme);
            TestEsitiNcMatriceFlaUiTests.ImpostaEsito(editor, 3, Esito.Conforme);
            var uidOperazioni = Enumerable.Range(0, 4)
                .Select(i => Guid.Parse(editor.GetUidCondizione(i))).ToArray();
            Assert.Equal(4, uidOperazioni.Distinct().Count());

            Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() == 3,
                "solo le tre prove con esito problematico generano segnalazioni");

            // Non e' sufficiente usare il pulsante primario: il suggeritore
            // potrebbe proporre di collegare la seconda segnalazione alla
            // prima NC, ma il requisito del test e' crearne due DIFFERENTI.
            TestEsitiNcMatriceFlaUiTests.CreaNc(app, editor);
            Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() == 2,
                "prima NC classificata, due segnalazioni ancora in sospeso");

            TestEsitiNcMatriceFlaUiTests.CreaNc(app, editor);
            Wait.ForCondition(() => editor.NumeroSegnalazioniDaClassificare() == 1,
                "seconda NC classificata senza unirla alla prima");

            const string motivo = "Terza anomalia non pertinente, esclusa dal test QA";
            editor.EscludiSegnalazione(0, motivo);
            Wait.ForCondition(() => editor.SegnalazioneEsclusa(0),
                "terza segnalazione esclusa e motivata");

            editor.GoToTab(4);
            editor.Save();
            // Avviso soft previsto dall'app (Funzionalita' mancante): si sceglie "Salva Test incompleto".
            TestLoom.App.UITests.Helpers.ShellHomeSettingsCoverageSupport.ConfermaSeCompare(app, TimeSpan.FromSeconds(6));
            Wait.ForCondition(() =>
                ElementFinders.ExistsById(window, "TestList_Title_Text"),
                "Test multi-operazione salvato", TimeSpan.FromSeconds(35));

            var tc = TestCoverageSeed.LeggiTestCase(
                Assert.Single(TestCoverageSeed.FileTestCase(workspace)));
            Assert.Equal(descrizione, tc.DescrizioneBreve);
            var revisione = Assert.Single(tc.Esecuzioni);
            Assert.Equal(4, revisione.Condizioni.Count);
            Assert.Equal(new[]
            {
                Esito.NonConforme, Esito.ConformeConDeviazioni,
                Esito.NonConforme, Esito.Conforme
            }, revisione.Condizioni.Select(c => c.Esito));
            Assert.Equal(Esito.NonConforme, revisione.Esito);
            for (var i = 0; i < 4; i++)
                Assert.Equal(uidOperazioni[i], revisione.Condizioni[i].Uid);

            var segnalazioni = TestCoverageSeed.FileSegnalazioni(workspace)
                .Select(TestCoverageSeed.LeggiSegnalazione).ToArray();
            var ncs = TestCoverageSeed.FileNonConformita(workspace)
                .Select(TestCoverageSeed.LeggiNonConformita).ToArray();
            Assert.Equal(3, segnalazioni.Length);
            Assert.Equal(2, ncs.Length);
            Assert.Equal(2, ncs.Select(n => n.Uid).Distinct().Count());
            Assert.Equal(2, ncs.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            var escluse = segnalazioni.Where(s => s.Stato == StatoSegnalazione.Esclusa).ToArray();
            var classificate = segnalazioni.Where(s => s.Stato == StatoSegnalazione.Classificata).ToArray();
            var esclusa = Assert.Single(escluse);
            Assert.Equal(motivo, esclusa.MotivazioneEsclusione);
            Assert.Null(esclusa.AnomaliaUid);
            Assert.Equal(2, classificate.Length);

            // Il collegamento e' biunivoco: Test -> segnalazione -> NC e
            // NC -> Test/operazione. Nessuna NC duplicata o orfana.
            foreach (var seg in classificate)
            {
                Assert.Equal(tc.Id, seg.TestId);
                var nc = Assert.Single(ncs, x => x.Uid == seg.AnomaliaUid);
                Assert.Equal(tc.Id, nc.TestCaseId);
                Assert.Equal(StatoAnomalia.Confermata, nc.StatoWorkflow);
                Assert.Equal(SeveritaAnomalia.Medium, nc.Severita);
                Assert.Contains(nc.ProveCollegate, p =>
                    p.TestId == tc.Id &&
                    p.CondizioneUid == seg.CondizioneUid &&
                    p.EsecuzioneIterazione == 1);
                var operazione = Assert.Single(revisione.Condizioni,
                    c => c.Uid == seg.CondizioneUid);
                Assert.Equal(seg.Uid, operazione.SegnalazioneUid);
            }
            Assert.Equal(tc.Id, esclusa.TestId);
            Assert.Equal(uidOperazioni[2], esclusa.CondizioneUid);
            Assert.Equal(esclusa.Uid, revisione.Condizioni[2].SegnalazioneUid);
            Assert.Null(revisione.Condizioni[3].SegnalazioneUid);

            var riaperto = new TestListPage(window, window).OpenOnlyRow();
            Wait.ForCondition(() => riaperto.NumeroCondizioni() == 4,
                "quattro operazioni ancora visibili alla riapertura");
            // Il pannello resta visibile per la segnalazione ESCLUSA (serve a poter annullare l'esclusione):
            // "da classificare" significa quindi nessun selettore di severita' ancora attivo.
            Assert.Equal(0, riaperto.NumeroSegnalazioniDaClassificare());

            // La semplice lettura dopo riapertura non deve ricreare NC o
            // segnalazioni, nemmeno quando il ViewModel aggiorna il pannello.
            Assert.Equal(2, TestCoverageSeed.FileNonConformita(workspace).Count);
            Assert.Equal(3, TestCoverageSeed.FileSegnalazioni(workspace).Count);
        }
        catch (Exception ex)
        {
            var diagnostica = FailureArtifacts.Capture(app,
                nameof(QuattroOperazioni_DueNcDistinte_UnaEsclusa_RestaTuttoCorrelatoDopoRiapertura),
                ex, step: ambito);
            throw new Exception($"Flusso multi-NC '{ambito}' fallito. Diagnostica: {diagnostica}", ex);
        }
    }
}
