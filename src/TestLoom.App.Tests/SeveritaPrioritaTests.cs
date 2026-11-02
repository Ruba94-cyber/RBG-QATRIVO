using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 18 (feedback utente 2026-08-20): scale Severità/Priorità a 6 valori
/// (0-5), Severità spostata anche sul TestCase (ereditata dalla NC quando se ne apre una), Priorità interna
/// assegnabile solo da un Master.</summary>
public sealed class SeveritaPrioritaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SeveritaPriorita_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState(RuoloOperatore ruolo = RuoloOperatore.Master)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var nome = "Op " + ruolo;
        var op = new Operatore { Nome = nome, Ruolo = ruolo, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(op); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        if (state.Master.TestTemplate.Load().Count == 0)
        {
            state.Master.TestTemplate.Save(new List<TestTemplate>
            {
                new TestTemplate { Uid = Guid.NewGuid(), Nome = "Template Default" }
            });
        }
        state.ImpostaOperatore(nome, "1234");
        return state;
    }

    [Fact]
    public void SeveritaAnomalia_HaSeiValori_Da0A5()
    {
        var valori = Enum.GetValues<SeveritaAnomalia>().Select(v => (int)v).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, valori);
        Assert.Equal(0, (int)SeveritaAnomalia.NA);
        Assert.Equal(5, (int)SeveritaAnomalia.Highest);
    }

    [Fact]
    public void PrioritaOperativa_HaSeiValori_Da0A5_DefaultNormal()
    {
        var valori = Enum.GetValues<PrioritaOperativa>().Select(v => (int)v).OrderBy(v => v).ToList();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, valori);
        Assert.Equal(0, (int)PrioritaOperativa.Enhancement);
        Assert.Equal(5, (int)PrioritaOperativa.Critical);
        Assert.Equal(PrioritaOperativa.Normal, new NonConformita().Priorita);
        Assert.Equal(PrioritaOperativa.Normal, new TestCase().Priorita);
    }

    [Fact]
    public void TestCaseEditorViewModel_SeveritaSelezionata_RiflettePropostaSulTestCase()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);

        vm.SeveritaSelezionata = SeveritaAnomalia.High;

        Assert.Equal(SeveritaAnomalia.High, vm.TestCase.Severita);
        Assert.True(vm.ModificheNonSalvate);
    }

    [Fact]
    public async Task TestCaseEditorViewModel_Severita_PersisteAlSalvataggioERicaricamento()
    {
        var state = CreateState();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.AssegnatarioSelezionato = "Op Test";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.SeveritaSelezionata = SeveritaAnomalia.Medium;
        vm.PrioritaSelezionata = PrioritaOperativa.Major;

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.ErroriValidazione);

        var ricaricato = state.TestCases.GetById(vm.TestCase.Id);
        Assert.Equal(SeveritaAnomalia.Medium, ricaricato!.Severita);
        Assert.Equal(PrioritaOperativa.Major, ricaricato.Priorita);

        var vm2 = new TestCaseEditorViewModel(state, testCaseId: ricaricato.Id, tornaAllaLista: () => { }, navigaATest: _ => { });
        Assert.Equal(SeveritaAnomalia.Medium, vm2.SeveritaSelezionata);
        Assert.Equal(PrioritaOperativa.Major, vm2.PrioritaSelezionata);
    }

    [Fact]
    public void NonConformitaViewModel_EMaster_RiflettelIlRuoloAttivo()
    {
        var stateMaster = CreateState(RuoloOperatore.Master);
        var vmMaster = new NonConformitaViewModel(stateMaster, _ => { });
        Assert.True(vmMaster.EMaster);

        var stateStandard = CreateState(RuoloOperatore.Standard);
        var vmStandard = new NonConformitaViewModel(stateStandard, _ => { });
        Assert.False(vmStandard.EMaster);
    }

    [Fact]
    public void ClassificaSegnalazione_EreditaSeveritaEPrioritaDalTestOrigine()
    {
        var state = CreateState();
        var tc = new TestCase
        {
            Id = "TC-EREDITA-01", Componente = "BCM", Severita = SeveritaAnomalia.High, Priorita = PrioritaOperativa.Critical,
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.NonConforme } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);
        var condizioneUid = tc.Esecuzioni[0].Condizioni[0].Uid;
        state.Segnalazioni.Save(new SegnalazioneDaClassificare
        {
            TestId = tc.Id, CondizioneUid = condizioneUid, Stato = StatoSegnalazione.DaClassificare
        });

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = vm.Segnalazioni.First(s => s.Segnalazione.TestId == tc.Id);

        // Punto 18: la Severità è già proposta (ereditata dal test), non scelta da zero.
        Assert.Equal(SeveritaAnomalia.High, riga.Severita);

        vm.ClassificaSegnalazioneCommand.Execute(riga);

        var nc = state.NonConformita.GetAll().Single(n => n.TestCaseId == tc.Id);
        Assert.Equal(SeveritaAnomalia.High, nc.Severita);
        Assert.Equal(PrioritaOperativa.Critical, nc.Priorita);
        // Già confermata, non da confermare (comportamento preesistente confermato dal punto 18).
        Assert.Equal(StatoAnomalia.Confermata, nc.StatoWorkflow);
    }

    [Fact]
    public void ClassificaSegnalazione_TestSenzaPrioritaImpostata_DefaultNormal()
    {
        var state = CreateState();
        var tc = new TestCase
        {
            Id = "TC-DEFAULT-01", Componente = "BCM",
            Esecuzioni = new List<Esecuzione> { new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.NonConforme } } } }
        };
        state.TestCases.Save(tc, isNew: true);
        var condizioneUid = tc.Esecuzioni[0].Condizioni[0].Uid;
        state.Segnalazioni.Save(new SegnalazioneDaClassificare { TestId = tc.Id, CondizioneUid = condizioneUid, Stato = StatoSegnalazione.DaClassificare });

        var vm = new NonConformitaViewModel(state, _ => { });
        var riga = vm.Segnalazioni.First(s => s.Segnalazione.TestId == tc.Id);
        riga.Severita = SeveritaAnomalia.Low;

        vm.ClassificaSegnalazioneCommand.Execute(riga);

        var nc = state.NonConformita.GetAll().Single(n => n.TestCaseId == tc.Id);
        Assert.Equal(PrioritaOperativa.Normal, nc.Priorita);
    }
}
