using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using TestLoom.App.Views;
using static TestLoom.App.Tests.LayoutHeadlessMisure;

namespace TestLoom.App.Tests;

/// <summary>Campi strumento in visualizzazione sulla vista reale: il riquadro di ogni operazione e' aperto solo se contiene almeno un
/// campo compilato, dentro si vedono solo i campi compilati e non sono modificabili, l'intestazione resta cliccabile. In modifica tutto
/// chiuso di default e campi modificabili. Vale anche sfogliando una revisione storica.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class CampiStrumentoLetturaHeadlessTests
{
    private const string Strumento = "CaptureTool";

    private static void ConfiguraStrumento(FixtureEditor fixture)
    {
        var strumenti = fixture.State.Master.Strumenti.Load();
        var s = strumenti.SingleOrDefault(x => x.Nome == Strumento);
        if (s == null) { s = new StrumentoDiagnostico { Nome = Strumento }; strumenti.Add(s); }
        s.Campi =
        [
            new DefinizioneCampoStrumento { Nome = "Compilato", Ambito = TipoAmbitoCampo.PerOperazione, Ordine = 1 },
            new DefinizioneCampoStrumento { Nome = "Vuoto", Ambito = TipoAmbitoCampo.PerOperazione, Ordine = 2 },
            new DefinizioneCampoStrumento { Nome = "DatoGruppo", Ambito = TipoAmbitoCampo.PerOperazione, Gruppo = "Gruppo prova", Ordine = 3 },
        ];
        fixture.State.Master.Strumenti.Save(strumenti);
    }

    private static Expander Riquadro(TestCaseEditorView pagina, Guid uid) =>
        pagina.GetVisualDescendants().OfType<Expander>()
            .Single(e => AutomationProperties.GetAutomationId(e) == $"Editor_Condizione_{uid}_Parametri_Expander");

    private static List<TextBox> Campo(TestCaseEditorView pagina, string nome) =>
        pagina.GetVisualDescendants().OfType<TextBox>()
            .Where(t => AutomationProperties.GetAutomationId(t) == $"Editor_Campo_{nome}").ToList();

    [Fact]
    public void Lettura_RiquadroApertoSoloSeCompilato_CampiVuotiNascosti_CampiNonModificabili_IntestazioneCliccabile()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            ConfiguraStrumento(fixture);
            var test = fixture.State.TestCases.GetById(IdTestLayout)!;
            test.StrumentiUsati.Add(Strumento);
            test.Esecuzioni[^1].Dettagli = new DettagliTestSnapshot { StrumentiUsati = [Strumento] };
            test.Esecuzioni[^1].Condizioni.Single(c => c.Uid == fixture.Prima).ValoriStrumenti[$"{Strumento}::Compilato"] = "valore-1";

            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            Window? finestra = null;
            try
            {
                var pagina = new TestCaseEditorView { DataContext = vm };
                finestra = MostraInSidebar(pagina, 1280, 900);
                vm.StrumentiOptions.Single(s => s.Valore == Strumento).Selezionato = true;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.False(vm.InModifica);

                var conValori = Riquadro(pagina, fixture.Prima);
                var senzaValori = Riquadro(pagina, fixture.Seconda);
                Assert.True(conValori.IsExpanded);
                Assert.False(senzaValori.IsExpanded);
                // L'intestazione e' interattiva anche in sola lettura (il contenitore non e' piu' disabilitato).
                Assert.True(conValori.IsEffectivelyEnabled);
                Assert.True(senzaValori.IsEffectivelyEnabled);

                // Nel riquadro aperto: solo il campo compilato, non modificabile; il vuoto non e' nell'albero.
                var compilato = Assert.Single(Campo(pagina, "Compilato"));
                Assert.Equal("valore-1", compilato.Text);
                Assert.False(compilato.IsEffectivelyEnabled);
                Assert.Empty(Campo(pagina, "Vuoto"));

                // L'utente apre a mano il riquadro vuoto: nessun campo da mostrare, solo il messaggio.
                senzaValori.IsExpanded = true;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(vm.RevisioneCorrente!.Condizioni.Single(c => c.Uid == fixture.Seconda).ParametriAperti); // stato scritto dall'Expander
                Assert.Single(Campo(pagina, "Compilato"));
                Assert.Contains(pagina.GetVisualDescendants().OfType<TextBlock>(),
                    t => AutomationProperties.GetAutomationId(t) == $"Editor_Condizione_{fixture.Seconda}_Parametri_NessunCompilato_Text" && t.IsEffectivelyVisible);

                // L'utente chiude il riquadro compilato.
                conValori.IsExpanded = false;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.Empty(Campo(pagina, "Compilato"));

                // "Mostra anche i campi vuoti": tutto aperto, tutti i campi visibili ma sempre non modificabili.
                var toggle = Trova<ToggleButton>(pagina, "Editor_ParametriStrumento_Lettura_Toggle");
                toggle.IsChecked = true;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(conValori.IsExpanded);
                Assert.True(senzaValori.IsExpanded);
                Assert.Equal(2, Campo(pagina, "Vuoto").Count);
                Assert.All(Campo(pagina, "Vuoto").Concat(Campo(pagina, "Compilato")), t => Assert.False(t.IsEffectivelyEnabled));
                toggle.IsChecked = false;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(conValori.IsExpanded);
                Assert.False(senzaValori.IsExpanded);
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
    public void Gruppo_ApertoSoloSeCompilato_InLettura_ESfogliandoUnaRevisioneStorica()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            ConfiguraStrumento(fixture);
            var test = fixture.State.TestCases.GetById(IdTestLayout)!;
            test.StrumentiUsati.Add(Strumento);
            var corrente = test.Esecuzioni[^1];
            corrente.Iterazione = 2;
            corrente.Dettagli = new DettagliTestSnapshot { StrumentiUsati = [Strumento] };
            // REV corrente: nessun valore. REV storica: la sola operazione ha un campo di gruppo compilato.
            var opStorica = new Condizione { Componente = new EcuSelection(new[] { "BCM" }), Funzionalita = "Storica", Esito = Esito.Conforme };
            opStorica.ValoriStrumenti[$"{Strumento}::DatoGruppo"] = "dato-storico";
            var storica = new Esecuzione
            {
                Iterazione = 1, Data = new DateTime(2026, 8, 1), Operatore = "Mario", Matricola = "Matricola-S",
                Dettagli = new DettagliTestSnapshot { StrumentiUsati = [Strumento] },
                Condizioni = { opStorica },
            };
            test.Esecuzioni.Insert(0, storica);

            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            Window? finestra = null;
            try
            {
                var pagina = new TestCaseEditorView { DataContext = vm };
                finestra = MostraInSidebar(pagina, 1280, 900);
                LayoutHeadlessSessione.Assesta(finestra);

                // REV corrente: nessun riquadro aperto.
                Assert.All(vm.RevisioneCorrente!.Condizioni, c => Assert.False(c.ParametriAperti));
                Assert.False(Riquadro(pagina, fixture.Prima).IsExpanded);

                // Sfoglio la REV storica: il suo riquadro e il suo gruppo si aprono (campo compilato), in sola lettura.
                var storicaVm = vm.Esecuzioni.Single(e => e.Iterazione == 1);
                vm.RevisioneVisualizzata = storicaVm;
                LayoutHeadlessSessione.Assesta(finestra);
                var operazione = storicaVm.Condizioni.Single();
                Assert.True(vm.VisualizzaStorico);
                Assert.True(operazione.ParametriAperti);
                var gruppo = Assert.Single(operazione.GruppiVisual!);
                Assert.True(gruppo.Aperto);
                Assert.True(Riquadro(pagina, operazione.Uid).IsExpanded);
                var dato = Assert.Single(Campo(pagina, "DatoGruppo"));
                Assert.Equal("dato-storico", dato.Text);
                Assert.False(dato.IsEffectivelyEnabled);
                Assert.Empty(Campo(pagina, "Compilato"));

                // Torno alla REV corrente: di nuovo tutto chiuso.
                vm.RevisioneVisualizzata = vm.RevisioneCorrente;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.False(Riquadro(pagina, fixture.Prima).IsExpanded);
                Assert.Empty(Campo(pagina, "DatoGruppo"));
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
    public void Modifica_TuttoChiusoDiDefaultAncheConValori_CampiModificabili_RitornoInLetturaRicalcola()
    {
        LayoutHeadlessSessione.Esegui(() =>
        {
            LocalizationService.Instance.Lingua = "it";
            using var fixture = CreaFixtureEditor();
            ConfiguraStrumento(fixture);
            var test = fixture.State.TestCases.GetById(IdTestLayout)!;
            test.StrumentiUsati.Add(Strumento);
            test.Esecuzioni[^1].Dettagli = new DettagliTestSnapshot { StrumentiUsati = [Strumento] };
            test.Esecuzioni[^1].Condizioni.Single(c => c.Uid == fixture.Prima).ValoriStrumenti[$"{Strumento}::Compilato"] = "valore-1";

            var vm = new TestCaseEditorViewModel(fixture.State, IdTestLayout, () => { }, _ => { }) { ModoEditorRapido = false };
            Window? finestra = null;
            try
            {
                var pagina = new TestCaseEditorView { DataContext = vm };
                finestra = MostraInSidebar(pagina, 1280, 900);
                vm.StrumentiOptions.Single(s => s.Valore == Strumento).Selezionato = true;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(Riquadro(pagina, fixture.Prima).IsExpanded); // lettura: aperto

                LayoutHeadlessSessione.AttendiComando(vm.IniziaModificaCommand.ExecuteAsync(null), TimeSpan.FromSeconds(30));
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(vm.InModifica);
                var riquadro = Riquadro(pagina, fixture.Prima);
                Assert.False(riquadro.IsExpanded); // modifica: chiuso di default
                riquadro.IsExpanded = true; // apribile a mano
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.NotEmpty(Campo(pagina, "Vuoto"));
                Assert.NotEmpty(Campo(pagina, "Compilato"));
                Assert.All(Campo(pagina, "Vuoto").Concat(Campo(pagina, "Compilato")), t => Assert.True(t.IsEffectivelyEnabled));

                vm.InModifica = false;
                LayoutHeadlessSessione.Assesta(finestra);
                Assert.True(Riquadro(pagina, fixture.Prima).IsExpanded);
                Assert.False(Riquadro(pagina, fixture.Seconda).IsExpanded);
                Assert.Empty(Campo(pagina, "Vuoto"));
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
}
