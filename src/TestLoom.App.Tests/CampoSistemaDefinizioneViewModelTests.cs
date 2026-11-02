using System.Linq;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class CampoSistemaDefinizioneViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_campisistemavm_" + Guid.NewGuid().ToString("N"));

    private AppState CreaStatoComeMaster()
    {
        SeedDemoDataService.Genera(_dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().Single(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    [Fact]
    public void Titolo_DifferisceTraPianificazioneEDettagli()
    {
        var state = CreaStatoComeMaster();
        var pianificazione = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Pianificazione);
        var dettagli = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);

        Assert.NotEqual(pianificazione.Titolo, dettagli.Titolo);
    }

    [Fact]
    public async Task Nuovo_RuoloNessunoEGruppoAssegnatiSubito_ChiaveDaEtichettaSoloAlSalvataggio()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();

        vm.NuovoCommand.Execute(null);
        var nuova = vm.Items.Last();
        Assert.Equal(RuoloCampoSistema.Nessuno, nuova.Ruolo);
        Assert.Equal(GruppoCampoSistema.Dettagli, nuova.Gruppo);
        Assert.True(string.IsNullOrWhiteSpace(nuova.Chiave)); // ancora nessuna Chiave: l'Etichetta è solo il placeholder finché non si salva

        nuova.Etichetta = "Colore carrozzeria";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Equal("colore-carrozzeria", nuova.Chiave);
    }

    [Fact]
    public async Task Salva_ChiaveUnivocaTraPianificazioneEDettagli()
    {
        var state = CreaStatoComeMaster();
        var pianificazione = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Pianificazione);
        pianificazione.IniziaModificaCommand.AttendiModifica();
        pianificazione.NuovoCommand.Execute(null);
        pianificazione.Items.Last().Etichetta = "Colore";
        await pianificazione.SalvaCommand.ExecuteAsync(null);

        var dettagli = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        dettagli.IniziaModificaCommand.AttendiModifica();
        dettagli.NuovoCommand.Execute(null);
        dettagli.Items.Last().Etichetta = "Colore";
        await dettagli.SalvaCommand.ExecuteAsync(null);

        Assert.Equal("colore-2", dettagli.Items.Single(v => v.Etichetta == "Colore").Chiave);
    }

    [Fact]
    public async Task Salva_EtichettaVuotaSuCampoNuovo_BloccaIlSalvataggio()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Etichetta = "   ";

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(vm.HaMessaggioErrore);
    }

    [Fact]
    public async Task Salva_BloccaCambioTipoSuCampoConRuolo()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        var componente = vm.Items.Single(v => v.Ruolo == RuoloCampoSistema.Componente);
        componente.Tipo = TipoCampoStrumento.Numero;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica); // il salvataggio non è avvenuto, resta in modifica
        Assert.True(vm.HaMessaggioErrore);
    }

    [Fact]
    public async Task Salva_BloccaCambioTipoSuCampoLiberoConDatiEsistenti()
    {
        var state = CreaStatoComeMaster();
        var tc = new TestCase { Id = "TC-CS-1", CampiCustom = { ["MioCampo"] = "valore" } };
        state.TestCases.Save(tc, true);

        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Add(new CampoSistemaDefinizione { Chiave = "MioCampo", Etichetta = "Mio campo", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Testo });
        await vm.SalvaCommand.ExecuteAsync(null); // prima salva per far diventare questa la nuova baseline/snapshot
        if (vm.RilascioSessioneLockInCorso is { } rilascioLock)
            await rilascioLock;
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Single(v => v.Chiave == "MioCampo").Tipo = TipoCampoStrumento.Numero;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(vm.HaMessaggioErrore);
    }

    [Fact]
    public async Task Salva_PermetteCambioTipoSuCampoLiberoSenzaDati()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Add(new CampoSistemaDefinizione { Chiave = "MioCampo2", Etichetta = "Mio campo 2", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Testo });
        await vm.SalvaCommand.ExecuteAsync(null);
        if (vm.RilascioSessioneLockInCorso is { } rilascioLock)
            await rilascioLock;
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Single(v => v.Chiave == "MioCampo2").Tipo = TipoCampoStrumento.Numero;

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.InModifica); // salvataggio riuscito
        Assert.False(vm.HaMessaggioErrore);
    }

    [Fact]
    public void Nuovo_SelezionaLaRigaNuovaERiportaLaRigaCreata()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        CampoSistemaDefinizione? creata = null;
        vm.RigaCreata += voce => creata = voce;
        vm.IniziaModificaCommand.AttendiModifica(); // IniziaModifica è asincrono: Nuovo richiede la modifica già avviata

        vm.NuovoCommand.Execute(null);

        Assert.NotNull(creata);
        Assert.Same(creata, vm.ElementoSelezionato);
        Assert.Equal(LocalizationService.Instance.Traduci("CampoSistemaNuovaEtichettaDefault"), creata!.Etichetta);
    }

    [Fact]
    public async Task Salva_CampoListaConOpzioniPersisteEDopoRicaricamento()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Add(new CampoSistemaDefinizione { Chiave = "vci-connection", Etichetta = "VCI connection", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "USB", "Ethernet" } });

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore);
        var persistito = state.Master.CampiSistemaDettagli.Load().Single(c => c.Chiave == "vci-connection");
        Assert.Equal(TipoCampoStrumento.Lista, persistito.Tipo);
        Assert.Equal(new[] { "USB", "Ethernet" }, persistito.OpzioniLista);

        var riaperto = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        Assert.Equal(new[] { "USB", "Ethernet" }, riaperto.Items.Single(c => c.Chiave == "vci-connection").OpzioniLista);
    }

    [Fact]
    public async Task ImpostaOpzioniLista_ModificaSoloLeOpzioniESalvaLaModifica()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Add(new CampoSistemaDefinizione { Chiave = "vci-connection", Etichetta = "VCI connection", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "USB" } });
        await vm.SalvaCommand.ExecuteAsync(null);
        if (vm.RilascioSessioneLockInCorso is { } rilascioLock)
            await rilascioLock;
        vm.IniziaModificaCommand.AttendiModifica();
        var campo = vm.Items.Single(c => c.Chiave == "vci-connection");

        Assert.False(vm.ImpostaOpzioniLista(campo, new List<string> { "USB" })); // stessa lista: nulla da salvare
        Assert.False(vm.HaModificheNonSalvate());

        Assert.True(vm.ImpostaOpzioniLista(campo, new List<string> { "USB", "Ethernet" }));
        Assert.True(vm.HaModificheNonSalvate());
        var voce = campo.Changelog.Last();
        Assert.Equal(nameof(CampoSistemaDefinizione.OpzioniLista), voce.Campo);
        Assert.Equal("USB", voce.ValorePrecedente);
        Assert.Equal("USB; Ethernet", voce.ValoreNuovo);

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore);
        Assert.False(vm.InModifica);
        Assert.Equal(new[] { "USB", "Ethernet" }, state.Master.CampiSistemaDettagli.Load().Single(c => c.Chiave == "vci-connection").OpzioniLista);
    }

    [Fact]
    public async Task Salva_UnitaMisuraDelCampoNumeroPersiste()
    {
        var state = CreaStatoComeMaster();
        var vm = new CampoSistemaDefinizioneViewModel(state, GruppoCampoSistema.Dettagli);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.Items.Add(new CampoSistemaDefinizione { Chiave = "distanza-test", Etichetta = "Distanza", Gruppo = GruppoCampoSistema.Dettagli, Tipo = TipoCampoStrumento.Numero, UnitaMisura = "km" });

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore);
        Assert.Equal("km", state.Master.CampiSistemaDettagli.Load().Single(c => c.Chiave == "distanza-test").UnitaMisura);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
