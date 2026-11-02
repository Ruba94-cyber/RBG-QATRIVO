using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 22 (feedback utente 2026-08-20): il meccanismo di suggerimento dalla
/// cronologia (già esistente ma nascosto, effetto collaterale silenzioso del primo "+ Aggiungi condizione")
/// deve essere più visibile — ora calcolato proattivamente appena Funzionalità/Tipo test lo rendono possibile,
/// con un banner più prominente e un'azione esplicita "Usa questo".</summary>
public sealed class SuggerimentoCronologiaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SuggerimentoCronologia_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Mario Rossi", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        return state;
    }

    private TestCase CreaTestConCronologia(AppState state, Guid templateUid, string funzionalita)
    {
        var tc = new TestCase
        {
            Id = "TC-STORICO-01", Componente = "BCM", TestTemplateUid = templateUid,
            Funzionalita = new List<string> { funzionalita }, UltimaModifica = DateTime.Now,
            Esecuzioni = new List<Esecuzione>
            {
                new() { Iterazione = 1, Condizioni = new List<Condizione> { new() { Esito = Esito.Conforme, ValoriStrumenti = new Dictionary<string, string> { ["ValidationTool::Canale"] = "CAN1" } } } }
            }
        };
        state.TestCases.Save(tc, isNew: true);
        return tc;
    }

    // Cuore del punto 22: il banner compare PRIMA di cliccare "+ Aggiungi condizione", appena si sceglie una
    // Funzionalità che trova un riscontro nella cronologia — non più un effetto collaterale scoperto per caso.
    [Fact]
    public void AggiungiFunzionalita_ConRiscontroInCronologia_MostraSubitoIlBanner()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        CreaTestConCronologia(state, template.Uid, "Diagnosi CAN");

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);

        Assert.False(vm.HaSuggerimentoCronologia); // nessuna Funzionalità ancora scelta

        vm.NuovaFunzionalita = "Diagnosi CAN";
        vm.AggiungiFunzionalitaCommand.Execute(null);

        Assert.True(vm.HaSuggerimentoCronologia);
        Assert.Contains("TC-STORICO-01", vm.SuggerimentoCronologiaTesto);
        Assert.Equal("TC-STORICO-01", vm.SuggerimentoCronologiaTestId);
    }

    [Fact]
    public void RimuoviFunzionalita_UnicoRiscontroRimosso_NascondeIlBanner()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        CreaTestConCronologia(state, template.Uid, "Diagnosi CAN");

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.NuovaFunzionalita = "Diagnosi CAN";
        vm.AggiungiFunzionalitaCommand.Execute(null);
        Assert.True(vm.HaSuggerimentoCronologia);

        vm.RimuoviFunzionalitaCommand.Execute("Diagnosi CAN");

        Assert.False(vm.HaSuggerimentoCronologia);
        Assert.Null(vm.SuggerimentoCronologiaTestId);
    }

    [Fact]
    public void UsaSuggerimentoCronologia_AggiungeCondizionePrecompilata()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        CreaTestConCronologia(state, template.Uid, "Diagnosi CAN");

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.NuovaFunzionalita = "Diagnosi CAN";
        vm.AggiungiFunzionalitaCommand.Execute(null);

        vm.UsaSuggerimentoCronologiaCommand.Execute(null);

        var condizione = Assert.Single(vm.RevisioneCorrente!.Condizioni);
        Assert.Equal("CAN1", condizione.ValoriStrumenti["ValidationTool::Canale"]);
        Assert.Equal(Esito.DaEseguire, condizione.Esito); // l'esito non si copia mai, va dato di proposito
        Assert.Null(vm.SuggerimentoCronologiaTestId);
    }

    [Fact]
    public void SenzaFunzionalitaCorrispondenti_NessunBanner()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        CreaTestConCronologia(state, template.Uid, "Diagnosi CAN");

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.NuovaFunzionalita = "Funzionalità mai vista";
        vm.AggiungiFunzionalitaCommand.Execute(null);

        Assert.False(vm.HaSuggerimentoCronologia);
    }

    // Con dati tecnici reali già compilati in QUESTO test, il banner cronologia non ha senso: "+ Aggiungi
    // condizione" copia comunque da quella, un punto di partenza più pertinente. Il solo Esito impostato (senza
    // campi tecnici) non basta a considerare la condizione "già compilata" — il suggerimento resta utile.
    [Fact]
    public void ConDatiTecniciGiaCompilati_BannerCronologiaNonMostrato()
    {
        var state = CreateState();
        var template = state.Master.TestTemplate.Load()[0];
        CreaTestConCronologia(state, template.Uid, "Diagnosi CAN");

        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        vm.SceglieTemplateCommand.Execute(template);
        vm.CondizioniVisual[0].Esito = Esito.Conforme;
        vm.CondizioniVisual[0].ValoriStrumenti["ValidationTool::Canale"] = "CAN2"; // dato tecnico reale già inserito a mano

        vm.NuovaFunzionalita = "Diagnosi CAN";
        vm.AggiungiFunzionalitaCommand.Execute(null);

        Assert.False(vm.HaSuggerimentoCronologia);
    }
}
