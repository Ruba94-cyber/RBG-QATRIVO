using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copertura del comando ViewModel per il bug segnalato 2026-09-18 ("nulla si elimina mai
/// definitivamente"): la logica di dominio è già coperta da DomainRecoveryServiceTests.Purge_*, qui si
/// verifica solo il cablaggio (permessi, conferma, refresh della lista) del comando PurgaCommand.</summary>
[Collection("Localization")]
public sealed class RecoveryBinPurgaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_RecoveryBinPurga_" + Guid.NewGuid().ToString("N"));

    public RecoveryBinPurgaTests() => ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

    public void Dispose()
    {
        ConfirmService.Handler = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AppState CreaStato(RuoloOperatore ruolo)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var nome = "Operatore " + ruolo;
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = nome, Ruolo = ruolo, PinHash = PinService.Hash("1234") }) });
        Assert.True(state.ImpostaOperatore(nome, "1234"));
        return state;
    }

    private static Guid CreaRecordDiRecovery(AppState state)
    {
        var source = state.Paths.NonConformitaFile("NC-PURGA-1");
        File.WriteAllText(source, "{\"Uid\":\"" + Guid.NewGuid() + "\",\"Id\":\"NC-PURGA-1\",\"RevisioneDati\":1}");
        return DomainRecoveryService.ArchiveAndDelete(state.Paths, source, DomainRecoveryCategory.NonConformita, "Master").RecoveryId;
    }

    [Fact]
    public async Task Master_PurgaUnElemento_LoRimuoveDallaListaEDalDisco()
    {
        var state = CreaStato(RuoloOperatore.Master);
        CreaRecordDiRecovery(state);
        var vm = new RecoveryBinViewModel(state);
        var riga = Assert.Single(vm.Elementi);

        await vm.PurgaCommand.ExecuteAsync(riga);

        Assert.False(vm.HaMessaggioErrore, vm.MessaggioErrore);
        Assert.Empty(vm.Elementi);
        Assert.Empty(DomainRecoveryService.GetAll(state.Paths));
    }

    [Fact]
    public async Task Standard_NonPuoPurgare_ComandoNoOp()
    {
        var state = CreaStato(RuoloOperatore.Standard);
        var idRecovery = CreaRecordDiRecovery(state);
        // Elementi resta vuoto per uno Standard (Aggiorna() filtra su EMaster): costruisco la riga a mano per
        // verificare che il comando stesso rifiuti l'azione, non solo che la lista sia vuota.
        var record = Assert.Single(DomainRecoveryService.GetAll(state.Paths), r => r.RecoveryId == idRecovery);
        var riga = new RecoveryBinEntryViewModel(record, workspaceWritable: true);
        var vm = new RecoveryBinViewModel(state);

        await vm.PurgaCommand.ExecuteAsync(riga);

        Assert.Single(DomainRecoveryService.GetAll(state.Paths));
    }
}
