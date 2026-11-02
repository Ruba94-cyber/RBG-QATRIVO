using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

[Collection("Localization")]
[Trait("Category", "Lento")]
public sealed class SecondaryTestEditorLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_secondary_editor_" + Guid.NewGuid().ToString("N"));
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_secondary_editor_cfg_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    [Fact]
    public async Task EditorValidazioneSecondario_AnnullaConfermato_RilasciaLockEInvocaChiusuraProprietaria()
    {
        SeedDemoDataService.Genera(_dir);
        using var state = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        var test = state.TestCases.GetAll().First(t => !t.Eliminato);
        var parent = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });
        var chiusuraRichiesta = false;
        var editor = parent.CreaEditorTestValidazione(test.Id, () => chiusuraRichiesta = true);

        await editor.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(editor.InModifica);
        var path = state.TestCases.PercorsoPer(editor.TestCase);
        Assert.NotNull(state.Locks.GetActiveLock(path));

        editor.ModificheNonSalvate = true;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        await editor.AnnullaCommand.ExecuteAsync(null);

        Assert.True(chiusuraRichiesta);
        Assert.Null(state.Locks.GetActiveLock(path));
        editor.Dispose();
    }

    [Fact]
    public async Task EditorValidazioneSecondario_AnnullaRifiutato_ConservaLockENonChiedeChiusura()
    {
        SeedDemoDataService.Genera(_dir);
        using var state = new AppState(new AppConfigService(Path.Combine(_configDir, "config.json")), runBackgroundMaintenance: false);
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = state.Master.Operatori.Load().First(o => o.Ruolo == RuoloOperatore.Master);
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));

        var test = state.TestCases.GetAll().First(t => !t.Eliminato);
        var parent = new ChangeRequestEditorViewModel(state, null, () => { }, _ => { });
        var chiusuraRichiesta = false;
        var editor = parent.CreaEditorTestValidazione(test.Id, () => chiusuraRichiesta = true);

        await editor.IniziaModificaCommand.ExecuteAsync(null);
        Assert.True(editor.InModifica);
        var path = state.TestCases.PercorsoPer(editor.TestCase);
        editor.ModificheNonSalvate = true;
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(false);

        await editor.AnnullaCommand.ExecuteAsync(null);

        Assert.False(chiusuraRichiesta);
        Assert.NotNull(state.Locks.GetActiveLock(path));
        editor.Dispose();
    }

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        TryDelete(_dir);
        TryDelete(_configDir);
    }

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
