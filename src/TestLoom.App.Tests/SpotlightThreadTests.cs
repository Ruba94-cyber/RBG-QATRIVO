using Avalonia.Controls;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Il registro statico di Spotlight non deve toccare controlli di un altro thread: un test che creava
/// una View con Spotlight.TargetId su un thread faceva fallire (a seconda dell'ordine) un test successivo che
/// costruiva MainViewModel su un altro thread ("The calling thread cannot access this object").</summary>
public sealed class SpotlightThreadTests
{
    [Fact]
    public void TargetId_ImpostatoDaAltroThread_NonToccaIControlliDiUnThreadDiverso()
    {
        Button? creatoAltrove = null;
        var thread = new Thread(() =>
        {
            creatoAltrove = new Button();
            Spotlight.SetTargetId(creatoAltrove, "spotlight-thread-test");
        });
        thread.Start();
        thread.Join();
        Assert.NotNull(creatoAltrove);

        try
        {
            var eccezione = Record.Exception(() => Spotlight.TargetId = "spotlight-thread-test");
            Assert.Null(eccezione);
        }
        finally
        {
            Spotlight.TargetId = null;
        }
        GC.KeepAlive(creatoAltrove);
    }
}
