using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Regressione G2 (6.4.5): dopo "Carica demo completa" su cartella vuota il messaggio di esito e
/// l'elenco operatori devono essere subito disponibili e notificati alla UI, senza premere ricarica.</summary>
public sealed class FirstRunDemoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_firstrun_demo_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CaricaDemoCompleta_PubblicaMessaggioEOperatoriSenzaRicaricaManuale()
    {
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        var vm = new FirstRunViewModel(state, () => { });
        vm.Cartella = _root;
        // 6.6.1 (correzione 1): la cartella vuota e gli operatori si leggono fuori dal thread UI: si attende l'esito di fondo.
        Assert.True(AttendiFinche(() => vm.PuoInizializzareDemo, TimeSpan.FromSeconds(10)));
        var notificate = new HashSet<string>();
        vm.PropertyChanged += (_, e) => notificate.Add(e.PropertyName ?? "");

        await vm.CaricaDemoCompletaCommand.ExecuteAsync(null);

        Assert.True(vm.MostraMessaggioDemo, vm.MessaggioErrore);
        Assert.True(AttendiFinche(() => vm.OperatoriDisponibili.Count > 0, TimeSpan.FromSeconds(10)));
        Assert.NotEmpty(vm.OperatoriDisponibili);
        Assert.False(vm.EPrimoAccesso);
        Assert.Equal("Operatore Prova A", vm.OperatoreSelezionato);
        Assert.Contains(nameof(FirstRunViewModel.OperatoriDisponibili), notificate);
        Assert.Contains(nameof(FirstRunViewModel.MostraMessaggioDemo), notificate);
        Assert.Contains(nameof(FirstRunViewModel.EPrimoAccesso), notificate);
    }

    /// <summary>Attende, con tetto, che una condizione diventi vera (esito di letture di fondo della schermata).</summary>
    private static bool AttendiFinche(Func<bool> condizione, TimeSpan tetto)
    {
        var scadenza = DateTime.UtcNow + tetto;
        while (DateTime.UtcNow < scadenza)
        {
            if (condizione()) return true;
            Thread.Sleep(25);
        }
        return condizione();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
