using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura dedicata al punto 11 (feedback utente 2026-08-20): uno Standard può aggiungere nuove
/// Funzionalità/componente ma non modificare o eliminare quelle esistenti — eccezione mirata alla regola uniforme
/// "tutti i master data solo Master" (PuoModificare). Verifica sia il gate PuoCreare sia la barriera reale al
/// salvataggio (SoloRigheNuoveModificate), non solo l'IsEnabled della UI.</summary>
public sealed class PermessiCreazioneStandardTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    private AppState CreateState(RuoloOperatore ruolo)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_PermessiCreazione_" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var op = new Operatore { Nome = "Op Test", Ruolo = ruolo, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" };
        RuoloSigilloService.Sigilla(op); // migrazione 6.4.2: un ruolo non sigillato vale come Viewer.
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Op Test", "1234");
        return state;
    }

    [Fact]
    public void Funzionalita_Standard_PuoCreare_Ma_NonPuoModificare()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var vm = new FunzionalitaViewModel(state);
        Assert.True(vm.PuoCreare);
        Assert.False(vm.PuoModificare);
    }

    [Fact]
    public async Task Funzionalita_Standard_AggiungeNuovaVoceESalva_Riesce()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var vm = new FunzionalitaViewModel(state);
        var prima = state.Master.Funzionalita.Load().Count;

        vm.IniziaModificaCommand.AttendiModifica();
        Assert.True(vm.InModifica);
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "Nuova funzionalità Standard";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore);
        Assert.False(vm.InModifica);
        Assert.Equal(prima + 1, state.Master.Funzionalita.Load().Count);
        Assert.Contains(state.Master.Funzionalita.Load(), f => f.Nome == "Nuova funzionalità Standard");
    }

    [Fact]
    public async Task Funzionalita_Standard_ModificaVoceEsistente_SalvaBloccatoConMessaggio()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var esistenti = state.Master.Funzionalita.Load().ToList();
        esistenti.Add(new Funzionalita { Nome = "Preesistente", ModificatoDa = "Master" });
        state.Master.Funzionalita.Save(esistenti);

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.First(f => f.Nome == "Preesistente");
        riga.Nome = "Modificata da Standard";

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.True(vm.InModifica); // il salvataggio non è avvenuto, resta in modifica
        Assert.Contains(state.Master.Funzionalita.Load(), f => f.Nome == "Preesistente");
        Assert.DoesNotContain(state.Master.Funzionalita.Load(), f => f.Nome == "Modificata da Standard");
    }

    [Fact]
    public void Funzionalita_Standard_ArchiviaVoceEsistente_Bloccato()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var esistenti = state.Master.Funzionalita.Load().ToList();
        esistenti.Add(new Funzionalita { Nome = "Preesistente Archivio", ModificatoDa = "Master" });
        state.Master.Funzionalita.Save(esistenti);

        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.First(f => f.Nome == "Preesistente Archivio");

        vm.ArchiviaCommand.Execute(riga);

        Assert.True(vm.HaMessaggioErrore);
        Assert.False(riga.Archiviata);
    }

    [Fact]
    public void Funzionalita_Standard_ArchiviaVoceAppenaCreata_Consentito()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var vm = new FunzionalitaViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        var nuova = vm.Items.Last();
        nuova.Nome = "Creata poi archiviata";

        vm.ArchiviaCommand.Execute(nuova);

        Assert.False(vm.HaMessaggioErrore);
        Assert.True(nuova.Archiviata);
    }

    [Fact]
    public void Componente_Standard_PuoCreare_Ma_NonPuoModificare()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var vm = new EcuViewModel(state);
        Assert.True(vm.PuoCreare);
        Assert.False(vm.PuoModificare);
    }

    [Fact]
    public async Task Componente_Master_PuoModificareVociEsistenti()
    {
        var state = CreateState(RuoloOperatore.Master);
        var esistenti = state.Master.Componente.Load().ToList();
        esistenti.Add(new Componente { Nome = "Componente Preesistente", ModificatoDa = "Master", VeicoliAssociati = { "V1" } });
        state.Master.Componente.Save(esistenti);

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.First(e => e.Nome == "Componente Preesistente");
        // Un Master può ancora modificare liberamente le altre proprietà di una riga esistente...
        riga.VeicoliAssociati.Add("V2");
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.HaMessaggioErrore);
        Assert.Contains(state.Master.Componente.Load(), e => e.Nome == "Componente Preesistente" && e.VeicoliAssociati.Contains("V2"));
    }

    /// <summary>Spec 2026-09-23 (terzo giro coordinatore): rinominare una voce esistente scrivendo direttamente
    /// nella cella Nome della griglia romperebbe silenziosamente ogni Test/NC/CR che la referenzia per nome —
    /// ora è bloccato anche per un Master, che deve passare dal motore Rinomina (Unione) con la sua anteprima e
    /// propagazione. Sostituisce il vecchio comportamento verificato da Componente_Master_PuoModificareVociEsistenti.</summary>
    [Fact]
    public async Task Componente_ModificaDirettaDelNomeInGriglia_EBloccataAncheAMaster_UsaRinomina()
    {
        var state = CreateState(RuoloOperatore.Master);
        var esistenti = state.Master.Componente.Load().ToList();
        esistenti.Add(new Componente { Nome = "Componente Preesistente", ModificatoDa = "Master" });
        state.Master.Componente.Save(esistenti);

        var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        var riga = vm.Items.First(e => e.Nome == "Componente Preesistente");
        riga.Nome = "Componente Rinominata da Master";
        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.DoesNotContain(state.Master.Componente.Load(), e => e.Nome == "Componente Rinominata da Master");
        Assert.Contains(state.Master.Componente.Load(), e => e.Nome == "Componente Preesistente");
    }

    [Fact]
    public void Requisiti_Standard_NonPuoCreare_AltriMasterDataRestanoMasterOnly()
    {
        var state = CreateState(RuoloOperatore.Standard);
        var vm = new RequisitiViewModel(state);
        Assert.False(vm.PuoCreare);
        Assert.False(vm.PuoModificare);
    }
}
