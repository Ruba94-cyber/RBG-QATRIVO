using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre la richiesta utente 2026-08-21: "nei test suggeriti... aprire il test in visualizzazione...
/// in una finestra separata dedicata... la richiesta di modifica comporterebbe la chiusura del test
/// attualmente aperto... confermato da pop-up dedicato". CreaVisualizzatoreCorrelato/RichiediModificaCorrelato
/// sono la parte orchestrata in ViewModel, testabile senza la Window Avalonia vera e propria.</summary>
[Collection("Localization")]
public sealed class TestCorrelatoViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestCorrelatoViewer_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private (AppState State, TestCase Principale, TestCase Correlato) CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        var principale = new TestCase { Id = "TC-A", Componente = "BCM", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        var correlato = new TestCase { Id = "TC-B", Componente = "BCM", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        state.TestCases.Save(principale, isNew: true);
        state.TestCases.Save(correlato, isNew: true);
        return (state, principale, correlato);
    }

    [Fact]
    public void CreaVisualizzatoreCorrelato_ApreInSolaVisualizzazioneSenzaLock()
    {
        var (state, principale, correlato) = CreaStato();
        var vmPrincipale = new TestCaseEditorViewModel(state, principale.Id, () => { }, _ => { });

        var vmViewer = vmPrincipale.CreaVisualizzatoreCorrelato(correlato.Id, () => Task.CompletedTask);

        Assert.False(vmViewer.InModifica);
        Assert.Equal(correlato.Id, vmViewer.TestCase.Id);
        Assert.Null(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(correlato)));
    }

    [Fact]
    public void IniziaModifica_SulVisualizzatore_ChiamaLoScavalcoInveceDiAcquisireIlLock()
    {
        var (state, principale, correlato) = CreaStato();
        var vmPrincipale = new TestCaseEditorViewModel(state, principale.Id, () => { }, _ => { });
        var invocato = false;
        var vmViewer = vmPrincipale.CreaVisualizzatoreCorrelato(correlato.Id, () => { invocato = true; return Task.CompletedTask; });

        vmViewer.IniziaModificaCommand.AttendiModifica();

        Assert.True(invocato);
        Assert.False(vmViewer.InModifica);
        Assert.Null(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(correlato)));
    }

    [Fact]
    public async Task RichiediModificaCorrelato_RilasciaIlLockDelPrincipaleEInvocaLaNavigazione()
    {
        var (state, principale, correlato) = CreaStato();
        string? richiesto = null;
        var vmPrincipale = new TestCaseEditorViewModel(state, principale.Id, () => { }, _ => { }, navigaATestInModifica: id => richiesto = id);
        vmPrincipale.IniziaModificaCommand.AttendiModifica();
        Assert.True(vmPrincipale.InModifica);
        Assert.NotNull(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(principale)));

        var esito = await vmPrincipale.RichiediModificaCorrelato(correlato.Id);

        Assert.True(esito);
        Assert.Equal(correlato.Id, richiesto);
        Assert.Null(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(principale)));
    }

    [Fact]
    public async Task RichiediModificaCorrelato_SenzaCallbackDiNavigazione_NonFaNiente()
    {
        var (state, principale, correlato) = CreaStato();
        var vmPrincipale = new TestCaseEditorViewModel(state, principale.Id, () => { }, _ => { });
        vmPrincipale.IniziaModificaCommand.AttendiModifica();

        var esito = await vmPrincipale.RichiediModificaCorrelato(correlato.Id);

        Assert.False(esito);
        Assert.NotNull(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(principale)));
    }

    /// <summary>Copre il bug scoperto durante l'analisi di questa funzionalità: prima Dispose() non rilasciava
    /// mai il lock, quindi abbandonare le modifiche dalla barra laterale (che scarta la pagina uscente con
    /// ImpostaCurrentPage, senza passare da Annulla) lasciava il file .lock orfano fino al timeout di 20
    /// minuti.</summary>
    [Fact]
    public async Task Dispose_RilasciaIlLockSeAncoraInModifica()
    {
        var (state, principale, _) = CreaStato();
        var vm = new TestCaseEditorViewModel(state, principale.Id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        var percorso = state.TestCases.PercorsoPer(principale);
        Assert.NotNull(state.Locks.GetActiveLock(percorso));

        vm.Dispose();
        await Assert.IsAssignableFrom<Task>(vm.RilascioLockDopoDisposePerTest)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(state.Locks.GetActiveLock(percorso));
    }

    [Fact]
    public void Dispose_NonRilasciaNienteSeMaiEntratoInModifica()
    {
        var (state, principale, _) = CreaStato();
        var vm = new TestCaseEditorViewModel(state, principale.Id, () => { }, _ => { });

        vm.Dispose(); // non deve lanciare né toccare un lock mai acquisito

        Assert.Null(state.Locks.GetActiveLock(state.TestCases.PercorsoPer(principale)));
    }
}
