using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>P2.1: un rinnovo non confermato non significa lock perso. Solo dopo la soglia l'avviso diventa persistente,
/// finché un rinnovo non torna confermato o il lock cambia detentore.</summary>
public sealed class MonitorRinnovoLockTests
{
    private static LockEnsureResult NonConfermato() => new(false, null, false);

    private static LockEnsureResult Confermato() => new(true, null, false, Confermato: true);

    private static LockEnsureResult DetenutoDaAltri() => new(false, new LockInfo { Operatore = "Luca" }, false);

    [Fact]
    public void RinnoviNonConfermatiSottoSoglia_NonMostranoAvviso()
    {
        var monitor = new MonitorRinnovoLock();

        for (var i = 1; i < MonitorRinnovoLock.SogliaAvviso; i++)
            Assert.Equal(EsitoRinnovoLock.NonConfermato, monitor.Registra(NonConfermato()));

        Assert.False(monitor.AvvisoAttivo);
    }

    [Fact]
    public void RinnoviNonConfermatiDiSeguitoOltreLaSoglia_MostranoUnAvvisoPersistente()
    {
        var monitor = new MonitorRinnovoLock();
        for (var i = 0; i < MonitorRinnovoLock.SogliaAvviso; i++) monitor.Registra(NonConfermato());

        Assert.True(monitor.AvvisoAttivo);
        monitor.Registra(NonConfermato());
        Assert.True(monitor.AvvisoAttivo);
    }

    [Fact]
    public void RinnovoConfermato_AzzeraConteggioEAvviso()
    {
        var monitor = new MonitorRinnovoLock();
        for (var i = 0; i < MonitorRinnovoLock.SogliaAvviso; i++) monitor.Registra(NonConfermato());

        Assert.Equal(EsitoRinnovoLock.Confermato, monitor.Registra(Confermato()));
        Assert.False(monitor.AvvisoAttivo);
        Assert.Equal(0, monitor.NonConfermatiConsecutivi);
    }

    [Fact]
    public void LockDiAltroDetentore_NonEUnRinnovoNonConfermato_EAzzeraIlConteggio()
    {
        var monitor = new MonitorRinnovoLock();
        monitor.Registra(NonConfermato());

        Assert.Equal(EsitoRinnovoLock.NonPosseduto, monitor.Registra(DetenutoDaAltri()));
        Assert.Equal(0, monitor.NonConfermatiConsecutivi);
    }
}
