using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressioni dell'audit sulle pagine master data (2026-09-30): pannello Unisci/Rinomina/Dividi
/// bloccato in modifica, Rinomina solo-maiuscole + Trim, nome obbligatorio, "+Nuovo" con filtro attivo e
/// duplicati di chiave fuori dalla finestra dei cataloghi paginati (Salva/Import).</summary>
public sealed class MasterDataAuditFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterAudit_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _handlerOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _handlerOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Audit", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);

    // --- 1. Pannello Unisci/Rinomina/Dividi disabilitato in modifica -------------------------------------------

    [Fact]
    public async Task InModifica_PannelloUnioneDisabilitato_EOperazioniRifiutateSenzaPerdereLeModificheLocali()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var vm = new EcuViewModel(state);
        Assert.True(vm.Unione.PannelloAbilitato);
        Assert.True(vm.Unione.MostraPannello);

        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        Assert.False(vm.Unione.PannelloAbilitato);
        Assert.False(vm.Unione.MostraPannello); // in modifica il pannello sparisce (feedback 2026-10-01), non resta solo disabilitato
        Assert.False(vm.Unione.MostraCoppieSospette);
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "NUOVA-LOCALE";

        Assert.False(await vm.Unione.Rinomina("BCM", "BCM2"));
        Assert.False(await vm.Unione.Unisci("BCM", "ECM"));
        Assert.False(await vm.Unione.Dividi("BCM", new[] { "BCM-B" }));

        Assert.Equal(T("MasterUnioneSalvaPrimaHint"), vm.Unione.ErroreOperazione);
        Assert.Contains(vm.Items, e => e.Nome == "NUOVA-LOCALE"); // nessun Ricarica silenzioso
        Assert.Equal(new[] { "BCM", "ECM" }, state.Master.Componente.Load().Select(e => e.Nome).OrderBy(n => n));

        vm.AnnullaCommand.Execute(null); // modifica non salvata: la conferma di abbandono e' gestita dall'handler
        Assert.False(vm.InModifica);
        Assert.True(vm.Unione.PannelloAbilitato);
        Assert.True(vm.Unione.MostraPannello);
    }

    // --- 3. Rinomina solo-maiuscole e Trim -----------------------------------------------------------------------

    [Fact]
    public async Task Rinomina_SoloMaiuscole_EAmmessaEMantieneLaVoce()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "componente1" } });
        var uid = MasterEntityIdentityService.GetOrCreate(state.Master.Componente.Load()[0]);
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var vm = new MasterUnioneViewModel<Componente>(state, state.Master.Componente, e => e.Nome);

        Assert.True(await vm.Rinomina("componente1", "Componente1"));

        var dopo = Assert.Single(state.Master.Componente.Load());
        Assert.Equal("Componente1", dopo.Nome);
        Assert.Equal(uid, MasterEntityIdentityService.GetOrCreate(dopo));
    }

    [Fact]
    public async Task Rinomina_NuovoNomeConSpaziFinali_VieneTrimmato()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente1" } });
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        var vm = new MasterUnioneViewModel<Componente>(state, state.Master.Componente, e => e.Nome);

        Assert.True(await vm.Rinomina("Componente1", "  Componente9  "));

        Assert.Equal("Componente9", Assert.Single(state.Master.Componente.Load()).Nome);
    }

    [Fact]
    public async Task Rinomina_VersoUnaAltraVoceEsistente_ResteraRifiutata()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "A" }, new() { Nome = "B" } });
        var vm = new MasterUnioneViewModel<Componente>(state, state.Master.Componente, e => e.Nome);

        Assert.False(await vm.Rinomina("A", "b"));
        Assert.Equal(T("MasterRinominaEsistenteProponiUnisci"), vm.ErroreOperazione);
    }

    [Fact]
    public void Servizio_RinominaSoloMaiuscole_NonLanciaEUsaIlNomeTrimmato()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "bcm" } });

        MasterUnioneService.Rinomina(state, state.Master.Componente, "Componente", "bcm", "BCM ", e => e.Nome, new List<string>());

        Assert.Equal("BCM", Assert.Single(state.Master.Componente.Load()).Nome);
    }

    // --- 4. Nome obbligatorio --------------------------------------------------------------------------------------

    [Fact]
    public async Task Salva_RigaNuovaConNomeVuoto_BloccataConMessaggioLocalizzato()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null); // Nome vuoto

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.Equal(T("MasterDataNomeObbligatorio"), vm.MessaggioErrore);
        Assert.Single(state.Master.Componente.Load());
    }

    [Fact]
    public async Task Salva_RigaConNomeVuotoMaArchiviata_NonBlocca()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.ArchiviaCommand.Execute(vm.Items.Last()); // riga vuota poi archiviata: innocua

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.NotEqual(T("MasterDataNomeObbligatorio"), vm.MessaggioErrore);
    }

    // --- 5. "+Nuovo" con filtro attivo ---------------------------------------------------------------------------

    [Fact]
    public void Nuovo_ConFiltroAttivo_AzzeraIlFiltroEMostraESelezionaLaNuovaRiga()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.FiltroTesto = "BCM";
        vm.ConfermaRicercaImmediata();
        Assert.Single(vm.ItemsAttivi);

        vm.NuovoCommand.Execute(null);

        Assert.Equal("", vm.FiltroTesto);
        var nuova = vm.Items.Last();
        Assert.Contains(nuova, vm.ItemsAttivi);
        Assert.Equal(3, vm.ItemsAttivi.Count);
        Assert.Same(nuova, vm.ElementoSelezionato);
    }

    // --- 2. Duplicati fuori finestra nei cataloghi paginati ------------------------------------------------------

    private AppState CreaStatoConEcuPaginate()
    {
        var state = CreaStato();
        state.Master.Componente.Save(Enumerable.Range(0, 450)
            .Select(i => new Componente { Nome = $"Componente-{i:D3}", ModificatoDa = "Seed" })
            .ToList());
        return state;
    }

    [Fact]
    public void Paginato_CatalogoInUnaPagina_NonMostraAvvisoModificaLimitata()
    {
        using var state = CreaStato();
        state.Master.Componente.Save(Enumerable.Range(0, 9)
            .Select(i => new Componente { Nome = $"Componente-{i:D3}", ModificatoDa = "Seed" })
            .ToList());
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        Assert.False(vm.MostraAvvisoModificaLimitata);
        vm.AnnullaCommand.Execute(null);
    }

    [Fact]
    public async Task Paginato_SalvaConChiaveDuplicataFuoriFinestra_MessaggioLocalizzatoConLaChiave()
    {
        using var state = CreaStatoConEcuPaginate();
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.MostraAvvisoModificaLimitata);
        Assert.DoesNotContain(vm.Items, e => e.Nome == "Componente-400"); // fuori dalla finestra da 200

        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "componente-400"; // stessa chiave a meno delle maiuscole

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.Equal(string.Format(T("MasterDataChiaveDuplicataFmt"), "componente-400"), vm.MessaggioErrore);
        Assert.DoesNotContain("Collisione Phase D", vm.MessaggioErrore);
        Assert.Equal(450, state.Master.Componente.Load().Count);
    }

    [Fact]
    public async Task Paginato_ImportConChiaveFuoriFinestra_TrattataComeEsistenteNonAggiuntaDuplicata()
    {
        using var state = CreaStatoConEcuPaginate();
        Directory.CreateDirectory(_dir);
        string percorso;
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            ws.Cell(1, 1).Value = "Nome";
            ws.Cell(2, 1).Value = "Componente-400";
            ws.Cell(3, 1).Value = "Componente-NUOVA";
            percorso = Path.Combine(_dir, "import_" + Guid.NewGuid().ToString("N") + ".xlsx");
            wb.SaveAs(percorso);
        }

        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ImportaDaExcelCommand.Execute(new ImportExcelRichiesta(percorso, null, 1));

        Assert.False(vm.HaMessaggioErrore);
        Assert.Single(vm.Items, e => e.Nome == "Componente-400"); // caricata dall'indice, non duplicata
        Assert.Single(vm.Items, e => e.Nome == "Componente-NUOVA");

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica, vm.MessaggioErrore);
        var catalogo = state.Master.Componente.Load();
        Assert.Equal(451, catalogo.Count);
        Assert.Single(catalogo, e => e.Nome == "Componente-400");
    }
}
