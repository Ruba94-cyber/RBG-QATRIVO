using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Segnalazione utente (2026-10, pagina Report): scelta la gamma LCV DLS, sostituirla con un'altra "non succede niente" e la
/// selezione resta quella vecchia finche' non si cambia schermata. L'oracolo e' un Report nuovo con la sola gamma di arrivo.
/// </summary>
[Trait("Category", "Lento")]
public sealed class ReportFiltroGammaCambioTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportFiltroGammaCambioTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportGammaCambio_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.TestCases.RefreshIndex();
    }

    public void Dispose()
    {
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FiltroMultiSelect Gamma(ReportViewModel vm) =>
        vm.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Gamma).Filtro;

    private static void ImpostaGamma(ReportViewModel vm, params string[] gamme)
    {
        foreach (var o in Gamma(vm).Opzioni) o.Selezionato = gamme.Contains(o.Valore);
    }

    private static string Impronta(ReportViewModel vm) =>
        $"gamma=[{string.Join(',', Gamma(vm).Selezionati)}] conteggio={vm.TestoConteggioAnteprima} vuota={vm.MatriceVuota}";

    private async Task<string> Oracolo(string gamma)
    {
        var vm = new ReportViewModel(_state);
        await vm.AttendiAggiornamentoAsync();
        ImpostaGamma(vm, gamma);
        await vm.AttendiAggiornamentoAsync();
        return Impronta(vm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CambioDaUnaGammaAUnAltra_IlReportSegueLaNuovaGamma(bool inBackground)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();
        Assert.True(gamme.Count >= 2, "il seed deve avere almeno due gamme");

        ImpostaGamma(vm, gamme[0]);
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(await Oracolo(gamme[0]), Impronta(vm));

        ImpostaGamma(vm, gamme[1]);
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(await Oracolo(gamme[1]), Impronta(vm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DopoIlRiavvioConGammaSalvata_IlCambioDiGammaVieneApplicato(bool inBackground)
    {
        var prima = new ReportViewModel(_state);
        await prima.AttendiAggiornamentoAsync();
        var gamme = Gamma(prima).Opzioni.Select(o => o.Valore).ToList();
        ImpostaGamma(prima, gamme[0]);
        await prima.AttendiAggiornamentoAsync();

        var riavviato = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await riavviato.AttendiAggiornamentoAsync();
        Assert.Equal(await Oracolo(gamme[0]), Impronta(riavviato));

        ImpostaGamma(riavviato, gamme[1]);
        await riavviato.AttendiAggiornamentoAsync();
        Assert.Equal(await Oracolo(gamme[1]), Impronta(riavviato));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CambiRapidiDiGamma_ApplicanoSempreL_UltimaScelta(bool inBackground)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();

        foreach (var g in gamme) ImpostaGamma(vm, g);
        ImpostaGamma(vm, gamme[0]);
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(await Oracolo(gamme[0]), Impronta(vm));
    }
}

/// <summary>
/// Segnalazione utente (2026-10): il filtro gamma "resta" giocando con righe e colonne della matrice del Report. Alterna cambi di
/// asse e di filtro, con e senza attesa fra un passo e l'altro, e confronta lo stato finale con un Report nuovo che ripristina la
/// stessa configurazione salvata.
/// </summary>
[Trait("Category", "Lento")]
public sealed class ReportAssiEFiltroGammaTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportAssiEFiltroGammaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportAssiGamma_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.TestCases.RefreshIndex();
    }

    public void Dispose()
    {
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FiltroMultiSelect Gamma(ReportViewModel vm) =>
        vm.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Gamma).Filtro;

    private static string Impronta(ReportViewModel vm) =>
        $"X=[{string.Join(',', vm.CampiXMatrice.Select(c => c.Dimensione.Id))}] Y=[{string.Join(',', vm.CampiYMatrice.Select(c => c.Dimensione.Id))}] " +
        $"filtri=[{string.Join(';', vm.FiltriMatrice.Select(f => f.Dimensione.Id + "=" + string.Join('|', f.Filtro.Selezionati)))}] " +
        $"conteggio={vm.TestoConteggioAnteprima} vuota={vm.MatriceVuota}";

    private async Task<string> Oracolo()
    {
        var vm = new ReportViewModel(_state);
        await vm.AttendiAggiornamentoAsync();
        return Impronta(vm);
    }

    private static void ImpostaGamma(ReportViewModel vm, params string[] gamme)
    {
        foreach (var o in Gamma(vm).Opzioni) o.Selezionato = gamme.Contains(o.Valore);
    }

    private static DimensioneMatriceOpzione Dim(ReportViewModel vm, string id) => vm.OpzioniDimensioniMatrice.First(d => d.Id == id);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AlternareAssiEFiltroGamma_LoStatoFinaleEQuelloSalvato(bool inBackground, bool attendiOgniPasso)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();
        Assert.True(gamme.Count >= 2);

        async Task Passo(Action azione)
        {
            azione();
            if (attendiOgniPasso) await vm.AttendiAggiornamentoAsync();
        }

        await Passo(() => ImpostaGamma(vm, gamme[0]));
        await Passo(() => vm.AggiungiCampoMatrice(righe: false));
        await Passo(() => vm.AggiungiCampoMatrice(righe: true));
        await Passo(() => vm.CampiXMatrice[0].Dimensione = Dim(vm, DimensioniMatrice.Gamma));
        await Passo(() => ImpostaGamma(vm, gamme[1]));
        await Passo(() => vm.CampiYMatrice[0].Dimensione = Dim(vm, DimensioniMatrice.Componente));
        await Passo(() => vm.RimuoviCampoMatrice(vm.CampiXMatrice[^1]));
        await Passo(() => ImpostaGamma(vm, gamme[0], gamme[1]));
        await Passo(() => vm.AggiungiCampoMatrice(righe: false));
        await Passo(() => ImpostaGamma(vm));
        await Passo(() => ImpostaGamma(vm, gamme[1]));
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(await Oracolo(), Impronta(vm));
        Assert.Equal(new[] { gamme[1] }, Gamma(vm).Selezionati);
    }
}

/// <summary>
/// Segnalazione utente (2026-10): nel Report la gamma scelta "resta visualizzata" anche dopo aver cambiato filtro o righe e colonne, e
/// i dati si aggiornano solo uscendo e rientrando nella pagina. Qui si controlla cio' che la matrice MOSTRA (etichette di righe e
/// colonne), non solo i conteggi: la vista mostra la finestra proiettata da ApplicaMatrice.
/// </summary>
[Trait("Category", "Lento")]
public sealed class ReportMatriceMostrataTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportMatriceMostrataTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ReportMatriceMostrata_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
        _state.TestCases.RefreshIndex();
    }

    public void Dispose()
    {
        _state.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FiltroMultiSelect Gamma(ReportViewModel vm) =>
        vm.FiltriMatrice.Single(f => f.Dimensione.Id == DimensioniMatrice.Gamma).Filtro;

    private static void ImpostaGamma(ReportViewModel vm, params string[] gamme)
    {
        foreach (var o in Gamma(vm).Opzioni) o.Selezionato = gamme.Contains(o.Valore);
    }

    private static string Mostrato(ReportViewModel vm) =>
        $"colonne=[{string.Join('|', vm.ColonneMatrice)}] righe=[{string.Join('|', vm.RigheMatrice.Select(r => r.Riga))}] vuota={vm.MatriceVuota}";

    /// <summary>Imposta un campo per asse (o nessuno sull'asse Y). La matrice non puo' restare senza campi: prima si portano gli assi su
    /// dimensioni libere, cosi' anche uno scambio fra X e Y non incontra il rifiuto delle dimensioni duplicate.</summary>
    private static void ImpostaAssi(ReportViewModel vm, string x, string? y)
    {
        DimensioneMatriceOpzione Op(string id) => vm.OpzioniDimensioniMatrice.First(d => d.Id == id);
        while (vm.CampiXMatrice.Count > 1) vm.RimuoviCampoMatrice(vm.CampiXMatrice[^1]);
        if (vm.CampiXMatrice.Count == 0) vm.AggiungiCampoMatrice(righe: false);
        if (y != null)
        {
            while (vm.CampiYMatrice.Count > 1) vm.RimuoviCampoMatrice(vm.CampiYMatrice[^1]);
            if (vm.CampiYMatrice.Count == 0) vm.AggiungiCampoMatrice(righe: true);
            vm.CampiXMatrice[0].Dimensione = Op(DimensioniMatrice.Esito);
            vm.CampiYMatrice[0].Dimensione = Op(DimensioniMatrice.Template);
            vm.CampiXMatrice[0].Dimensione = Op(x);
            vm.CampiYMatrice[0].Dimensione = Op(y);
        }
        else
        {
            while (vm.CampiYMatrice.Count > 0) vm.RimuoviCampoMatrice(vm.CampiYMatrice[0]);
            vm.CampiXMatrice[0].Dimensione = Op(x);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConGammaSuUnAsse_LaMatriceMostraSoloLaGammaFiltrata_ESiAggiornaAlCambio(bool inBackground)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();
        ImpostaAssi(vm, DimensioniMatrice.Gamma, DimensioniMatrice.Componente);
        await vm.AttendiAggiornamentoAsync();

        ImpostaGamma(vm, gamme[0]);
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(new[] { gamme[0] }, vm.ColonneMatrice.Select(ReportMatriceService.EtichettaPercorso).Distinct());

        ImpostaGamma(vm, gamme[1]);
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(new[] { gamme[1] }, vm.ColonneMatrice.Select(ReportMatriceService.EtichettaPercorso).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TogliendoLAsseRighe_LaMatriceMostraSoloLaGammaFiltrata(bool inBackground)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();
        ImpostaAssi(vm, DimensioniMatrice.Gamma, DimensioniMatrice.Componente);
        ImpostaGamma(vm, gamme[0]);
        await vm.AttendiAggiornamentoAsync();
        Assert.NotEmpty(vm.ColonneMatrice);

        ImpostaAssi(vm, DimensioniMatrice.Gamma, null);
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(new[] { gamme[0] }, vm.ColonneMatrice.Select(ReportMatriceService.EtichettaPercorso).Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CambiandoAssiEFiltro_LaMatriceMostrataEQuellaNuova(bool inBackground)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();
        ImpostaAssi(vm, DimensioniMatrice.Gamma, DimensioniMatrice.Componente);
        ImpostaGamma(vm, gamme[0]);
        await vm.AttendiAggiornamentoAsync();

        ImpostaAssi(vm, DimensioniMatrice.Componente, DimensioniMatrice.Gamma);
        ImpostaGamma(vm, gamme[1]);
        await vm.AttendiAggiornamentoAsync();

        Assert.Equal(new[] { gamme[1] }, vm.RigheMatrice.Select(r => ReportMatriceService.EtichettaPercorso(r.Riga)).Distinct());
    }

    /// <summary>Percorso segnalato dall'utente: gamma sulle ascisse, tolte tutte le voci dalle ascisse, gamma rimessa sulle ascisse.
    /// A ogni passo la matrice mostrata e i filtri devono coincidere con quelli di un Report nuovo con la stessa configurazione.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task GammaSulleAscisse_TolteLeVociERimessa_NonRestaNullaDiVecchio(bool inBackground, bool attendiOgniPasso)
    {
        var vm = new ReportViewModel(_state, aggiornaInBackground: inBackground);
        await vm.AttendiAggiornamentoAsync();
        var gamme = Gamma(vm).Opzioni.Select(o => o.Valore).ToList();
        DimensioneMatriceOpzione Op(string id) => vm.OpzioniDimensioniMatrice.First(d => d.Id == id);
        async Task Passo(Action azione) { azione(); if (attendiOgniPasso) await vm.AttendiAggiornamentoAsync(); }

        ImpostaAssi(vm, DimensioniMatrice.Gamma, DimensioniMatrice.Componente);
        await Passo(() => ImpostaGamma(vm, gamme[0]));
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(new[] { gamme[0] }, await ColonneGamma(vm));

        // Tolte tutte le voci dalle ascisse (resta l'asse righe).
        await Passo(() => { while (vm.CampiXMatrice.Count > 0) vm.RimuoviCampoMatrice(vm.CampiXMatrice[0]); });
        await vm.AttendiAggiornamentoAsync();
        Assert.Empty(vm.CampiXMatrice);
        Assert.Equal(new[] { gamme[0] }, Gamma(vm).Selezionati);

        // Gamma di nuovo sulle ascisse.
        await Passo(() => { vm.AggiungiCampoMatrice(righe: false); vm.CampiXMatrice[0].Dimensione = Op(DimensioniMatrice.Gamma); });
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(new[] { gamme[0] }, await ColonneGamma(vm));

        // Cambio della gamma dopo il giro: deve valere subito.
        await Passo(() => ImpostaGamma(vm, gamme[1]));
        await vm.AttendiAggiornamentoAsync();
        Assert.Equal(new[] { gamme[1] }, await ColonneGamma(vm));
        Assert.Equal(new[] { gamme[1] }, Gamma(vm).Selezionati);
    }

    private static Task<IEnumerable<string>> ColonneGamma(ReportViewModel vm) =>
        Task.FromResult(vm.ColonneMatrice.Select(ReportMatriceService.EtichettaPercorso).Distinct());
}
