using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using static TestLoom.App.Tests.LayoutHeadlessMisure;

namespace TestLoom.App.Tests;

[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class CampiStrumentoRevisioniStoricheHeadlessTests
{
    [Fact]
    public void CampiNelTemplateSemplice_ConsentonoRicordaERimuoviInRevisioneEInGruppo()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            const string strumento = "CaptureTool";
            const string nomeCampoRevisione = "VersioneSw";
            const string nomeCampoGruppo = "DatoGruppo";
            const string nomeGruppo = "Guasto / Gruppo prova";
            var strumenti = fixture.State.Master.Strumenti.Load();
            var capturetool = strumenti.SingleOrDefault(s => s.Nome == strumento);
            if (capturetool == null)
            {
                capturetool = new StrumentoDiagnostico { Nome = strumento };
                strumenti.Add(capturetool);
            }
            capturetool.Campi =
            [
                new DefinizioneCampoStrumento
                {
                    Nome = nomeCampoRevisione,
                    Ambito = TipoAmbitoCampo.PerRevisione,
                },
                new DefinizioneCampoStrumento
                {
                    Nome = nomeCampoGruppo,
                    Ambito = TipoAmbitoCampo.PerOperazione,
                    Gruppo = nomeGruppo,
                },
            ];
            fixture.State.Master.Strumenti.Save(strumenti);

            var test = fixture.State.TestCases.GetById(IdTestLayout)!;
            test.StrumentiUsati.Add(strumento);
            test.Esecuzioni[^1].Dettagli = new DettagliTestSnapshot { StrumentiUsati = [strumento] };

            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            Window? finestra = null;
            try
            {
                var pagina = new TestCaseEditorView { DataContext = vm };
                finestra = MostraInSidebar(pagina, 1280, 672);
                LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                vm.StrumentiOptions.Single(s => s.Valore == strumento).Selezionato = true;
                LayoutHeadlessSessione.Assesta(finestra);

                var campoRevisione = vm.CampiRevisioneCostanti.Single(c => c.Nome == nomeCampoRevisione);
                campoRevisione.Valore = "release-42";
                LayoutHeadlessSessione.Assesta(finestra);
                var ricordaRevisione = TrovaPulsanteDefault(pagina, campoRevisione, rimuovi: false);
                Assert.True(ricordaRevisione.IsVisible);
                ricordaRevisione.Command!.Execute(ricordaRevisione.CommandParameter);
                Assert.Equal("release-42", fixture.State.OperatoreCorrente!.ValoriDefaultCampi[$"{strumento}::{nomeCampoRevisione}"]);
                var rimuoviRevisione = TrovaPulsanteDefault(pagina, campoRevisione, rimuovi: true);
                Assert.True(rimuoviRevisione.IsVisible);
                rimuoviRevisione.Command!.Execute(rimuoviRevisione.CommandParameter);
                Assert.DoesNotContain($"{strumento}::{nomeCampoRevisione}", fixture.State.OperatoreCorrente!.ValoriDefaultCampi.Keys);

                var operazione = vm.RevisioneCorrente!.Condizioni.Single(c => c.Uid == fixture.Prima);
                var gruppo = operazione.GruppiVisual!.Single(g => g.Nome == nomeGruppo);
                var campoGruppo = Assert.Single(gruppo.Campi);
                var expander = pagina.GetVisualDescendants().OfType<Expander>().Single(e => ReferenceEquals(e.DataContext, gruppo));
                expander.IsExpanded = true;
                campoGruppo.Valore = "dato-di-prova";
                LayoutHeadlessSessione.Assesta(finestra);
                var ricordaGruppo = TrovaPulsanteDefault(pagina, campoGruppo, rimuovi: false);
                Assert.True(ricordaGruppo.IsVisible);
                ricordaGruppo.Command!.Execute(ricordaGruppo.CommandParameter);
                Assert.Equal("dato-di-prova", fixture.State.OperatoreCorrente!.ValoriDefaultCampi[$"{strumento}::{nomeCampoGruppo}"]);
                var rimuoviGruppo = TrovaPulsanteDefault(pagina, campoGruppo, rimuovi: true);
                Assert.True(rimuoviGruppo.IsVisible);
                rimuoviGruppo.Command!.Execute(rimuoviGruppo.CommandParameter);
                Assert.DoesNotContain($"{strumento}::{nomeCampoGruppo}", fixture.State.OperatoreCorrente!.ValoriDefaultCampi.Keys);
            }
            finally
            {
                finestra?.Hide();
                vm.Dispose();
                if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                    LayoutHeadlessSessione.AttendiComando(rilascio, TimeSpan.FromSeconds(30));
            }
        });
    }

    [Fact]
    public void CampiCustomNelTemplateSemplice_NonMostranoAzioniDefaultNonSupportate()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();

            var test = fixture.State.TestCases.GetById(IdTestLayout)!;
            var templates = fixture.State.Master.TestTemplate.Load();
            var template = templates.Single(t => t.Uid == test.TestTemplateUid);
            var campoPianificazione = new CampoTemplate { Nome = "Nota pianificazione custom" };
            var campoDettagli = new CampoTemplate { Nome = "Nota dettagli custom" };
            template.CampiPianificazione.Add(campoPianificazione);
            template.Sezioni[0].Campi.Add(campoDettagli);
            fixture.State.Master.TestTemplate.Save(templates);

            // I campi contengono già un valore: il template semplice non deve offrire un comando che
            // non può salvare una preferenza personale per un campo senza strumento.
            test.CampiCustom[campoPianificazione.Uid.ToString()] = "valore piano";
            test.CampiCustom[campoDettagli.Uid.ToString()] = "valore dettagli";
            fixture.State.TestCases.Save(test, isNew: false);

            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            Window? finestra = null;
            try
            {
                var pagina = new TestCaseEditorView { DataContext = vm };
                finestra = MostraInSidebar(pagina, 1280, 672);
                LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                LayoutHeadlessSessione.Assesta(finestra);

                VerificaCampoCustom(vm.CampiCustomPianificazioneVisual.Single(c => c.Nome == campoPianificazione.Nome));
                VerificaCampoCustom(vm.CampiCustomDettagliVisual.Single(c => c.Nome == campoDettagli.Nome));

                Assert.DoesNotContain("::Nota pianificazione custom", fixture.State.OperatoreCorrente!.ValoriDefaultCampi.Keys);
                Assert.DoesNotContain("::Nota dettagli custom", fixture.State.OperatoreCorrente.ValoriDefaultCampi.Keys);

                void VerificaCampoCustom(CampoStrumentoValore campo)
                {
                    Assert.Equal("", campo.Strumento);
                    Assert.False(campo.PuoRicordareDefault);
                    Assert.False(campo.MostraRicordaDefault);
                    Assert.False(campo.MostraRimuoviDefault);
                    Assert.False(TrovaPulsanteDefault(pagina, campo, rimuovi: false).IsVisible);
                    Assert.False(TrovaPulsanteDefault(pagina, campo, rimuovi: true).IsVisible);

                    // Anche un invio diretto del comando non deve creare un default personale.
                    vm.SalvaValoreComeMioDefaultCommand.Execute(campo.CampoPerRicordareDefault);
                    vm.RimuoviMioDefaultCommand.Execute(campo.CampoPerRicordareDefault);
                    Assert.False(campo.HaDefaultPersonale);
                }
            }
            finally
            {
                finestra?.Hide();
                vm.Dispose();
                if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                    LayoutHeadlessSessione.AttendiComando(rilascio, TimeSpan.FromSeconds(30));
            }
        });
    }

    [Fact]
    public void AprireEVisitareRevStorica_NonMaterializzaDefaultNeiDizionariDati()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            const string strumento = "CaptureTool";
            const string chiaveTest = strumento + "::Licenza";
            const string chiaveRevisione = strumento + "::VersioneSw";
            const string chiaveOperazione = strumento + "::Comando";

            var strumenti = fixture.State.Master.Strumenti.Load();
            var capturetool = strumenti.SingleOrDefault(s => s.Nome == strumento);
            if (capturetool == null)
            {
                capturetool = new StrumentoDiagnostico { Nome = strumento };
                strumenti.Add(capturetool);
            }
            capturetool.Campi =
            [
                new DefinizioneCampoStrumento
                {
                    Nome = "Licenza",
                    Ambito = TipoAmbitoCampo.PerTest,
                    ValoreDefault = "licenza-master",
                },
                new DefinizioneCampoStrumento
                {
                    Nome = "VersioneSw",
                    Ambito = TipoAmbitoCampo.PerRevisione,
                    ValoreDefault = "versione-master",
                },
                new DefinizioneCampoStrumento
                {
                    Nome = "Comando",
                    Ambito = TipoAmbitoCampo.PerOperazione,
                    ValoreDefault = "comando-master",
                },
            ];
            fixture.State.Master.Strumenti.Save(strumenti);
            fixture.State.OperatoreCorrente!.ValoriDefaultCampi[chiaveOperazione] = "comando-operatore";

            // Prepara una REV storica e una corrente, entrambe senza valori dei campi, prima di costruire l'editor.
            var test = fixture.State.TestCases.GetById(IdTestLayout)!;
            test.StrumentiUsati.Add(strumento);
            var corrente = test.Esecuzioni[^1];
            corrente.Iterazione = 2;
            corrente.Dettagli = new DettagliTestSnapshot { StrumentiUsati = [strumento] };
            var storico = new Esecuzione
            {
                Iterazione = 1,
                Data = new DateTime(2026, 8, 1),
                Operatore = "Mario",
                Matricola = "Matricola-STORICO",
                Dettagli = new DettagliTestSnapshot { StrumentiUsati = [strumento] },
                Condizioni =
                {
                    new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Storica" },
                },
            };
            test.Esecuzioni.Insert(0, storico);

            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            Window? finestra = null;
            try
            {
                var pagina = new TestCaseEditorView { DataContext = vm };
                finestra = MostraInSidebar(pagina, 1280, 672);

                var storicoVm = vm.Esecuzioni.Single(e => e.Iterazione == 1);
                var correnteVm = vm.RevisioneCorrente!;
                var operazioneStorica = storicoVm.Condizioni.Single();

                // Aprire il Test in sola lettura, anche partendo dalla REV corrente, non scrive default.
                Assert.Empty(vm.TestCase.ValoriStrumentiTest);
                Assert.Empty(correnteVm.ValoriStrumenti);
                Assert.All(correnteVm.Condizioni, c => Assert.Empty(c.ValoriStrumenti));

                // Entrando in modifica, i default restano disponibili sulla REV corrente e sul Test.
                LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                Assert.True(vm.InModifica);
                Assert.Equal("licenza-master", vm.TestCase.ValoriStrumentiTest[chiaveTest]);
                Assert.Equal("versione-master", correnteVm.ValoriStrumenti[chiaveRevisione]);
                Assert.Equal("comando-operatore", correnteVm.Condizioni[0].ValoriStrumenti[chiaveOperazione]);

                // La REV storica dichiara lo strumento nello snapshot, ma il solo passaggio alla sua visualizzazione
                // non deve importare default correnti/master nel dizionario storico.
                vm.RevisioneVisualizzata = storicoVm;
                Assert.True(vm.VisualizzaStorico);
                Assert.Empty(storicoVm.ValoriStrumenti);
                Assert.Empty(operazioneStorica.ValoriStrumenti);
                Assert.Equal("licenza-master", vm.TestCase.ValoriStrumentiTest[chiaveTest]);
                Assert.Equal("versione-master", correnteVm.ValoriStrumenti[chiaveRevisione]);
                Assert.Equal("comando-operatore", correnteVm.Condizioni[0].ValoriStrumenti[chiaveOperazione]);
                Assert.DoesNotContain(chiaveRevisione, storicoVm.ValoriStrumenti.Keys);
                Assert.DoesNotContain(chiaveOperazione, operazioneStorica.ValoriStrumenti.Keys);
            }
            finally
            {
                finestra?.Hide();
                vm.Dispose();
                if (vm.RilascioLockDopoDisposePerTest is { } rilascio)
                    LayoutHeadlessSessione.AttendiComando(rilascio, TimeSpan.FromSeconds(30));
            }
        });
    }

    private static Button TrovaPulsanteDefault(TestCaseEditorView pagina, CampoStrumentoValore campo, bool rimuovi)
    {
        var automationId = rimuovi ? campo.AutomationIdRimuoviDefault : campo.AutomationIdRicordaDefault;
        return pagina.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetAutomationId(b) == automationId && ReferenceEquals(b.DataContext, campo));
    }
}
