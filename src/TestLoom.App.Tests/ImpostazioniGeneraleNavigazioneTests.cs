using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using static TestLoom.App.Tests.NavigazioneTestHelper;

namespace TestLoom.App.Tests;

/// <summary>Banner cliccabili "File con problemi"/"ID duplicati" (Addendum: audit UX 2026-08-26, punto 7):
/// MainViewModel.VaiAImpostazioniGeneraleCommand deve portare sempre su Impostazioni → Generale, anche quando
/// l'utente ha personalizzato l'ordine delle sotto-sezioni tanto da spostare "Generale" fuori dalla prima
/// posizione — e non deve mai scartare in silenzio un editor aperto altrove con modifiche non salvate.</summary>
/// <remarks>[Collection("Localization")]: muta ConfirmService.Handler, statico e condiviso da tutto il
/// processo — va serializzato con ogni altro test che tocca lo stesso Handler.</remarks>
[Collection("Localization")]
public sealed class ImpostazioniGeneraleNavigazioneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ImpGenerale_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private AppState CreateMasterState()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Master Test", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234"), ModificatoDa = "Test" });
        state.Master.Operatori.Save(new List<Operatore> { op });
        state.ImpostaOperatore("Master Test", "1234");
        return state;
    }

    [Fact]
    public async Task VaiAImpostazioniGenerale_AtterraSuGenerale()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);
        await NavigaAsync(main, "home");

        main.VaiAImpostazioniGeneraleCommand.Execute(null);
        await AttendiCaricamentoAsync(main);

        var pagina = Assert.IsType<ImpostazioniViewModel>(main.CurrentPage);
        Assert.True(pagina.EGenerale);
    }

    [Fact]
    public async Task VaiAImpostazioniGenerale_AtterraSuGenerale_AncheConOrdineSottoSezioniPersonalizzato()
    {
        var state = CreateMasterState();
        // L'utente ha spostato "operatori" davanti a "generale" nell'ordine salvato delle sotto-sezioni.
        state.Config.OrdineImpostazioni = new List<string> { "operatori", "generale", "archivio" };
        var main = new MainViewModel(state);

        main.VaiAImpostazioniGeneraleCommand.Execute(null);
        await AttendiCaricamentoAsync(main);

        var pagina = Assert.IsType<ImpostazioniViewModel>(main.CurrentPage);
        Assert.True(pagina.EGenerale);
        Assert.Equal("generale", pagina.SottoSezioneSelezionata?.Id);
    }

    [Fact]
    public async Task VaiAImpostazioniGenerale_ConEditorTestAperto_ChiedeConfermaENonScartaInSilenzio()
    {
        var state = CreateMasterState();
        var main = new MainViewModel(state);
        await NavigaAsync(main, "test");
        var editor = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });
        editor.SceglieTemplateCommand.Execute(editor.TemplatesDisponibili[0]);
        editor.ModificheNonSalvate = true; // simula modifiche non ancora salvate, come le rileva HaModificheNonSalvate()
        main.CurrentPage = editor;

        var confermaChiesta = false;
        ConfirmService.Handler = (_, _, _, _) => { confermaChiesta = true; return Task.FromResult(false); }; // utente annulla

        main.VaiAImpostazioniGeneraleCommand.Execute(null);

        Assert.True(confermaChiesta);
        // Annullando la conferma, l'editor con le modifiche deve restare la pagina corrente: nessuna perdita silenziosa.
        Assert.Same(editor, main.CurrentPage);
    }
}
