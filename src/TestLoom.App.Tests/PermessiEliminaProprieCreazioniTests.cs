using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Follow-up del punto 11 (feedback utente 2026-08-20, coperto da PermessiCreazioneStandardTests): l'utente
/// ha chiesto di più (2026-09-18): "gli standard possono creare di base ma non possono eliminare voci create
/// da un master o da un altro standard, possono solo creare voci in componente, veicoli, nello strumento ecc.
/// dovrebbero poter creare ed eliminare esclusivamente le cose create da loro stessi." Prima di questa
/// modifica un utente con solo PuoCreare non poteva archiviare/eliminare NESSUNA riga preesistente, nemmeno
/// una creata da lui stesso in una sessione precedente (solo le righe "appena creata in questa sessione" erano
/// consentite, via EInRigaNuova). Ora CreatoDa (nuovo campo su MasterEntity, mai più sovrascritto dopo la
/// creazione) distingue "mia" da "di qualcun altro" anche fra sessioni diverse.
/// </summary>
public sealed class PermessiEliminaProprieCreazioniTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private AppState CreateState(RuoloOperatore ruolo, string nome = "Op Test")
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_EliminaProprieCreazioni_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var ops = state.Master.Operatori.Load().ToList();
        if (!ops.Any(o => o.Nome == nome))
        {
            var op = new Operatore { Nome = nome, Ruolo = ruolo, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
            RuoloSigilloService.Sigilla(op); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
            ops.Add(op);
        }
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore(nome, "1234"));
        return state;
    }

    [Fact]
    public async Task Componente_Standard_ArchiviaUnaRigaCreataDaLuiInUnaSessionePrecedente_Consentito()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        // Simula una riga creata da questo stesso operatore in una sessione PRECEDENTE (già su disco, non
        // "appena creata" in questa sessione: CreatoDa e' l'unica cosa che ora lo rende eliminabile).
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente-MIA", ModificatoDa = "Mario Standard", CreatoDa = "Mario Standard" } });

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "Componente-MIA");

        vm.ArchiviaCommand.Execute(riga);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.True(riga.Archiviata);

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.True(state.Master.Componente.Load().Single(e => e.Nome == "Componente-MIA").Archiviata);
    }

    [Fact]
    public void Componente_Standard_ArchiviaUnaRigaCreataDaUnAltroStandard_Bloccato()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente-ALTRUI", ModificatoDa = "Luigi Standard", CreatoDa = "Luigi Standard" } });

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "Componente-ALTRUI");

        vm.ArchiviaCommand.Execute(riga);

        Assert.True(vm.HaMessaggioErrore);
        Assert.False(riga.Archiviata);
    }

    [Fact]
    public void Componente_Standard_ArchiviaUnaRigaSenzaCreatoDaAttribuito_Bloccato()
    {
        // Righe esistenti prima di questa modifica: CreatoDa vuoto, nessuna attribuzione retroattiva
        // possibile — deve restare bloccato, mai trattato come "eliminabile per default".
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente-VECCHIA", ModificatoDa = "Master" } });

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "Componente-VECCHIA");

        vm.ArchiviaCommand.Execute(riga);

        Assert.True(vm.HaMessaggioErrore);
        Assert.False(riga.Archiviata);
    }

    [Fact]
    public async Task Componente_Standard_EliminaDefinitivamenteUnaRigaSuaGiaArchiviata_ConsentitoESalvaRiesce()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente-MIA-DA-ELIMINARE", ModificatoDa = "Mario Standard", CreatoDa = "Mario Standard", Archiviata = true } });

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "Componente-MIA-DA-ELIMINARE");

        vm.EliminaDefinitivamenteCommand.Execute(riga);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.DoesNotContain(vm.Items, e => e.Nome == "Componente-MIA-DA-ELIMINARE");

        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.DoesNotContain(state.Master.Componente.Load(), e => e.Nome == "Componente-MIA-DA-ELIMINARE");
    }

    [Fact]
    public void Componente_Master_ArchiviaEliminaQualunqueRigaIndipendentementeDaCreatoDa()
    {
        var state = CreateState(RuoloOperatore.Master, "Il Master");
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "Componente-DI-STANDARD", ModificatoDa = "Uno Standard", CreatoDa = "Uno Standard" } });

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(e => e.Nome == "Componente-DI-STANDARD");

        vm.ArchiviaCommand.Execute(riga);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.True(riga.Archiviata);
    }

    [Fact]
    public void NuovaVoce_CreaDaStandard_HaCreatoDaImpostatoAllOperatoreCorrente()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);

        Assert.Equal("Mario Standard", vm.Items.Last().CreatoDa);
    }

    // === Veicoli e Strumenti ora hanno la stessa eccezione di componente/Funzionalità (feedback utente 2026-09-18:
    // "possono solo creare voci in componente, veicoli, nello strumento ecc.") ===================================

    [Fact]
    public async Task Veicoli_Standard_OraPuoCreare_EArchiviareLaPropriaVoce()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        var vm = new VeicoliViewModel(state);
        Assert.True(vm.PuoCreare);
        Assert.False(vm.PuoModificare);

        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Veicolo Nuovo Standard";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.Contains(state.Master.Veicoli.Load(), v => v.Nome == "Veicolo Nuovo Standard" && v.CreatoDa == "Mario Standard");
    }

    [Fact]
    public void Veicoli_Standard_ArchiviaVoceAltrui_Bloccato()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Veicolo Altrui", ModificatoDa = "Master", CreatoDa = "Master" } });

        var vm = new VeicoliViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(v => v.Nome == "Veicolo Altrui");

        vm.ArchiviaCommand.Execute(riga);

        Assert.True(vm.HaMessaggioErrore);
        Assert.False(riga.Archiviata);
    }

    [Fact]
    public async Task Strumenti_Standard_OraPuoCreare_EArchiviareLaPropriaVoce()
    {
        var state = CreateState(RuoloOperatore.Standard, "Mario Standard");
        var vm = new StrumentiViewModel(state);
        Assert.True(vm.PuoCreare);
        Assert.False(vm.PuoModificare);

        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Strumento Nuovo Standard";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);

        if (vm.RilascioSessioneLockInCorso is { } rilascio)
            await rilascio.WaitAsync(TimeSpan.FromSeconds(5));
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.Single(s => s.Nome == "Strumento Nuovo Standard");
        vm.ArchiviaCommand.Execute(riga);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.True(riga.Archiviata);
    }
}
