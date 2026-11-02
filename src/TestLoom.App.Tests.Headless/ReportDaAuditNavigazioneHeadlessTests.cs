using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressione del difetto utente 6.7.3 (a) nell'applicazione reale (MainWindow e viste montate): tornando sul Report
/// (anche dopo essere passati da Audit con un veicolo selezionato) il banner di navigazione mostrava l'eccezione grezza
/// ("Object reference not set...") e la pagina non si apriva.</summary>
[Collection("LayoutHeadless")]
[Trait("Category", "LayoutHeadless")]
public sealed class ReportDaAuditNavigazioneHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportDaAuditHl_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStatoConMolteProve(int numeroTest)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Report", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        state.Master.Operatori.Save(new List<Operatore> { op });
        state.ImpostaOperatore("Master Report", "1234");

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

    private AppState CreaStatoDemo()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        const string veicolo = "E-DAILY MY24/26";
        var veicoli = state.Master.Veicoli.Load().ToList();
        veicoli.Add(new Veicolo { Nome = veicolo, GammaNome = "" });
        state.Master.Veicoli.Save(veicoli);
        var componente = state.Master.Componente.Load().ToList();
        componente.Add(new Componente { Nome = "IPC12", VeicoliAssociati = { veicolo } });
        state.Master.Componente.Save(componente);
        state.RicaricaTutto();
        return state;
    }

    /// <summary>Stato persistito con campi di sistema (uno archiviato) usati come assi e filtri del Report, come lo lascia un uso reale.</summary>
    private AppState CreaStatoPersistito()
    {
        var state = CreaStatoDemo();
        state.Master.CampiSistemaDettagli.Save(new List<CampoSistemaDefinizione>
        {
            new() { Chiave = "vci-connection", Etichetta = "VCI Connection", Tipo = TipoCampoStrumento.Lista, OpzioniLista = new() { "USB", "Bluetooth" }, UsabileComeFiltro = true },
            new() { Chiave = "vci-old", Etichetta = "VCI Vecchio", Tipo = TipoCampoStrumento.Lista, OpzioniLista = new() { "X" }, UsabileComeFiltro = true, Archiviata = true },
        });
        for (var i = 0; i < 50; i++)
        {
            state.TestCases.Save(new TestCase
            {
                Id = $"TC-VCI-{i:D3}",
                Componente = "IPC12",
                Veicolo = "E-DAILY MY24/26",
                CampiCustom = new() { ["vci-connection"] = i % 2 == 0 ? "USB" : "Bluetooth" },
            }, isNew: true);
        }
        state.RicaricaTutto();
        ViewStateService.Scrivi(state, "Report.FiltroEcu", "IPC12");
        ViewStateService.Scrivi(state, "Report.FiltroVeicolo", "E-DAILY MY24/26");
        ViewStateService.ScriviData(state, "Report.DataDa", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ViewStateService.ScriviData(state, "Report.DataA", new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        ViewStateService.Scrivi(state, "Report.AssiX", System.Text.Json.JsonSerializer.Serialize(new List<string> { "custom:vci-connection", "sottoveicolo" }));
        ViewStateService.Scrivi(state, "Report.AssiY", System.Text.Json.JsonSerializer.Serialize(new List<string> { "custom:vci-old", "componente" }));
        ViewStateService.Scrivi(state, "Report.ModalitaMatrice", "stato");
        ViewStateService.ScriviLista(state, "Report.FiltroCustom.vci-connection", new List<string> { "USB" });
        ViewStateService.ScriviLista(state, "Report.FiltroMatrice.custom:vci-connection", new List<string> { "USB" });
        return state;
    }

    private static async Task AttendiAsync(Func<bool> condizione, string descrizione)
    {
        var scadenza = DateTime.UtcNow.AddSeconds(15);
        while (!condizione() && DateTime.UtcNow < scadenza) await Task.Delay(5);
        Assert.True(condizione(), $"entro 15 s non si verifica: {descrizione}");
    }

    [Fact]
    public async Task AuditDemo_PoiReport_InAppReale_NonMostraErroreNavigazione()
    {
        await EseguiScenario(CreaStatoDemo());
    }

    [Fact]
    public async Task AuditConVeicoloSelezionato_PoiReport_InAppReale_NonMostraErroreNavigazione()
    {
        await EseguiScenario(CreaStatoConMolteProve(300));
    }

    [Fact]
    public async Task ReportAuditReport_StatoDemo_TornandoSulReportNonMostraErroreNavigazione()
    {
        await EseguiScenario(CreaStatoDemo(), visitaReportPrima: true);
    }

    [Fact]
    public async Task ReportAuditReport_ConStatoPersistito_TornandoSulReportNonMostraErroreNavigazione()
    {
        await EseguiScenario(CreaStatoPersistito(), visitaReportPrima: true);
    }

    /// <summary>Navigazione reale: Audit (con veicolo selezionato) e Report montati in una MainWindow. Con
    /// <paramref name="visitaReportPrima"/> il Report viene aperto, lasciato e riaperto dalla cache: il ritorno e' il caso del difetto.</summary>
    private async Task EseguiScenario(AppState state, bool visitaReportPrima = false)
    {
        var main = LayoutHeadlessSessione.Esegui(() => new MainViewModel(state) { MostraPrimoAvvio = false });
        await AttendiAsync(() => !main.CaricamentoPagina, "navigazione iniziale completata");
        var finestra = LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.CreaFinestraPrincipale(main, 1280, 800));
        try
        {
            LayoutHeadlessSessione.Esegui(() => LayoutHeadlessSessione.Mostra(finestra));

            if (visitaReportPrima)
            {
                LayoutHeadlessSessione.Esegui(() => main.SelectedNavItem = main.NavItems.First(n => n.Id == "report"));
                await AttendiAsync(() => !main.CaricamentoPagina && main.CurrentPage is ReportViewModel, "Report aperto la prima volta");
                Assert.Null(main.ErroreNavigazione);
            }

            LayoutHeadlessSessione.Esegui(() => main.SelectedNavItem = main.NavItems.First(n => n.Id == "audit"));
            await AttendiAsync(() => !main.CaricamentoPagina && main.CurrentPage is AuditViewModel, "Audit aperto");
            var audit = (AuditViewModel)main.CurrentPage!;
            LayoutHeadlessSessione.Esegui(() => audit.SottoveicoloSelezionato = "E-DAILY MY24/26");

            LayoutHeadlessSessione.Esegui(() => main.SelectedNavItem = main.NavItems.First(n => n.Id == "report"));
            await AttendiAsync(() => !main.CaricamentoPagina && main.CurrentPage is ReportViewModel, "Report aperto");

            Assert.Null(main.ErroreNavigazione);
            Assert.IsType<ReportViewModel>(main.CurrentPage);
        }
        finally
        {
            LayoutHeadlessSessione.Esegui(() => finestra.Hide());
        }
    }
}
