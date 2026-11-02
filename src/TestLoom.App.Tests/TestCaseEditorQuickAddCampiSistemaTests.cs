using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Bug/richiesta segnalata dall'utente (2026-09-18): "possibilita' di aggiungere i campi di sistema tipo
/// componente/veicoli ecc anche direttamente dal test altrimenti diventa macchioso, la possibilita' viene data sia
/// ad utente master che standard". Prima di questa modifica solo AggiungiStrumentoRapido esisteva, gated a
/// Master (PuoGestireStrumenti): un componente o un Veicolo mancante dal catalogo richiedeva di uscire dal Test,
/// andare in Impostazioni e tornare indietro.
/// </summary>
[Collection("Localization")]
public sealed class TestCaseEditorQuickAddCampiSistemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_QuickAddCampiSistema_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppState CreaStato(RuoloOperatore ruolo)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var nome = "Operatore " + ruolo;
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = nome, Ruolo = ruolo, PinHash = PinService.Hash("1234") }) });
        Assert.True(state.ImpostaOperatore(nome, "1234"));
        return state;
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    public void AggiungiEcuRapido_CreaLEcuNelMasterELaAggiungeAlTest_PerMasterEStandard(RuoloOperatore ruolo)
    {
        var state = CreaStato(ruolo);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.True(vm.PuoAggiungereCampiSistemaRapidi);

        vm.NuovoEcuNome = "Componente-NUOVA-TEST";
        vm.AggiungiEcuRapidoCommand.Execute(null);

        Assert.Contains(state.Master.Componente.Load(), e => e.Nome == "Componente-NUOVA-TEST");
        Assert.Contains("Componente-NUOVA-TEST", (IEnumerable<string>)vm.TestCase.Componente);
        Assert.Contains("Componente-NUOVA-TEST", vm.EcuVisual);
        Assert.Equal("", vm.NuovoEcuNome);
    }

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    public void AggiungiVeicoloRapido_CreaIlVeicoloNelMasterELoAggiungeAlTest_PerMasterEStandard(RuoloOperatore ruolo)
    {
        var state = CreaStato(ruolo);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.NuovoVeicoloNome = "Veicolo-Nuovo-Test";
        vm.AggiungiVeicoloRapidoCommand.Execute(null);

        Assert.Contains(state.Master.Veicoli.Load(), v => v.Nome == "Veicolo-Nuovo-Test");
        Assert.Contains("Veicolo-Nuovo-Test", vm.TestCase.Veicoli);
        Assert.Equal("", vm.NuovoVeicoloNome);
    }

    private TestCaseEditorViewModel NuovoVm(AppState state) =>
        new(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

    [Theory]
    [InlineData(RuoloOperatore.Master)]
    [InlineData(RuoloOperatore.Standard)]
    public void AggiungiFunzionalitaRapida_SenzaEcu_CreaFunzionalitaGenericaELaAggiungeAlTest(RuoloOperatore ruolo)
    {
        var state = CreaStato(ruolo);
        var vm = NuovoVm(state);

        vm.NuovaFunzionalitaCatalogoNome = "  Funz-Nuova  ";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);

        var f = Assert.Single(state.Master.Funzionalita.Load(), x => x.Nome == "Funz-Nuova");
        Assert.True(f.TutteLeEcu);
        Assert.Empty(f.EcuAssociate);
        Assert.Contains("Funz-Nuova", (IEnumerable<string>)vm.TestCase.Funzionalita);
        Assert.Contains("Funz-Nuova", vm.FunzionalitaDisponibili);
        Assert.Equal("", vm.NuovaFunzionalitaCatalogoNome);
    }

    [Fact]
    public void AggiungiFunzionalitaRapida_ConEcuNelTest_CreaFunzionalitaAssociataAQuelleEcu()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        var vm = NuovoVm(state);
        vm.NuovoEcuNome = "Componente-X";
        vm.AggiungiEcuRapidoCommand.Execute(null);

        vm.NuovaFunzionalitaCatalogoNome = "Funz-Componente";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);

        var f = Assert.Single(state.Master.Funzionalita.Load(), x => x.Nome == "Funz-Componente");
        Assert.False(f.TutteLeEcu);
        Assert.Equal(new[] { "Componente-X" }, f.EcuAssociate);
        Assert.Contains("Funz-Componente", (IEnumerable<string>)vm.TestCase.Funzionalita);
    }

    [Fact]
    public void AggiungiFunzionalitaRapida_Viewer_NonCreaNiente()
    {
        var state = CreaStato(RuoloOperatore.Viewer);
        var vm = NuovoVm(state);
        vm.NuovaFunzionalitaCatalogoNome = "Funz-Viewer";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);
        Assert.DoesNotContain(state.Master.Funzionalita.Load(), x => x.Nome == "Funz-Viewer");
    }

    [Fact]
    public void AggiungiFunzionalitaRapida_NomeEsistenteCaseDiverso_NonDuplicaEUsaIlNomeDelCatalogo()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Diagnosi CAN" } });
        var vm = NuovoVm(state);

        vm.NuovaFunzionalitaCatalogoNome = " diagnosi can ";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);

        Assert.Single(state.Master.Funzionalita.Load());
        Assert.Contains("Diagnosi CAN", (IEnumerable<string>)vm.TestCase.Funzionalita);
        Assert.DoesNotContain("diagnosi can", (IEnumerable<string>)vm.TestCase.Funzionalita);
    }

    [Fact]
    public void AggiungiFunzionalitaRapida_EsistenteGenerica_RestaGenerica()
    {
        var state = CreaStato(RuoloOperatore.Master);
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Gen", TutteLeEcu = true } });
        var vm = NuovoVm(state);
        vm.NuovoEcuNome = "Componente-X";
        vm.AggiungiEcuRapidoCommand.Execute(null);

        vm.NuovaFunzionalitaCatalogoNome = "gen";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);

        var f = Assert.Single(state.Master.Funzionalita.Load());
        Assert.True(f.TutteLeEcu);
        Assert.Empty(f.EcuAssociate);
    }

    [Theory]
    [InlineData(RuoloOperatore.Master, true)]
    [InlineData(RuoloOperatore.Standard, false)]
    public void AggiungiFunzionalitaRapida_EsistenteNonGenerica_SoloMasterEstendeLeEcu(RuoloOperatore ruolo, bool estesa)
    {
        var state = CreaStato(ruolo);
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Spec", TutteLeEcu = false, EcuAssociate = new() { "Componente-A" } } });
        var vm = NuovoVm(state);
        vm.NuovoEcuNome = "Componente-X";
        vm.AggiungiEcuRapidoCommand.Execute(null);

        vm.NuovaFunzionalitaCatalogoNome = "Spec";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);

        var f = Assert.Single(state.Master.Funzionalita.Load());
        Assert.Equal(estesa, f.EcuAssociate.Contains("Componente-X"));
        Assert.False(f.TutteLeEcu);
        Assert.Contains("Spec", (IEnumerable<string>)vm.TestCase.Funzionalita);
    }

    [Fact]
    public void AggiungiFunzionalitaRapida_Archiviata_NonCreaNeRipristinaEAvvisa()
    {
        var state = CreaStato(RuoloOperatore.Master);
        state.Master.Funzionalita.Save(new List<Funzionalita> { new() { Nome = "Vecchia", Archiviata = true } });
        var vm = NuovoVm(state);

        vm.NuovaFunzionalitaCatalogoNome = "vecchia";
        vm.AggiungiFunzionalitaRapidaCommand.Execute(null);

        var f = Assert.Single(state.Master.Funzionalita.Load());
        Assert.True(f.Archiviata);
        Assert.Empty(vm.TestCase.Funzionalita);
        Assert.True(vm.HaAvvisoFunzionalitaRapida);
    }

    [Fact]
    public void TitoloDaOperazione_UsaFunzionalitaEcuEProblema_MaiLaNota()
    {
        var t = NonConformitaRulesService.TitoloDaOperazione("Diagnosi CAN", "BCM", "Nessuna risposta dal bus\nseconda riga", "Operazione 1");
        Assert.Equal("Diagnosi CAN (BCM): Nessuna risposta dal bus", t);
        Assert.Equal("Operazione 2", NonConformitaRulesService.TitoloDaOperazione("", null, null, "Operazione 2"));
        var lungo = NonConformitaRulesService.TitoloDaOperazione("F", null, new string('x', 50) + " " + new string('y', 80), "Op");
        Assert.True(lungo.Length < 100);
        Assert.EndsWith("…", lungo);
    }

    [Fact]
    public void CampoStrumentoValore_IndicatoriDefault_PersonaleMasterERimozione()
    {
        var valori = new Dictionary<string, string>();
        var def = new DefinizioneCampoStrumento { Nome = "Campo", Tipo = TipoCampoStrumento.Testo, ValoreDefault = "M" };
        var campo = new CampoStrumentoValore(valori, "Strum", def, "P");
        Assert.True(campo.PrecompilatoDaDefault);
        Assert.True(campo.EDefaultPersonale);
        Assert.False(campo.EDefaultMaster);
        campo.Valore = "M";
        Assert.True(campo.EDefaultMaster);
        Assert.False(campo.EDefaultPersonale);
        campo.ImpostaDefaultPersonale("M");
        Assert.True(campo.EDefaultPersonale);
        campo.ImpostaDefaultPersonale(null);
        Assert.False(campo.HaDefaultPersonale);
        Assert.True(campo.EDefaultMaster);
    }

    [Fact]
    public void AggiungiEcuRapido_Viewer_NonCreaNienteENonLanciaEccezioni()
    {
        var state = CreaStato(RuoloOperatore.Viewer);
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.False(vm.PuoAggiungereCampiSistemaRapidi);

        vm.NuovoEcuNome = "Componente-NON-DEVE-ESISTERE";
        vm.AggiungiEcuRapidoCommand.Execute(null);

        Assert.DoesNotContain(state.Master.Componente.Load(), e => e.Nome == "Componente-NON-DEVE-ESISTERE");
    }

    [Fact]
    public void AggiungiEcuRapido_NomeGiaEsistente_NonCreaUnDuplicatoMaLaAggiungeComunqueAlTest()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        vm.NuovoEcuNome = "bcm"; // stesso nome, case diverso
        vm.AggiungiEcuRapidoCommand.Execute(null);

        Assert.Single(state.Master.Componente.Load(), e => e.Nome.Equals("BCM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("bcm", (IEnumerable<string>)vm.TestCase.Componente);
    }
}
