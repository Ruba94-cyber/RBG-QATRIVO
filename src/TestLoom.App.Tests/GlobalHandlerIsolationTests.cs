using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// I dialoghi di due ViewModel non devono leggere l'handler installato da un'altra classe di test.
/// Il ripristino in finally e' necessario ma, da solo, non serializza due test xunit concorrenti.
/// </summary>
[Collection("Localization")]
public sealed class GlobalHandlerIsolationFirstTests
{
    [Fact]
    public async Task HandlerDiConferma_RestaQuelloDellaPropriaClasse()
    {
        var originale = ConfirmService.Handler;
        Func<string, string, string, string, Task<bool>> mio = (_, _, _, _) => Task.FromResult(true);
        try
        {
            ConfirmService.Handler = mio;
            await Task.Delay(300);
            Assert.Same(mio, ConfirmService.Handler);
        }
        finally { ConfirmService.Handler = originale; }
    }
}

[Collection("Localization")]
public sealed class GlobalHandlerIsolationSecondTests
{
    [Fact]
    public async Task HandlerDiConferma_RestaQuelloDellaPropriaClasse()
    {
        var originale = ConfirmService.Handler;
        Func<string, string, string, string, Task<bool>> mio = (_, _, _, _) => Task.FromResult(false);
        try
        {
            ConfirmService.Handler = mio;
            await Task.Delay(300);
            Assert.Same(mio, ConfirmService.Handler);
        }
        finally { ConfirmService.Handler = originale; }
    }
}
