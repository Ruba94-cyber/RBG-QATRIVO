using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using static TestLoom.App.Tests.NavigazioneTestHelper;

namespace TestLoom.App.Tests;

/// <summary>Regressione del difetto utente 6.7.3 (a): da Audit con un veicolo selezionato, passando al Report il banner
/// di navigazione mostrava l'eccezione grezza ("Object reference not set...") e la pagina non si apriva.</summary>
public sealed class ReportDaAuditNavigazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportDaAudit_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoConMolteProve(int numeroTest)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Master Test", "1234");

        const string veicolo = "E-DAILY MY24/26";
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = veicolo }, new() { Nome = "Veicolo-Altro" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "IPC12", VeicoliAssociati = { veicolo } } });
        state.Master.Requisiti.Save(new List<Requisito> { new() { Id = "R1", Testo = "Il sistema deve..." } });
        for (var i = 0; i < numeroTest; i++)
        {
            state.TestCases.Save(new TestCase
            {
                Id = $"TC-{i:D5}",
                Componente = "IPC12",
                Veicolo = veicolo,
                RequisitoId = new List<string> { "R1" },
            }, isNew: true);
        }
        state.RicaricaTutto();
        return state;
    }

    [Fact]
    public void BannerNavigazione_MostraTestoLocalizzatoSenzaMessaggioGrezzo()
    {
        var testo = MainViewModel.MessaggioErroreNavigazione("Report");

        Assert.Contains("Report", testo);
        Assert.DoesNotContain("Object reference", testo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reference", testo, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Il selettore dell'asse, staccando la pagina dall'albero visuale, azzera la propria selezione e la scrive nel
    /// campo: il Report deve conservare la dimensione e il ritorno (Aggiorna) non deve dereferenziare un campo vuoto.</summary>
    [Fact]
    public void Report_DimensioneAsseAzzeratadallaVista_ConservaLaSceltaERitornoNonFallisce()
    {
        var state = CreaStatoConMolteProve(10);
        var report = new ReportViewModel(state);
        var campo = report.CampiXMatrice[0];
        var scelta = campo.Dimensione;

        campo.Dimensione = null!;

        Assert.Same(scelta, campo.Dimensione);
        report.Aggiorna();
        Assert.NotNull(report.CampiXMatrice[0].Dimensione);
    }

    [Fact]
    public async Task ReportInBackground_DopoAuditConVeicolo_RiaggiornaSenzaEccezioni()
    {
        var state = CreaStatoConMolteProve(300);
        var report = new ReportViewModel(state, aggiornaInBackground: true);
        await report.AttendiAggiornamentoAsync();
        Assert.Null(report.UltimaEccezioneAggiornamento);

        var audit = new AuditViewModel(state) { SottoveicoloSelezionato = "E-DAILY MY24/26" };
        Assert.NotNull(audit);

        report.Aggiorna();
        await report.AttendiAggiornamentoAsync();
        Assert.Null(report.UltimaEccezioneAggiornamento);
    }

    [Fact]
    public async Task AuditConVeicoloSelezionato_PoiReport_NonMostraErroreNavigazione()
    {
        var state = CreaStatoConMolteProve(2000);
        var main = new MainViewModel(state);

        await NavigaAsync(main, "audit");
        var audit = Assert.IsType<AuditViewModel>(main.CurrentPage);
        audit.SottoveicoloSelezionato = "E-DAILY MY24/26";

        await NavigaAsync(main, "report");

        Assert.Null(main.ErroreNavigazione);
        Assert.IsType<ReportViewModel>(main.CurrentPage);
    }
}
