using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class TestSaveBeginRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SaveBeginRace_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_SaveBeginRaceLocal_" + Guid.NewGuid().ToString("N"));

    public TestSaveBeginRaceTests()
    {
        SeedDemoDataService.Genera(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_local);
    }

    [Fact]
    public async Task ForeignMarkerAppearingAfterEditStarted_BlocksSaveWithoutTouchingAuthoritativeTest()
    {
        using var state = new AppState(new AppConfigService(Path.Combine(_local, "config.json")), runBackgroundMaintenance: false);
        state.InizializzaCartella(_root);
        var operatore = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(operatore.Nome, "1234"));

        var tc = new TestCase
        {
            Id = "TC-RACE-0001",
            CreatoDa = operatore.Nome,
            ModificatoDa = operatore.Nome,
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            Descrizione = "autorevole"
        };
        tc.Componente.Add("VCM");
        state.TestCases.Save(tc, isNew: true);

        var authoritativePath = state.TestCases.PercorsoPer(tc);
        var before = File.ReadAllBytes(authoritativePath);

        using var vm = new TestCaseEditorViewModel(state, tc.Id, () => { }, _ => { });
        await vm.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(vm.InModifica);

        vm.TestCase.Descrizione = "modifica locale non committata";
        vm.ModificheNonSalvate = true;
        var draftBefore = state.BozzaAutosave.CaricaModifica(operatore.Nome, vm.TestCase.Uid);
        Assert.NotNull(draftBefore);

        // Simula esattamente la race: l'editor ha già lock/dirty state, poi un altro PC pubblica un marker
        // prima che questo client entri nella Unit of Work del Save.
        var txDir = Path.Combine(_root, "_ditm", "transactions");
        Directory.CreateDirectory(txDir);
        var marker = new WorkflowTransactionMarker
        {
            Id = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow,
            Device = "PC-REMOTE",
            Description = "save-test:TC-REMOTE"
        };
        var markerPath = Path.Combine(txDir, $"{marker.Id:N}.pending.json");
        JsonIo.WriteAtomic(markerPath, marker);

        await vm.SalvaSicuroCommand.ExecuteAsync(null);

        Assert.True(vm.InModifica);
        Assert.True(vm.ModificheNonSalvate);
        Assert.NotEmpty(vm.ErroriValidazione);
        Assert.Contains(vm.ErroriValidazione, e => e.Contains("commit", StringComparison.OrdinalIgnoreCase)
            || e.Contains("transazione", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, File.ReadAllBytes(authoritativePath));

        var draftAfter = state.BozzaAutosave.CaricaModifica(operatore.Nome, vm.TestCase.Uid);
        Assert.NotNull(draftAfter);
        Assert.Equal("modifica locale non committata", draftAfter!.TestCase.Descrizione);
        Assert.Null(WorkflowTransactionService.ActiveId);

        File.Delete(markerPath);
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
