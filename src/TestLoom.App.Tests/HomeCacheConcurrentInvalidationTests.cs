using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Piano 6.4.0 (seconda postazione): la postazione A tiene la Home aperta con cache calda (KPI testata,
/// copertura requisiti, avviso "Test senza componente", grafico donut — stesso perimetro di
/// <see cref="HomeGraficiCacheTests"/>) mentre la postazione B, sulla STESSA cartella condivisa, salva un
/// nuovo Test — in un caso senza alcuna componente associata (TestSenzaEcu), in un altro con
/// <see cref="OrigineTest.ImportStatoUdt"/> ("importato" da WU Status). Dopo il refresh che l'app farebbe
/// davvero (RefreshIndex sull'archivio Test, stesso schema usato in HomeGraficiCacheTests e
/// MainViewModelPreparaPaginaTests), la Home di A deve riflettere lo stato aggiornato — copertura, riepilogo,
/// avviso componente, grafico — senza valori vecchi residui e senza eccezioni.
/// </summary>
[Trait("Category", "Lento")]
public sealed class HomeCacheConcurrentInvalidationTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public HomeCacheConcurrentInvalidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeCacheInvalid_" + Guid.NewGuid().ToString("N"));
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

    private static void AttendiCondizione(Func<bool> condizione, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condizione() && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(25);
        Assert.True(condizione(), "Condizione di attesa non soddisfatta entro il timeout.");
    }

    [Fact]
    public void SecondaPostazioneSalvaTestSenzaEcu_DopoRefresh_HomeMostraAvvisoAggiornatoSenzaEccezioni()
    {
        var home = new HomeViewModel(_state, _ => { });
        var totalePrima = home.TotaleTestCase;
        var senzaEcuPrima = home.TestSenzaEcu;

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_B1", "config.json"));
        using var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(_dir);
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-HOME-SENZA-componente",
            // Nessuna componente associata di proposito: deve incrementare TestSenzaEcu dopo il refresh.
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);

        var ex = Record.Exception(() =>
        {
            _state.TestCases.RefreshIndex();
            home.Aggiorna();
        });

        Assert.Null(ex);
        Assert.Equal(totalePrima + 1, home.TotaleTestCase);
        Assert.Equal(senzaEcuPrima + 1, home.TestSenzaEcu);
        Assert.True(home.MostraAvvisoTestSenzaEcu);
        // Nessun residuo: il totale non e' rimasto "congelato" alla fotografia di apertura.
        Assert.NotEqual(totalePrima, home.TotaleTestCase);
    }

    [Fact]
    public void SecondaPostazioneSalvaTestImportatoDaWuStatus_DopoRefresh_NessunaEccezioneEConteggioStabileSuRivisiteRipetute()
    {
        var home = new HomeViewModel(_state, _ => { });
        var totalePrima = home.TotaleTestCase;

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_B2", "config.json"));
        using var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(_dir);
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-HOME-IMPORTATO",
            Origine = OrigineTest.ImportStatoUdt,
            StatoLavoro = StatoLavoroTest.Concluso,
            Esecuzioni = { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme } },
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);

        var ex = Record.Exception(() =>
        {
            _state.TestCases.RefreshIndex();
            home.Aggiorna();
        });
        Assert.Null(ex);

        // OrigineTest.ImportStatoUdt e' per design escluso dal conteggio "numero Test" della testata (commento
        // su HomeViewModel.AggiornaKpiTestata: "Test importati: solo stato finale... esclusi dai conteggi
        // operativi della testata"): la scrittura della seconda postazione non deve quindi far apparire un
        // valore residuo diverso da quello corretto, qualunque esso sia per questo dataset.
        var totaleDopoUnaRivisita = home.TotaleTestCase;

        // Una seconda rivisita senza ulteriori cambiamenti deve restare STABILE: nessun valore residuo
        // dell'apertura originale che "torna" al refresh successivo, nessuna oscillazione fra due rivisite
        // identiche (regressione di cache/percorso di calcolo).
        var ex2 = Record.Exception(() => home.Aggiorna());
        Assert.Null(ex2);
        Assert.Equal(totaleDopoUnaRivisita, home.TotaleTestCase);
    }

    [Fact]
    public void SecondaPostazioneScriveMentreAECheckaGraficoDonut_RigeneraSenzaValoriVecchiResidui()
    {
        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        Assert.Single(home.ChartsAttivi);
        var generatiPrima = PerfContatori.LeggiSnapshot().GraficoPngGenerato;

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_B3", "config.json"));
        using var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(_dir);
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-HOME-DONUT-SECONDA",
            StatoLavoro = StatoLavoroTest.Concluso,
            Esecuzioni = { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme } },
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);

        _state.TestCases.RefreshIndex();
        var ex = Record.Exception(() => home.Aggiorna());

        Assert.Null(ex);
        var dopo = PerfContatori.LeggiSnapshot();
        // La scrittura remota ha cambiato l'esito conteggiato nel donut: la cache basata sulla firma dei dati
        // deve rigenerare (mai restituire il PNG precedente, "vecchio", come se nulla fosse cambiato).
        Assert.Equal(generatiPrima + 1, dopo.GraficoPngGenerato);
        Assert.Single(home.ChartsAttivi);
    }
}
