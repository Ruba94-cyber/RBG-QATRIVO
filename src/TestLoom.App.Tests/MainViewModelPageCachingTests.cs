using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using static TestLoom.App.Tests.NavigazioneTestHelper;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata per l'audit prestazioni 2026-08-25: le pagine di primo livello (tranne
/// Impostazioni) restano vive tra un cambio scheda e l'altro invece di essere ricreate, e vengono riallineate
/// ai dati correnti tramite IPaginaAggiornabile.Aggiorna() invece che con una ricostruzione completa.</summary>
[Collection("Localization")]
public sealed class MainViewModelPageCachingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MainViewModelCaching_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateMasterState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    [Fact]
    public async Task CambioScheda_RivisitaTest_RiusaLaStessaIstanzaInveceDiRicrearla()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);

        await NavigaAsync(main, "test");
        var primaVisita = main.CurrentPage;
        Assert.IsType<TestCaseListViewModel>(primaVisita);

        await NavigaAsync(main, "home");
        await NavigaAsync(main, "test");
        var secondaVisita = main.CurrentPage;

        Assert.Same(primaVisita, secondaVisita);
    }

    [Fact]
    public async Task CambioScheda_RivisitaTest_RifletteUnTestAppenaCreatoAltrove()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);

        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        Assert.Empty(lista.Righe);

        // Un test viene creato "altrove" (es. dall'editor) e i dati vengono ricaricati, come avviene
        // realmente dopo un salvataggio.
        state.TestCases.Save(new TestCase { Id = "TC-00001", Componente = "Componente-000", Veicolo = "Veicolo-000" }, isNew: true);
        state.RicaricaTutto();

        await NavigaAsync(main, "home");
        await NavigaAsync(main, "test");

        Assert.Same(lista, main.CurrentPage);
        Assert.Single(lista.Righe);
    }

    [Fact]
    public async Task CambioScheda_Impostazioni_NonVieneMaiRiusata()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);

        await NavigaAsync(main, "impostazioni");
        var prima = main.CurrentPage;
        Assert.IsType<ImpostazioniViewModel>(prima);

        await NavigaAsync(main, "home");
        await NavigaAsync(main, "impostazioni");
        var seconda = main.CurrentPage;

        Assert.NotSame(prima, seconda);
    }

    [Fact]
    public async Task Logout_SvuotaLaCacheDellePagine_ProssimoOperatoreNonEreditaLoStatoDelPrecedente()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);

        await NavigaAsync(main, "test");
        var lista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        lista.FiltroTesto = "qualcosa";

        main.LogoutCommand.Execute(null);
        state.ImpostaOperatore("Master Test", "1234");
        await NavigaAsync(main, "test");

        var nuovaLista = Assert.IsType<TestCaseListViewModel>(main.CurrentPage);
        Assert.NotSame(lista, nuovaLista);
        Assert.Equal("", nuovaLista.FiltroTesto);
    }

    [Fact]
    public async Task CambioScheda_RivisitaReport_RiusaLIstanzaERifletteUnNuovoTest()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);

        await NavigaAsync(main, "report");
        var report = Assert.IsType<ReportViewModel>(main.CurrentPage);
        await report.AttendiAggiornamentoAsync();
        Assert.Empty(report.Anteprima);

        state.TestCases.Save(new TestCase { Id = "TC-00001", Componente = "Componente-000", Veicolo = "Veicolo-000" }, isNew: true);
        state.RicaricaTutto();

        await NavigaAsync(main, "home");
        await NavigaAsync(main, "report");
        await report.AttendiAggiornamentoAsync();

        Assert.Same(report, main.CurrentPage);
        Assert.Single(report.Anteprima);
        // Gli assi di default restano quelli impostati una sola volta alla prima apertura, non azzerati dalla rivisita.
        Assert.Equal(DimensioniMatrice.Componente, report.AsseXMatrice?.Id);
        Assert.Equal(DimensioniMatrice.CategoriaFunzionalita, report.AsseYMatrice?.Id);
    }

    // Feedback utente 2026-08-26: "quando torno su audit invece di tenermi selezionato il sottoveicolo che
    // avevo precedentemente mi torna sempre in cima alla lista" — comportamento della 5.3.1 (pagine sempre
    // ricreate). Con la cache pagine + AuditViewModel.Aggiorna() la selezione deve restare quella scelta.
    [Fact]
    public async Task CambioScheda_RivisitaAudit_MantieneIlSottoveicoloSelezionato()
    {
        var state = CreateMasterState();
        state.Master.Veicoli.Save([
            new() { Nome = "Veicolo-AAA", GammaNome = "Gamma-A" },
            new() { Nome = "Veicolo-BBB", GammaNome = "Gamma-A" },
        ]);
        state.RicaricaTutto();
        var main = new MainViewModel(state);

        await NavigaAsync(main, "audit");
        var audit = Assert.IsType<AuditViewModel>(main.CurrentPage);
        Assert.Equal("Veicolo-AAA", audit.SottoveicoloSelezionato); // primo della lista, default alla prima apertura

        audit.SottoveicoloSelezionato = "Veicolo-BBB";

        await NavigaAsync(main, "home");
        await NavigaAsync(main, "audit");

        Assert.Same(audit, main.CurrentPage);
        Assert.Equal("Veicolo-BBB", audit.SottoveicoloSelezionato);
    }

}
