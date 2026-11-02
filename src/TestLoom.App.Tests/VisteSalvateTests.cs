using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Viste salvate sulla lista Test (Addendum: audit UX 2026-08-26, punto 3): una combinazione di
/// filtri ricorrente salvata una volta e riapplicata con un clic. Verifica il ciclo completo — cattura,
/// persistenza su disco (stesso AppConfigService del resto delle preferenze locali), riapplicazione dopo un
/// riavvio simulato, sovrascrittura per nome, eliminazione — non solo lo stato in memoria.</summary>
/// <remarks>[Collection("Localization")]: mutano TextEditService.Handler/ConfirmService.Handler, statici e
/// condivisi da tutto il processo — vanno serializzati con ogni altro test che tocca gli stessi Handler,
/// stesso motivo di TestCaseListBulkActionsTests.</remarks>
[Collection("Localization")]
public sealed class VisteSalvateTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_VisteSalvate_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, Task<string?>>? _textEditOriginale = TextEditService.Handler;
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        TextEditService.Handler = _textEditOriginale;
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_configDir)) { try { Directory.Delete(_configDir, true); } catch { } }
    }

    private AppState NuovoStato() => new(new AppConfigService(Path.Combine(_configDir, "config.json")));

    [Fact]
    public async Task SalvaVista_CatturaIFiltriCorrentiESopravviveAUnRicaricamentoDelConfigDaDisco()
    {
        var state = NuovoStato();
        var vm = new TestCaseListViewModel(state, _ => { });
        vm.FiltroTesto = "abc123";
        vm.FiltroEsito.Seleziona(nameof(Esito.NonConforme));
        vm.FiltroStatoLavoro.Seleziona(nameof(StatoLavoroTest.InCorso));
        vm.FiltroDataDa = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("La mia vista");

        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);

        Assert.Contains(vm.VisteSalvate, v => v.Nome == "La mia vista");

        // Simula la riapertura dell'app: un secondo AppState che rilegge lo stesso file di config da disco,
        // non la stessa istanza in memoria.
        var stateRiavviato = NuovoStato();
        var vmRiavviato = new TestCaseListViewModel(stateRiavviato, _ => { });
        var vistaRiletta = Assert.Single(vmRiavviato.VisteSalvate);
        Assert.Equal("La mia vista", vistaRiletta.Nome);

        vmRiavviato.VistaSelezionata = vistaRiletta;
        Assert.Equal("abc123", vmRiavviato.FiltroTesto);
        Assert.Equal(new[] { nameof(Esito.NonConforme) }, vmRiavviato.FiltroEsito.Selezionati);
        Assert.Equal(new[] { nameof(StatoLavoroTest.InCorso) }, vmRiavviato.FiltroStatoLavoro.Selezionati);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), vmRiavviato.FiltroDataDa);
    }

    [Fact]
    public async Task SalvaVista_ConLoStessoNome_Sovrascrive_NonDuplica()
    {
        var state = NuovoStato();
        var vm = new TestCaseListViewModel(state, _ => { });
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Vista X");

        vm.FiltroTesto = "primo";
        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);
        vm.FiltroTesto = "secondo";
        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);

        var vista = Assert.Single(vm.VisteSalvate);
        Assert.Equal("secondo", vista.Valori["Testo"]);
    }

    [Fact]
    public async Task SalvaVista_FallimentoMostraBanner_EIlRetryRiuscitoLoPulisce()
    {
        var vm = new TestCaseListViewModel(NuovoStato(), _ => { });
        var marcatore = "errore vista " + Guid.NewGuid().ToString("N");
        TextEditService.Handler = (_, _) => Task.FromException<string?>(new IOException(marcatore));

        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);

        Assert.True(vm.HaMessaggioErrore);
        Assert.Contains(marcatore, vm.MessaggioErrore);

        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Vista recuperata");
        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);

        Assert.False(vm.HaMessaggioErrore);
        Assert.Null(vm.MessaggioErrore);
        Assert.Contains(vm.VisteSalvate, vista => vista.Nome == "Vista recuperata");
    }

    [Fact]
    public async Task EliminaVistaSelezionata_LaRimuoveDallaListaEDalConfigPersistito()
    {
        var state = NuovoStato();
        var vm = new TestCaseListViewModel(state, _ => { });
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Da eliminare");
        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);
        vm.VistaSelezionata = vm.VisteSalvate.Single();
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        await vm.EliminaVistaSelezionataCommand.ExecuteAsync(null);

        Assert.Empty(vm.VisteSalvate);
        Assert.Null(vm.VistaSelezionata);

        var stateRiavviato = NuovoStato();
        Assert.Empty(stateRiavviato.Config.VisteSalvate.GetValueOrDefault("test-list") ?? new List<VistaSalvataFiltri>());
    }

    [Fact]
    public void EliminaVistaSelezionataCommand_NonEseguibileSenzaSelezione()
    {
        var state = NuovoStato();
        var vm = new TestCaseListViewModel(state, _ => { });

        Assert.False(vm.EliminaVistaSelezionataCommand.CanExecute(null));
    }
}
