using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>P4 (1): l'errore UI non gestito mostra nella shell un avviso non bloccante con il percorso del crash.log, che
/// l'utente può chiudere senza che l'app si fermi.</summary>
[Collection("CrashLogUi")]
public sealed class MainViewModelErroreUiTests
{
    [Fact]
    public void ErroreUiNonGestito_MostraLAvvisoConIlPercorsoDelCrashLog_EPuoEssereChiuso()
    {
        CrashLogService.AzzeraLimiteAvvisoErroreUi();
        var state = TestStateFactory.Create();
        var vm = new MainViewModel(state);
        try
        {
            Assert.False(vm.MostraErroreUi);

            CrashLogService.GestisciErroreUi(new InvalidOperationException("prova avviso"), DateTime.UtcNow);

            Assert.True(vm.MostraErroreUi);
            Assert.Equal(Path.Combine(CrashLogService.LogDirectory, "crash.log"), vm.PercorsoLogErroreUi);

            vm.ChiudiErroreUiCommand.Execute(null);
            Assert.False(vm.MostraErroreUi);
        }
        finally
        {
            vm.Dispose();
            CrashLogService.AzzeraLimiteAvvisoErroreUi();
        }
    }
}
