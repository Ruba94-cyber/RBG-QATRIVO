using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata ai filtri aggiuntivi della pagina Test (feedback utente 2026-08-20, punto 19):
/// Assegnatario, scorciatoie "assegnati a me"/"creati da me", filtro presenza riferimento Ticketing.</summary>
public sealed class TestCaseListFiltriTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestListFiltri_" + Guid.NewGuid().ToString("N"));

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

        state.TestCases.Save(new TestCase { Id = "TC-A", Componente = "BCM", Assegnatario = "Mario Rossi", CreatoDa = "Mario Rossi", TicketCollegati = new List<TicketRif> { new() { Riferimento = "TICKET-1" } } }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-B", Componente = "BCM", Assegnatario = "Altro Operatore", CreatoDa = "Mario Rossi" }, isNew: true);
        state.TestCases.Save(new TestCase { Id = "TC-C", Componente = "BCM", Assegnatario = "Altro Operatore", CreatoDa = "Altro Operatore" }, isNew: true);
        return state;
    }

    /// <summary>Correzione prestazioni 6.4.0 (pagina Test a freddo, 100k): Aggiorna() a contenuto invariato
    /// non deve riassegnare le liste dei valori dei filtri né notificarle (ComboBox/AutoCompleteBox legati
    /// ricaricherebbero ~1000 voci ciascuno); quando un valore cambia la lista va invece aggiornata e notificata.</summary>
    [Fact]
    public void Aggiorna_ValoriFiltriInvariati_NonRiassegnaNeNotificaLeListe_CambiatiSi()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });
        var ecuPrima = vm.EcuDisponibili;
        var assegnatariPrima = vm.AssegnatariDisponibili;
        var notificate = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName?.EndsWith("Disponibili", StringComparison.Ordinal) == true) notificate.Add(e.PropertyName); };

        vm.Aggiorna();

        Assert.Same(ecuPrima, vm.EcuDisponibili);
        Assert.Same(assegnatariPrima, vm.AssegnatariDisponibili);
        Assert.DoesNotContain(nameof(TestCaseListViewModel.EcuDisponibili), notificate);
        Assert.DoesNotContain(nameof(TestCaseListViewModel.AssegnatariDisponibili), notificate);

        state.TestCases.Save(new TestCase { Id = "TC-D", Componente = "Componente-NUOVA", Assegnatario = "Mario Rossi", CreatoDa = "Mario Rossi" }, isNew: true);
        notificate.Clear();
        vm.Aggiorna();

        Assert.Contains("Componente-NUOVA", vm.EcuDisponibili);
        Assert.NotSame(ecuPrima, vm.EcuDisponibili);
        Assert.Contains(nameof(TestCaseListViewModel.EcuDisponibili), notificate);
        Assert.Same(assegnatariPrima, vm.AssegnatariDisponibili);
    }

    [Fact]
    public void FiltroAssegnatario_MostraSoloIRigheDiQuellAssegnatario()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltroAssegnatario.Seleziona("Altro Operatore");

        Assert.Equal(2, vm.Righe.Count);
        Assert.All(vm.Righe, tc => Assert.Equal("Altro Operatore", tc.Assegnatario));
    }

    [Fact]
    public void FiltraAssegnatiAMe_Toggle_FiltraEPoiRimuoveIlFiltro()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltraAssegnatiAMeCommand.Execute(null);
        Assert.True(vm.FiltroAssegnatiAMeAttivo);
        Assert.Single(vm.Righe);
        Assert.Equal("TC-A", vm.Righe[0].Id);

        vm.FiltraAssegnatiAMeCommand.Execute(null);
        Assert.False(vm.FiltroAssegnatiAMeAttivo);
        Assert.Equal(3, vm.Righe.Count);
    }

    [Fact]
    public void FiltraCreatiDaMe_Toggle_FiltraPerCreatoDa()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltraCreatiDaMeCommand.Execute(null);

        Assert.True(vm.FiltroCreatiDaMeAttivo);
        Assert.Equal(2, vm.Righe.Count);
        Assert.All(vm.Righe, tc => Assert.Equal("Mario Rossi", tc.CreatoDa));
    }

    [Fact]
    public void FiltroHaPolarion_ConPolarion_MostraSoloTestConTicketCollegati()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion;

        Assert.Single(vm.Righe);
        Assert.Equal("TC-A", vm.Righe[0].Id);
    }

    [Fact]
    public void FiltroHaPolarion_SenzaPolarion_MostraSoloTestSenzaTicketCollegati()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltroHaPolarion = FiltroPolarioneOpzione.SenzaPolarion;

        Assert.Equal(2, vm.Righe.Count);
        Assert.DoesNotContain(vm.Righe, tc => tc.Id == "TC-A");
    }

    [Fact]
    public void FiltroHaPolarion_IncludeEstaPresenteSoloSullAnomalia()
    {
        var state = CreateState();
        var tcB = state.TestCases.GetAll().Single(t => t.Id == "TC-B");
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-0001",
            TestCaseId = "TC-B",
            TestUid = tcB.Uid,
            DataApertura = DateTime.Now,
            CollegamentiPolarion = new() { new CollegamentoPolarion { Codice = "TICKET-99" } }
        });
        var vm = new TestCaseListViewModel(state, _ => { });

        vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion;

        Assert.Equal(new[] { "TC-A", "TC-B" }, vm.Righe.Select(t => t.Id).OrderBy(x => x));
    }

    [Fact]
    public void ReimpostaFiltri_AzzeraAncheINuoviFiltri()
    {
        var state = CreateState();
        var vm = new TestCaseListViewModel(state, _ => { });
        vm.FiltroAssegnatario.Seleziona("Altro Operatore");
        vm.FiltroHaPolarion = FiltroPolarioneOpzione.ConPolarion;
        vm.FiltraCreatiDaMeCommand.Execute(null);
        Assert.True(vm.HaFiltriAttivi);

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.False(vm.HaFiltriAttivi);
        Assert.False(vm.FiltroAssegnatario.HaSelezione);
        Assert.False(vm.FiltroCreatoDa.HaSelezione);
        Assert.Equal(FiltroPolarioneOpzione.Tutti, vm.FiltroHaPolarion);
        Assert.Equal(3, vm.Righe.Count);
    }
}
