using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Cestino di recovery (ciclo prestazioni 6.6.1): la lettura avviene fuori dal thread UI e l'elenco viene sostituito in blocco.
/// Un ripristino o una purga durante la lettura non deve far riapparire un record gia' ripristinato, ne' far perdere gli altri.
/// </summary>
public sealed class RecoveryBinCaricamentoConcorrenteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_RecoveryConc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState Stato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        const string nome = "Operatore Master Recovery";
        state.Master.Operatori.Save(new List<Operatore>
        {
            TestOperatori.Sigilla(new Operatore { Nome = nome, Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") })
        });
        Assert.True(state.ImpostaOperatore(nome, "1234"));
        return state;
    }

    /// <summary>Crea n NC, le rimuove archiviandole nel cestino e restituisce i RecoveryId in ordine di creazione.</summary>
    private static Guid[] CreaRecord(AppState state, int n)
    {
        var ids = new Guid[n];
        for (var i = 0; i < n; i++)
        {
            var id = $"NC-REC-{i:D3}";
            var source = state.Paths.NonConformitaFile(id);
            File.WriteAllText(source, "{\"Uid\":\"" + Guid.NewGuid() + "\",\"Id\":\"" + id + "\",\"RevisioneDati\":1}");
            ids[i] = DomainRecoveryService.ArchiveAndDelete(state.Paths, source, DomainRecoveryCategory.NonConformita, "Master").RecoveryId;
        }
        return ids;
    }

    [Fact]
    public async Task RipristinoDuranteLaLettura_NonRiappareERonPerdeNulla()
    {
        using var state = Stato();
        var ids = CreaRecord(state, 5);
        var vm = new RecoveryBinViewModel(state);
        Assert.Equal(5, vm.Totale);
        Assert.Equal(5, vm.Disponibili);

        using var rilascio = new ManualResetEventSlim(false);
        vm.ForzaBackground = true;
        vm.GateLetturaPerTest = () => rilascio.Wait(TimeSpan.FromSeconds(30));

        vm.AggiornaCommand.Execute(null);                                        // lettura superata: si ferma prima dell'applicazione
        DomainRecoveryService.Restore(state.Paths, ids[2], "Master");   // ripristino concorrente, come dalla UI
        vm.AggiornaCommand.Execute(null);                                        // lettura successiva al ripristino
        rilascio.Set();
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(5, vm.Totale);                           // nessun record perso
        Assert.Equal(4, vm.Disponibili);                      // il ripristinato non risulta piu' disponibile
        Assert.True(vm.Elementi.Single(x => x.RecoveryId == ids[2]).Ripristinato);
        Assert.True(vm.LettureScartate >= 1);                 // la lettura superata non e' stata applicata
        Assert.False(vm.CaricamentoInCorso);
    }

    [Fact]
    public async Task LetturaInBackground_UgualeAllaLetturaInLinea_ConERipristinati()
    {
        using var state = Stato();
        var ids = CreaRecord(state, 6);
        DomainRecoveryService.Restore(state.Paths, ids[1], "Master");
        DomainRecoveryService.Restore(state.Paths, ids[4], "Master");

        var inLinea = new RecoveryBinViewModel(state);
        var attesi = inLinea.Elementi.Select(x => x.RecoveryId).ToArray();
        var attesiDisponibili = inLinea.Elementi.Where(x => !x.Ripristinato).Select(x => x.RecoveryId).ToArray();

        var inBackground = new RecoveryBinViewModel(state) { ForzaBackground = true };
        inBackground.AggiornaCommand.Execute(null);
        await inBackground.AttendiAggiornamentoAsync();
        Assert.Equal(attesi, inBackground.Elementi.Select(x => x.RecoveryId));

        inBackground.MostraGiaRipristinati = false;           // il filtro passa dal ricaricamento in background
        await inBackground.AttendiAggiornamentoAsync();
        Assert.Equal(attesiDisponibili, inBackground.Elementi.Select(x => x.RecoveryId));
        Assert.Equal(4, inBackground.Totale);
        Assert.False(inBackground.CaricamentoInCorso);
    }

    [Fact]
    public void LetturaInLinea_NessunaIndicazioneDiCaricamentoRestaAccesa()
    {
        using var state = Stato();
        CreaRecord(state, 2);
        var vm = new RecoveryBinViewModel(state);

        Assert.Equal(2, vm.Totale);
        Assert.False(vm.CaricamentoInCorso);
        Assert.Equal(2, vm.Elementi.Count);
    }
}
