using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Perf Home 6.4.0-rivisita: i PNG SkiaSharp dei grafici (<see cref="ChartRenderer"/>) sono costosi da
/// rigenerare e la Home vi torna spesso senza che i dati del grafico attivo siano cambiati. Questi test
/// verificano tramite <see cref="PerfContatori"/> (non tramite l'identita' del Bitmap risultante, che in un
/// host di unit test puro senza platform Avalonia/Skia inizializzata resta sempre null: PngABitmap fallisce
/// silenziosamente) che <see cref="HomeViewModel"/> riusi l'input cache invece di rigenerare il PNG quando
/// non e' cambiato nulla, che lo rigeneri quando cambia — anche per una scrittura da una SECONDA postazione
/// sulla stessa cartella condivisa, non solo per una modifica locale — e che deselezionare/riselezionare un
/// grafico non lasci un Bitmap smaltito agganciato alla UI né generi eccezioni.
/// </summary>
[Trait("Category", "Lento")]
public sealed class HomeGraficiCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public HomeGraficiCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeGraficiCache_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Il grafico "donut" e' selezionato di default: due rivisite di fila senza alcun cambiamento
    /// di dati/filtri/lingua devono riusare la cache (nessun nuovo render), non rigenerare il PNG.</summary>
    [Fact]
    public void AggiornaDueVolteSenzaCambiamenti_NonRigeneraIlPngDelGraficoDonut()
    {
        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        Assert.Single(home.ChartsAttivi);
        var dopoCostruzione = PerfContatori.LeggiSnapshot();
        Assert.True(dopoCostruzione.GraficoPngGenerato >= 1);

        home.Aggiorna();

        var dopoSecondaRivisita = PerfContatori.LeggiSnapshot();
        Assert.Equal(dopoCostruzione.GraficoPngGenerato, dopoSecondaRivisita.GraficoPngGenerato);
        Assert.True(dopoSecondaRivisita.GraficoPngRiusato > dopoCostruzione.GraficoPngRiusato);
        Assert.Single(home.ChartsAttivi);
    }

    /// <summary>Una scrittura da una SECONDA postazione che cambia l'esito conteggiato nel donut deve
    /// invalidare la cache: la firma e' sui dati effettivi (bucket/aggregati), non su data o dimensione file,
    /// quindi deve accorgersene anche quando il cambiamento arriva da fuori processo.</summary>
    [Fact]
    public void AggiornaDopoScritturaDiSecondaPostazioneCheCambiaIDati_RigeneraIlPngDelGraficoDonut()
    {
        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var dopoCostruzione = PerfContatori.LeggiSnapshot();

        var configService2 = new AppConfigService(Path.Combine(_dir + "_local_B", "config.json"));
        var stato2 = new AppState(configService2, runBackgroundMaintenance: false);
        stato2.InizializzaCartella(_dir);
        stato2.TestCases.Save(new TestCase
        {
            Id = "T-HOME-GRAFICI-SECONDA-POSTAZIONE",
            StatoLavoro = StatoLavoroTest.Concluso,
            Esecuzioni = { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.NonConforme } },
            ModificatoDa = "Seconda Postazione",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        }, isNew: true);
        stato2.Dispose();

        _state.TestCases.RefreshIndex();
        home.Aggiorna();

        var dopoSecondaRivisita = PerfContatori.LeggiSnapshot();
        Assert.Equal(dopoCostruzione.GraficoPngGenerato + 1, dopoSecondaRivisita.GraficoPngGenerato);
        Assert.Equal(dopoCostruzione.GraficoPngRiusato, dopoSecondaRivisita.GraficoPngRiusato);
    }

    /// <summary>Deselezionare un grafico deve smaltirne il Bitmap in cache (niente accumulo); riselezionarlo
    /// deve rigenerare il PNG (mai riusare una cache scaduta) senza eccezioni.</summary>
    [Fact]
    public void DeselezionaERiselezionaGraficoDonut_RigeneraSenzaEccezioni()
    {
        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var opzioneDonut = home.ChartOptions.Single(c => c.Id == "donut");
        Assert.True(opzioneDonut.Selezionato);
        var dopoCostruzione = PerfContatori.LeggiSnapshot();

        opzioneDonut.Selezionato = false;
        Assert.Empty(home.ChartsAttivi);

        opzioneDonut.Selezionato = true;
        var dopoRiselezione = PerfContatori.LeggiSnapshot();

        Assert.Single(home.ChartsAttivi);
        Assert.Equal(dopoCostruzione.GraficoPngGenerato + 1, dopoRiselezione.GraficoPngGenerato);
    }

    /// <summary>Cambiare un filtro della dashboard (Gamma) cambia l'aggregato del donut: la cache deve
    /// rigenerare, non restituire il grafico della selezione precedente.</summary>
    [Fact]
    public void CambioFiltroGammaDashboard_RigeneraIlPngDelGraficoDonut()
    {
        var home = new HomeViewModel(_state, _ => { });
        var gamma = home.FiltroGammaDashboard.Opzioni.Select(o => o.Valore).FirstOrDefault();
        Assert.NotNull(gamma); // il seed demo deve popolare almeno una Gamma perche' il test sia significativo.

        PerfContatori.Reset();
        home.FiltraDashboardPerGammaCommand.Execute(gamma);

        var dopoCambioFiltro = PerfContatori.LeggiSnapshot();
        Assert.True(dopoCambioFiltro.GraficoPngGenerato >= 1);
    }
}
