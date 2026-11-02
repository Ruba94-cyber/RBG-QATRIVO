using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Viste salvate sulla pagina Anomalie/CR, stesso modello delle viste sulla lista Test (VisteSalvateTests):
/// cattura di tutti i filtri e dell'ordinamento, persistenza su disco, riapplicazione con un solo ricaricamento,
/// valori non piu' nel catalogo che non allargano il filtro neppure dopo un refresh della pagina.</summary>
/// <remarks>[Collection("Localization")]: mutano TextEditService.Handler/ConfirmService.Handler, statici e condivisi da
/// tutto il processo, stesso motivo di VisteSalvateTests.</remarks>
[Collection("Localization")]
public sealed class AnomalieVisteSalvateTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieViste_" + Guid.NewGuid().ToString("N"));
    private readonly string _cartella = Path.Combine(Path.GetTempPath(), "TestLoom_AnomalieViste_Dati_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, Task<string?>>? _textEditOriginale = TextEditService.Handler;
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        TextEditService.Handler = _textEditOriginale;
        ConfirmService.Handler = _confirmOriginale;
        foreach (var dir in new[] { _configDir, _cartella })
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
    }

    private AppState NuovoStato() => new(new AppConfigService(Path.Combine(_configDir, "config.json")));

    private AppState StatoConCartella()
    {
        var state = NuovoStato();
        Directory.CreateDirectory(_cartella); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_cartella);
        return state;
    }

    private static void ImpostaVistaInConfig(AppState state, string nome, Dictionary<string, string?> valori) =>
        state.Config.VisteSalvate["anomalie-list"] = new List<VistaSalvataFiltri> { new() { Nome = nome, Valori = valori } };

    [Fact]
    public async Task SalvaVista_CatturaTuttiIFiltriERiapplicaliDopoIlRiavvioDelConfig()
    {
        var vm = new NonConformitaViewModel(NuovoStato(), _ => { });
        vm.FiltroEcu.Popola(new[] { ("ABS", "ABS"), ("BCM", "BCM") });
        vm.FiltroEcu.Opzioni.Single(o => o.Valore == "BCM").Selezionato = true;
        vm.FiltroTesto = "rear camera";
        vm.FiltroTicket = "TICKET-42";
        vm.FiltroTipo = FiltroTipoAnomaliaOCr.ChangeRequest;
        vm.FiltroStato = StatoNonConformita.Chiusa;
        vm.FiltroStatoCr = StatoChangeRequest.Implementata;
        vm.FiltroTicketEsta = FiltroPresenzaTicketEsta.ConTicket;
        vm.FiltroPriorita.Opzioni.Single(o => o.Valore == nameof(PrioritaOperativa.Critical)).Selezionato = true;
        vm.FiltroGravita.Opzioni.Single(o => o.Valore == nameof(SeveritaAnomalia.High)).Selezionato = true;
        vm.FiltroDataAperturaDa = new DateTimeOffset(new DateTime(2026, 9, 1));
        vm.FiltroDataChiusuraA = new DateTimeOffset(new DateTime(2026, 10, 31));
        vm.OrdinamentoChiave = AnomalieOrdinamentoChiave.Priorita;
        vm.OrdinamentoDecrescente = false;
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Vista completa");

        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);

        // Riavvio simulato: un secondo AppState rilegge lo stesso config.json da disco.
        var riavviato = new NonConformitaViewModel(NuovoStato(), _ => { });
        riavviato.FiltroEcu.Popola(new[] { ("ABS", "ABS"), ("BCM", "BCM") });
        var vista = Assert.Single(riavviato.VisteSalvate);
        Assert.Equal("Vista completa", vista.Nome);

        // Filtri portati su altri valori: la riapplicazione deve riportare tutto a quanto salvato.
        riavviato.FiltroTesto = "";
        riavviato.FiltroTicket = "";
        riavviato.FiltroTipo = FiltroTipoAnomaliaOCr.Entrambi;
        riavviato.FiltroStato = null;
        riavviato.FiltroStatoCr = null;
        riavviato.FiltroTicketEsta = FiltroPresenzaTicketEsta.Tutti;
        riavviato.FiltroEcu.DeselezionaTutto();
        riavviato.FiltroPriorita.DeselezionaTutto();
        riavviato.FiltroGravita.DeselezionaTutto();
        riavviato.FiltroDataAperturaDa = null;
        riavviato.FiltroDataChiusuraA = null;
        riavviato.OrdinamentoChiave = AnomalieOrdinamentoChiave.Apertura;
        riavviato.OrdinamentoDecrescente = true;

        riavviato.VistaSelezionata = vista;

        Assert.Equal("rear camera", riavviato.FiltroTesto);
        Assert.Equal("TICKET-42", riavviato.FiltroTicket);
        Assert.Equal(FiltroTipoAnomaliaOCr.ChangeRequest, riavviato.FiltroTipo);
        Assert.Equal(StatoNonConformita.Chiusa, riavviato.FiltroStato);
        Assert.Equal(StatoChangeRequest.Implementata, riavviato.FiltroStatoCr);
        Assert.Equal(FiltroPresenzaTicketEsta.ConTicket, riavviato.FiltroTicketEsta);
        Assert.Equal(new[] { "BCM" }, riavviato.FiltroEcu.Selezionati);
        Assert.Equal(new[] { nameof(PrioritaOperativa.Critical) }, riavviato.FiltroPriorita.Selezionati);
        Assert.Equal(new[] { nameof(SeveritaAnomalia.High) }, riavviato.FiltroGravita.Selezionati);
        Assert.Equal(new DateTime(2026, 9, 1), riavviato.DataAperturaDa);
        Assert.Equal(new DateTime(2026, 10, 31), riavviato.DataChiusuraA);
        Assert.Equal(AnomalieOrdinamentoChiave.Priorita, riavviato.OrdinamentoChiave);
        Assert.False(riavviato.OrdinamentoDecrescente);
    }

    [Fact]
    public async Task EliminaVistaSelezionata_LaRimuoveDallaListaEDalConfigPersistito()
    {
        var vm = new NonConformitaViewModel(NuovoStato(), _ => { });
        Assert.False(vm.EliminaVistaSelezionataCommand.CanExecute(null));
        TextEditService.Handler = (_, _) => Task.FromResult<string?>("Da eliminare");
        await vm.SalvaVistaCorrenteCommand.ExecuteAsync(null);
        Assert.NotNull(vm.VistaSelezionata); // il salvataggio seleziona la vista appena creata
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);

        await vm.EliminaVistaSelezionataCommand.ExecuteAsync(null);

        Assert.Empty(vm.VisteSalvate);
        Assert.Null(vm.VistaSelezionata);
        Assert.Empty(new NonConformitaViewModel(NuovoStato(), _ => { }).VisteSalvate);
    }

    [Fact]
    public void VistaConValoriNonPiuNelCatalogo_NonAllargaIlFiltroNeAggiornamentiSuccessivi()
    {
        var state = StatoConCartella();
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ABS" }, new() { Nome = "BCM" } });
        // Vista salvata quando il componente "VECCHIA" esisteva ancora e l'assegnatario non compare piu' nei dati.
        ImpostaVistaInConfig(state, "Valori fuori catalogo", new()
        {
            ["Componente"] = "BCM\u001FVECCHIA",
            ["Assegnatario"] = "Ex Operatore",
        });
        var vm = new NonConformitaViewModel(state, _ => { });

        vm.VistaSelezionata = Assert.Single(vm.VisteSalvate);

        Assert.Equal(new[] { "BCM", "VECCHIA" }, vm.FiltroEcu.Selezionati);
        Assert.Equal(new[] { "Ex Operatore" }, vm.FiltroAssegnatario.Selezionati);

        // Refresh della pagina (Aggiorna = RipopolaFiltri + Ricarica): i valori fuori catalogo restano selezionati.
        vm.Aggiorna();

        Assert.Equal(new[] { "BCM", "VECCHIA" }, vm.FiltroEcu.Selezionati);
        Assert.Equal(new[] { "Ex Operatore" }, vm.FiltroAssegnatario.Selezionati);
    }

    [Fact]
    public void ApplicaVista_RicaricaLaPaginaUnaSolaVoltaAncheConPiuFiltriCambiati()
    {
        var state = NuovoStato();
        ImpostaVistaInConfig(state, "Completa", new()
        {
            ["Testo"] = "abc",
            ["Ticket"] = "TICKET-1",
            ["Stato"] = nameof(StatoNonConformita.Chiusa),
            ["Componente"] = "BCM\u001FABS",
            ["Priorita"] = nameof(PrioritaOperativa.Critical),
            ["Gravita"] = nameof(SeveritaAnomalia.High),
            ["DataAperturaDa"] = "2026-09-01",
            ["OrdinamentoChiave"] = nameof(AnomalieOrdinamentoChiave.Priorita),
            ["OrdinamentoDecrescente"] = "0",
        });
        var ricariche = 0;
        var vm = new NonConformitaViewModel(state, _ => { }, null, null, null, () => ricariche++);
        var prima = ricariche;

        vm.VistaSelezionata = Assert.Single(vm.VisteSalvate);

        Assert.Equal(prima + 1, ricariche);
        Assert.Equal("abc", vm.FiltroTesto); // la vista e' stata applicata davvero, non solo selezionata
        Assert.Equal(StatoNonConformita.Chiusa, vm.FiltroStato);
        Assert.Equal(2, vm.FiltroEcu.Selezionati.Count);
    }

    [Fact]
    public void CambioManualeDiUnFiltro_SvuotaLaVistaSelezionataEConsenteDiRiapplicarla()
    {
        var state = NuovoStato();
        ImpostaVistaInConfig(state, "Chiuse", new() { ["Stato"] = nameof(StatoNonConformita.Chiusa) });
        var vm = new NonConformitaViewModel(state, _ => { });
        vm.VistaSelezionata = Assert.Single(vm.VisteSalvate);
        Assert.Equal(StatoNonConformita.Chiusa, vm.FiltroStato);

        vm.FiltroStato = StatoNonConformita.Aperta; // modifica manuale: la vista non descrive piu' i filtri correnti

        Assert.Null(vm.VistaSelezionata);
        vm.VistaSelezionata = vm.VisteSalvate.Single();
        Assert.Equal(StatoNonConformita.Chiusa, vm.FiltroStato);
    }

    [Fact]
    public void RimuoviFiltri_SvuotaLaVistaSelezionata()
    {
        var state = NuovoStato();
        ImpostaVistaInConfig(state, "Chiuse", new() { ["Stato"] = nameof(StatoNonConformita.Chiusa) });
        var vm = new NonConformitaViewModel(state, _ => { });
        vm.VistaSelezionata = Assert.Single(vm.VisteSalvate);

        vm.ReimpostaFiltriCommand.Execute(null);

        Assert.Null(vm.VistaSelezionata);
        Assert.Null(vm.FiltroStato);
    }
}
