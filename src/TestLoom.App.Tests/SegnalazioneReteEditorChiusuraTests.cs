using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Editor della segnalazione di rete aperto da "Apri": sola lettura finche' non si preme Modifica, Conclusa
/// fuori dalla combo Stato (si chiude solo col pulsante Chiudi, con motivo) e riapertura con motivo.</summary>
[Collection("Localization")]
public sealed class SegnalazioneReteEditorChiusuraTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_SegReteChiusura_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Operatore Rete", Ruolo = RuoloOperatore.Standard, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-RETE-1", DaSegnalazioneRete = true, Owner = "Operatore Rete", Descrizione = "Segnalazione dalla rete",
            Severita = SeveritaAnomalia.Medium, StatoWorkflow = StatoAnomalia.Confermata, DataApertura = DateTime.Now,
        });
        return state;
    }

    private static SegnalazioneReteEditorViewModel Apri(AppState state, Action? torna = null) =>
        new(state, "NC-RETE-1", torna ?? (() => { }));

    [Fact]
    public void ComboStato_NonOffreConclusa_PerUnaNcAperta()
    {
        var vm = Apri(CreaStato());

        Assert.DoesNotContain(StatoAnomalia.Conclusa, vm.StatiDisponibili.Cast<StatoAnomalia>());
        Assert.True(vm.PuoCambiareStato);
    }

    [Fact]
    public void ApertaInSolaLettura_NonScriveNeChiudeFinoAModifica()
    {
        var vm = Apri(CreaStato());
        vm.ImpostaSolaLetturaIniziale(null);

        Assert.False(vm.InModifica);
        Assert.False(vm.PuoModificare);
        Assert.False(vm.PuoSalvare);
        Assert.False(vm.PuoChiudere);
        Assert.True(vm.PuoEntrareInModifica);

        vm.EntraInModificaCommand.Execute(null);

        Assert.True(vm.PuoModificare);
        Assert.True(vm.PuoChiudere);
        Assert.False(vm.PuoEntrareInModifica);
    }

    [Fact]
    public void EntraInModifica_ConCallback_DelegaLaNavigazioneENonCambiaLEditorCorrente()
    {
        var vm = Apri(CreaStato());
        var richieste = 0;
        vm.ImpostaSolaLetturaIniziale(() => richieste++);

        vm.EntraInModificaCommand.Execute(null);

        Assert.Equal(1, richieste);
        Assert.False(vm.PuoModificare);
    }

    [Fact]
    public async Task Chiudi_SenzaMotivo_MostraErroreENonChiude()
    {
        var state = CreaStato();
        var tornate = 0;
        var vm = Apri(state, () => tornate++);
        vm.MotivoChiusura = "  ";

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrEmpty(vm.MessaggioErrore));
        Assert.Equal(0, tornate);
        Assert.Equal(StatoAnomalia.Confermata, state.NonConformita.GetById("NC-RETE-1")!.StatoWorkflow);
    }

    [Fact]
    public async Task Chiudi_ConMotivo_ConcludeLaNcETornaAllaLista()
    {
        var state = CreaStato();
        var tornate = 0;
        var vm = Apri(state, () => tornate++);
        vm.MotivoChiusura = "Risolta con la nuova release";

        await vm.ChiudiCommand.ExecuteAsync(null);

        Assert.Equal(1, tornate);
        var chiusa = state.NonConformita.GetById("NC-RETE-1")!;
        Assert.Equal(StatoAnomalia.Conclusa, chiusa.StatoWorkflow);
        Assert.Equal("Risolta con la nuova release", chiusa.MotivoChiusura);
        Assert.True(chiusa.DaSegnalazioneRete);
        Assert.Contains(chiusa.Changelog, c => c.Campo == "Chiusura");
    }

    [Fact]
    public async Task NcChiusa_RiapriRichiedeIlMotivoEIlComboStatoEBloccato()
    {
        var state = CreaStato();
        var chiusura = Apri(state);
        chiusura.MotivoChiusura = "ok";
        await chiusura.ChiudiCommand.ExecuteAsync(null);

        var vm = Apri(state);
        Assert.False(vm.PuoCambiareStato);
        Assert.True(vm.PuoRiaprire);
        Assert.False(vm.PuoChiudere);

        vm.MotivoRiapertura = "";
        await vm.RiapriCommand.ExecuteAsync(null);
        Assert.False(string.IsNullOrEmpty(vm.MessaggioErrore));
        Assert.Equal(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-RETE-1")!.StatoWorkflow);

        vm.MotivoRiapertura = "Si ripresenta";
        await vm.RiapriCommand.ExecuteAsync(null);

        Assert.NotEqual(StatoAnomalia.Conclusa, state.NonConformita.GetById("NC-RETE-1")!.StatoWorkflow);
    }

    [Fact]
    public void Chiudi_NonDisponibileConModificheNonSalvate()
    {
        var vm = Apri(CreaStato());
        Assert.True(vm.PuoChiudere);

        vm.Owner = "Qualcun altro";

        Assert.True(vm.ModificheNonSalvate);
        Assert.False(vm.PuoChiudere);
    }
}
