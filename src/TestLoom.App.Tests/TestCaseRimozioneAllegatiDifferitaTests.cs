using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Rimozione differita degli allegati sull'editor Test: stessa regola di CR e segnalazione. Il binario
/// lascia il percorso attivo solo dopo un Salva riuscito in cui il Test non lo referenzia piu'.</summary>
[Collection("Localization")]
public sealed class TestCaseRimozioneAllegatiDifferitaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_TestCase_RimozioneAllegati_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore("Mario Rossi", "1234");
        return state;
    }

    /// <summary>Test nuovo con i dati minimi che il Salva accetta (stessi del fixture di robustezza del Test).</summary>
    private static TestCaseEditorViewModel NuovoTestValido(AppState state)
    {
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";
        return vm;
    }

    private string Sorgente(string nome)
    {
        var file = Path.Combine(_dir, nome);
        File.WriteAllText(file, "evidenza test " + nome);
        return file;
    }

    [Fact]
    public async Task RimozioneAllegato_SalvataggioRiuscito_SpostaIlBinarioInRecoveryEAggiornaIlTest()
    {
        var state = CreaState();
        var vm = NuovoTestValido(state);
        await vm.AggiungiAllegatoSicuro(Sorgente("nuovo.txt"));
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.CampiObbligatoriMancanti);
        var id = vm.TestCase.Id;
        var allegato = vm.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, vm.TestCase, allegato);
        Assert.True(File.Exists(attivo));

        vm.RimuoviAllegatoCommand.Execute(allegato);
        Assert.True(File.Exists(attivo)); // finche' non si salva il binario resta nel percorso attivo

        await vm.SalvaCommand.ExecuteAsync(null);

        Assert.Empty(vm.CampiObbligatoriMancanti);
        Assert.False(File.Exists(attivo));
        Assert.True(File.Exists(AttachmentService.PercorsoCompleto(state.Paths, vm.TestCase, allegato)));
        Assert.Empty(state.TestCases.GetById(id)!.Allegati);
    }

    [Fact]
    public async Task RimozioneAllegato_AbbandonoConfermato_LasciaFileERecordInvariati()
    {
        var state = CreaState();
        var vm = NuovoTestValido(state);
        await vm.AggiungiAllegatoSicuro(Sorgente("persistito.txt"));
        await vm.SalvaCommand.ExecuteAsync(null); // Test e allegato entrano nel record persistito
        Assert.Empty(vm.CampiObbligatoriMancanti);
        var id = vm.TestCase.Id;
        var allegato = vm.AllegatiVisual.Single();
        var attivo = AttachmentService.PercorsoCompleto(state.Paths, vm.TestCase, allegato);
        Assert.True(File.Exists(attivo));

        vm.RimuoviAllegatoCommand.Execute(allegato);
        ((IConfirmedAbandonmentParticipant)vm).PrepareConfirmedAbandonmentCleanup(); // abbandono confermato
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.True(File.Exists(attivo));
        Assert.Single(state.TestCases.GetById(id)!.Allegati);
    }

    /// <summary>Regressione: IsNew resta vero dopo il primo Salva. Un allegato gia' persistito non deve essere
    /// trattato come "aggiunta mai salvata" quando, dopo il Salva, si abbandona un'ulteriore modifica.</summary>
    [Fact]
    public async Task AllegatoGiaPersistito_AbbandonoDopoIlSalvataggio_NonVienCancellato()
    {
        var state = CreaState();
        var vm = NuovoTestValido(state);
        await vm.AggiungiAllegatoSicuro(Sorgente("salvato.txt"));
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.CampiObbligatoriMancanti);
        var id = vm.TestCase.Id;
        var salvato = vm.AllegatiVisual[0];
        var attivoSalvato = AttachmentService.PercorsoCompleto(state.Paths, vm.TestCase, salvato);

        await vm.AggiungiAllegatoSicuro(Sorgente("solo-bozza.txt")); // modifica non salvata
        var bozza = vm.AllegatiVisual[1];
        var attivoBozza = AttachmentService.PercorsoCompleto(state.Paths, vm.TestCase, bozza);
        ((IConfirmedAbandonmentParticipant)vm).PrepareConfirmedAbandonmentCleanup(); // abbandono confermato
        await AttachmentRemovalTestSupport.AttendiCallbackAbbandono();

        Assert.True(File.Exists(attivoSalvato));
        Assert.Equal("evidenza test salvato.txt", File.ReadAllText(attivoSalvato));
        Assert.False(File.Exists(attivoBozza)); // l'aggiunta mai salvata viene ripulita, come nel Test esistente
        Assert.Single(state.TestCases.GetById(id)!.Allegati);
    }
}
