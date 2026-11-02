using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// P2.10: Unisci in corso e modifica in anagrafica. La modifica non parte mentre Unisci (o Rinomina/Dividi) e' in
/// esecuzione. Una ricarica arrivata durante la modifica non sostituisce le righe digitate: si conservano, si avvisa
/// che i dati sono cambiati e la lista aggiornata arriva a fine modifica.
/// </summary>
[Collection("Localization")]
public sealed class MasterDataEditorUnioneInCorsoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterUnioneInCorso_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _handlerOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _handlerOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static string T(string chiave) => LocalizationService.Instance.Traduci(chiave);

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Unione", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(op);
        state.Master.Operatori.Save(ops);
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    /// <summary>Invoca la ricarica privata dell'editor: e' la stessa callback registrata su
    /// Unione.OperazioneCompletata, che dall'esterno non si puo' sollevare.</summary>
    private static void RicaricaCome(MasterDataEditorViewModelBase<Componente> vm) =>
        typeof(MasterDataEditorViewModelBase<Componente>)
            .GetMethod("Ricarica", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null);

    [Fact]
    public void UnioneInCorso_IniziaModificaRifiutataFinoAlTermine()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var vm = new EcuViewModel(state);

        // Unisci in esecuzione (IsBusy): la modifica non parte e l'editor lo dice.
        vm.Unione.IsBusy = true;
        vm.IniziaModificaCommand.AttendiModifica();

        Assert.False(vm.InModifica);
        Assert.Equal(T("MasterUnioneInCorsoModificaRifiutata"), vm.MessaggioErrore);

        // Unisci concluso: la stessa azione ora parte.
        vm.Unione.IsBusy = false;
        vm.IniziaModificaCommand.AttendiModifica();

        Assert.True(vm.InModifica);
        Assert.Null(vm.MessaggioErrore);
        vm.AnnullaCommand.Execute(null);
        Assert.False(vm.InModifica);
    }

    [Fact]
    public void RinominaDiretta_DellaVoceEsistenteVieneBloccataMaLeNuoveRimangonoNominabili()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" } });
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();

        var esistente = Assert.Single(vm.Items);
        Assert.True(vm.Unione.Disponibile);
        Assert.True(vm.BloccaRinominaDiretta(esistente, "Nome"));
        Assert.Equal("BCM", esistente.Nome);
        Assert.Contains("Rinomina", vm.MessaggioErrore, StringComparison.OrdinalIgnoreCase);

        // Gli altri campi restano modificabili e una voce nuova va ancora nominata prima del primo Salva.
        Assert.False(vm.BloccaRinominaDiretta(esistente, "VeicoliAssociati"));
        vm.NuovoCommand.Execute(null);
        var nuova = Assert.Single(vm.Items.Where(e => e.Nome.Length == 0));
        Assert.False(vm.BloccaRinominaDiretta(nuova, "Nome"));
    }

    [Fact]
    public void RicaricaDuranteLaModifica_ConservaLeRigheDigitateEAvvisa()
    {
        var state = CreaStato();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "BCM" }, new() { Nome = "ECM" } });
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        using var vm = new EcuViewModel(state);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.NuovoCommand.Execute(null);
        vm.Items.Last().Nome = "NUOVA-LOCALE";

        RicaricaCome(vm);

        Assert.True(vm.InModifica);
        Assert.Contains(vm.Items, e => e.Nome == "NUOVA-LOCALE");
        Assert.Equal(T("MasterDatiAggiornatiDuranteModifica"), vm.Messaggio);

        // Fine modifica: la ricarica riporta la lista dal disco e chiude l'avviso.
        vm.AnnullaCommand.Execute(null);
        Assert.False(vm.InModifica);
        Assert.DoesNotContain(vm.Items, e => e.Nome == "NUOVA-LOCALE");
        Assert.NotEqual(T("MasterDatiAggiornatiDuranteModifica"), vm.Messaggio);
    }
}
