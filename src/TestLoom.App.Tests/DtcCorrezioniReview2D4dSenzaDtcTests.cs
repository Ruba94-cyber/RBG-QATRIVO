using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

public sealed partial class DtcCorrezioniReview2EditorTests
{
    // ---- 8a) Extra senza guasto: dal editor Requisiti al Test ---------------------------------------------------------------

    [Fact]
    public async Task D4dSenzaDtc_CreatoDallEditorRequisiti_Salvato_Ricaricato_Collegato_EditorTestSenzaErroriESenzaSelettoreDtc()
    {
        var state = CreaStato();
        using (var extra = new RequisitiViewModel(state, TipoRequisito.Extra))
        {
            extra.IniziaModificaCommand.AttendiModifica();
            extra.NuovoCommand.Execute(null);
            extra.ElementoSelezionato!.Id = "D9"; extra.ElementoSelezionato.Testo = "requisito senza guasto";
            await extra.SalvaCommand.ExecuteAsync(null);
            Assert.True(string.IsNullOrEmpty(extra.MessaggioErrore));
        }
        var ricaricato = state.Master.Requisiti.Load().Single(r => r.Id == "D9");     // riletto dal JSON, non dalla copia in memoria
        Assert.Equal(TipoRequisito.Extra, RequisitoApplicabilita.TipoDi(ricaricato));
        Assert.Null(ricaricato.Guasto);
        Assert.Equal(0, ricaricato.DtcTotale);

        var vm = Apri(state, Test(new[] { "D9" }, new[] { "F1" }));
        Assert.Empty(vm.ErroriValidazione);
        Assert.False(vm.HaRequisitoD4dConDtc);
        Assert.False(vm.MostraDtcOperazione);                      // nessun selettore guasto: niente guasto nel requisito e niente guasto Injection
        Assert.False(vm.DtcMostraCombo);
        Assert.False(vm.DtcMostraPicker);
        Assert.Empty(vm.DtcOpzioniOperazione);
        Assert.False(vm.MostraBloccoAggiungiDtc);
        await vm.SalvaSicuroCommand.ExecuteAsync(null);           // il salvataggio del test non lamenta guasto mancanti
        Assert.DoesNotContain(vm.ErroriValidazione, e => e.Contains("Guasto", StringComparison.OrdinalIgnoreCase));
        vm.Dispose();
    }
}
