using System.Threading;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Home con aggiornamento in background (giri): i risultati coincidono con il calcolo sincrono, un giro superato non
/// applica nulla, un errore in background viene registrato senza far crashare la Home, e durante il caricamento non
/// compaiono dati dell'operatore precedente. Le letture bloccate si controllano con i ganci di test
/// (<see cref="HomeViewModel.GateLetturaPerTest"/>, <see cref="HomeViewModel.GuastoLetturaPerTest"/>), cosi' i tempi sono deterministici.
/// Fixture come HomeSezioniLazyTests; xunit.runner.json disattiva il parallelismo.
/// </summary>
[Trait("Category", "Lento")]
public sealed class HomeAggiornamentoInBackgroundTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public HomeAggiornamentoInBackgroundTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeBackground_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        foreach (var path in Directory.GetFiles(_dir, "testcase_CORROTTO-TEST-99.json", SearchOption.AllDirectories))
            File.Delete(path);
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

    /// <summary>Confronto di tutto cio' che la Home mostra: testata, copertura, liste, filtri, grafici, Recenti.</summary>
    private static void AssertUguali(HomeViewModel atteso, HomeViewModel ottenuto)
    {
        Assert.Equal(atteso.TotaleTestCase, ottenuto.TotaleTestCase);
        Assert.Equal(atteso.TotaleNcAperte, ottenuto.TotaleNcAperte);
        Assert.Equal(atteso.ChangeRequestAperte, ottenuto.ChangeRequestAperte);
        Assert.Equal(atteso.DaClassificare, ottenuto.DaClassificare);
        Assert.Equal(atteso.DaRitestare, ottenuto.DaRitestare);
        Assert.Equal(atteso.MieiAttivi, ottenuto.MieiAttivi);
        Assert.Equal(atteso.TestSenzaEcu, ottenuto.TestSenzaEcu);
        Assert.Equal(atteso.TassoConformitaPercentuale, ottenuto.TassoConformitaPercentuale, 6);
        Assert.Equal(atteso.CoperturaRequisitiPercentuale, ottenuto.CoperturaRequisitiPercentuale, 6);
        Assert.Equal(atteso.TotaleDaFare, ottenuto.TotaleDaFare);
        Assert.Equal(atteso.TotaleFatti, ottenuto.TotaleFatti);
        Assert.Equal(atteso.DaFare.Select(t => t.Id), ottenuto.DaFare.Select(t => t.Id));
        Assert.Equal(atteso.Fatti.Select(t => t.Id), ottenuto.Fatti.Select(t => t.Id));
        Assert.Equal(atteso.Recenti.Select(r => r.Id), ottenuto.Recenti.Select(r => r.Id));
        Assert.Equal(atteso.FiltroGammaDashboard.Opzioni.Select(o => o.Valore), ottenuto.FiltroGammaDashboard.Opzioni.Select(o => o.Valore));
        Assert.Equal(atteso.FiltroEcuDashboard.Opzioni.Select(o => o.Valore), ottenuto.FiltroEcuDashboard.Opzioni.Select(o => o.Valore));
        Assert.Equal(atteso.FiltriCampiCustomDashboard.Count, ottenuto.FiltriCampiCustomDashboard.Count);
        Assert.Equal(atteso.TestCaseNelFiltro, ottenuto.TestCaseNelFiltro);
        Assert.Equal(atteso.KpiIterazioniTesto, ottenuto.KpiIterazioniTesto);
        Assert.Equal(atteso.ChartsAttivi.Select(c => (c.Id, c.Titolo)), ottenuto.ChartsAttivi.Select(c => (c.Id, c.Titolo)));
        Assert.Equal(atteso.PerOperatore.Select(w => (w.Etichetta, w.Valore)), ottenuto.PerOperatore.Select(w => (w.Etichetta, w.Valore)));
        Assert.Equal(atteso.PerGamma.Select(w => (w.Etichetta, w.Valore)), ottenuto.PerGamma.Select(w => (w.Etichetta, w.Valore)));
    }

    private static bool RegistratoNelLogCrash(string identificativo)
    {
        var cartella = CrashLogService.LogDirectory;
        if (!Directory.Exists(cartella)) return false;
        return Directory.GetFiles(cartella).Any(file =>
        {
            try { return File.ReadAllText(file).Contains(identificativo, StringComparison.Ordinal); }
            catch { return false; }
        });
    }

    /// <summary>Il calcolo in background, applicato sul thread UI, da' gli stessi valori del calcolo sincrono.</summary>
    [Fact]
    public void Background_RisultatiUgualiAlCalcoloSincrono()
    {
        var sincrona = new HomeViewModel(_state, _ => { });
        var asincrona = new HomeViewModel(_state, _ => { }, aggiornaInBackground: true);
        asincrona.AttendiAggiornamentoAsync().GetAwaiter().GetResult();

        Assert.False(asincrona.CaricamentoInCorso);
        AssertUguali(sincrona, asincrona);
    }

    [Fact]
    public void ModalitaConformitaHome_PersistenzaLocaleESelezioneDelleDueBasi()
    {
        var home = new HomeViewModel(_state, _ => { });
        Assert.Equal(0, home.ModalitaPercentualeConformita);
        Assert.Contains("combinazioni", home.TestoTassoConformita, StringComparison.OrdinalIgnoreCase);

        home.ModalitaPercentualeConformita = 1;

        Assert.Equal("test", ViewStateService.Leggi(_state, "Home.ModalitaPercentualeConformita"));
        Assert.Contains("test)", home.TestoTassoConformita, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, new HomeViewModel(_state, _ => { }).ModalitaPercentualeConformita);
    }

    [Fact]
    public async Task DistribuzioneConformita_BackgroundInizialeECambioModalitaCalcolatiSulWorker()
    {
        var home = new HomeViewModel(_state, _ => { });
        home.ModalitaPercentualeConformita = 0;
        home.ImpostaAggiornamentoInBackgroundPerTest(true);
        var threadUi = Environment.CurrentManagedThreadId;
        var preparate = new System.Collections.Concurrent.ConcurrentQueue<(int Modalita, int Thread)>();
        home.DistribuzioneConformitaPreparataPerTest = (modalita, thread) => preparate.Enqueue((modalita, thread));

        await home.AggiornaAsync();
        home.ModalitaPercentualeConformita = 1;
        await home.AttendiAggiornamentoAsync();

        Assert.Contains(preparate, x => x.Modalita == 0 && x.Thread != threadUi);
        Assert.Contains(preparate, x => x.Modalita == 1 && x.Thread != threadUi);
        Assert.Equal(1, home.ModalitaPercentualeConformita);
        Assert.False(home.CaricamentoInCorso);
    }

    /// <summary>Un giro superato da uno piu' recente non applica i propri risultati: restano quelli del giro piu' recente.</summary>
    [Fact]
    public void GiroSuperato_RisultatoScartato_NonApplicato()
    {
        var riferimento = new HomeViewModel(_state, _ => { });
        var home = new HomeViewModel(_state, _ => { });
        var entrato = new ManualResetEventSlim(false);
        var rilascio = new ManualResetEventSlim(false);
        var letture = 0;
        home.ImpostaAggiornamentoInBackgroundPerTest(true);
        home.GateLetturaPerTest = () =>
        {
            if (Interlocked.Increment(ref letture) == 1)
            {
                entrato.Set();
                rilascio.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var primo = home.AggiornaAsync();
        Assert.True(entrato.Wait(TimeSpan.FromSeconds(30)), "La lettura in background del primo giro non e' partita.");
        var secondo = home.AggiornaAsync();
        secondo.GetAwaiter().GetResult();
        rilascio.Set();
        primo.GetAwaiter().GetResult();
        home.AttendiAggiornamentoAsync().GetAwaiter().GetResult();

        Assert.Equal(1, home.GiriScartati);
        Assert.False(home.CaricamentoInCorso);
        AssertUguali(riferimento, home);
    }

    /// <summary>Un guasto nella lettura in background non esce dalla Home: viene registrato nel log dei crash, l'indicatore
    /// si spegne e un aggiornamento successivo riporta i valori corretti.</summary>
    [Fact]
    public void ErroreInBackground_RegistratoSenzaCrash_LaHomeRestaUtilizzabile()
    {
        var riferimento = new HomeViewModel(_state, _ => { });
        var home = new HomeViewModel(_state, _ => { });
        var identificativo = Guid.NewGuid().ToString("N");
        home.ImpostaAggiornamentoInBackgroundPerTest(true);
        home.GuastoLetturaPerTest = () => throw new InvalidOperationException("guasto di prova " + identificativo);

        home.Aggiorna();
        home.AttendiAggiornamentoAsync().GetAwaiter().GetResult();

        Assert.NotNull(home.UltimaEccezioneAggiornamento);
        Assert.Contains(identificativo, home.UltimaEccezioneAggiornamento!.Message, StringComparison.Ordinal);
        Assert.False(home.CaricamentoInCorso);
        Assert.True(RegistratoNelLogCrash(identificativo), "L'eccezione in background non e' finita nel log dei crash.");

        home.GuastoLetturaPerTest = null;
        home.Aggiorna();
        home.AttendiAggiornamentoAsync().GetAwaiter().GetResult();
        AssertUguali(riferimento, home);
    }

    /// <summary>Cambio operatore: mentre il nuovo giro e' in corso la Home non mostra Da fare, Fatti e miei attivi dell'operatore
    /// precedente; a fine giro i valori sono quelli del nuovo operatore, come nel calcolo sincrono.</summary>
    [Fact]
    public void CambioOperatore_DuranteIlCaricamento_NonMostraIDatiDelPrecedente()
    {
        var home = new HomeViewModel(_state, _ => { });
        Assert.True(home.DaFare.Count + home.Fatti.Count > 0, "Il seed demo deve avere Test in Da fare o Fatti.");
        var operatorePrecedente = _state.Config.OperatoreAttivo;

        _state.Config.OperatoreAttivo = "Operatore di prova";
        var rilascio = new ManualResetEventSlim(false);
        home.ImpostaAggiornamentoInBackgroundPerTest(true);
        home.GateLetturaPerTest = () => rilascio.Wait(TimeSpan.FromSeconds(30));

        var giro = home.AggiornaAsync();
        Assert.True(home.CaricamentoInCorso);
        Assert.Empty(home.DaFare);
        Assert.Empty(home.Fatti);
        Assert.Equal(0, home.MieiAttivi);

        rilascio.Set();
        giro.GetAwaiter().GetResult();
        Assert.False(home.CaricamentoInCorso);
        AssertUguali(new HomeViewModel(_state, _ => { }), home);

        _state.Config.OperatoreAttivo = operatorePrecedente;
    }
}
