using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace TestLoom.App.Tests;

/// <summary>Piano 6.5.0 punto 5: tipo di rappresentazione selezionabile per ciascun grafico della Home.
/// Catalogo (tipi compatibili col dato), aggregazione "Altri", layout treemap, rendering di ogni tipo e
/// integrazione col ViewModel (chiave cache = dati + tipo, nessuna query al cambio tipo, preferenza locale).</summary>
public sealed class HomeGraficiTipoCatalogoTests
{
    [Fact]
    public void Compatibili_DatiCategorici_NonOffronoSerieTemporali()
    {
        foreach (var id in new[] { HomeChartCatalog.IdCopertura })
        {
            var tipi = HomeChartCatalog.Compatibili(id);
            Assert.DoesNotContain(TipoGrafico.Linea, tipi);
            Assert.DoesNotContain(TipoGrafico.Area, tipi);
            Assert.Contains(TipoGrafico.Torta, tipi);
            Assert.Contains(TipoGrafico.Treemap, tipi);
            Assert.Contains(TipoGrafico.BarreOrizzontali, tipi);
        }
    }

    [Fact]
    public void Compatibili_EsitiPerVeicolo_OffreTuttiITipi()
    {
        Assert.Equal(new[] { TipoGrafico.Ciambella, TipoGrafico.Torta, TipoGrafico.BarreVerticali, TipoGrafico.BarreOrizzontali,
            TipoGrafico.Treemap, TipoGrafico.Linea, TipoGrafico.Area }, HomeChartCatalog.Compatibili(HomeChartCatalog.IdDonut));
    }

    [Fact]
    public void Compatibili_SerieTemporale_SoloLineaEArea()
    {
        Assert.Equal(new[] { TipoGrafico.Linea, TipoGrafico.Area }, HomeChartCatalog.Compatibili(HomeChartCatalog.IdAndamento));
    }

    [Fact]
    public void Compatibili_IlPrimoTipoERaIlPredefinitoStorico()
    {
        Assert.Equal(TipoGrafico.BarreVerticali, HomeChartCatalog.Predefinito(HomeChartCatalog.IdCopertura));
        Assert.Equal(TipoGrafico.Linea, HomeChartCatalog.Predefinito(HomeChartCatalog.IdAndamento));
        Assert.Equal(TipoGrafico.Ciambella, HomeChartCatalog.Predefinito(HomeChartCatalog.IdDonut));
        Assert.Empty(HomeChartCatalog.Compatibili("inesistente"));
    }

    [Theory]
    [InlineData("donut", "Treemap", TipoGrafico.Treemap)]
    [InlineData("donut", "Linea", TipoGrafico.Linea)]
    [InlineData("donut", "Boh", TipoGrafico.Ciambella)]         // sconosciuto -> predefinito
    [InlineData("andamento", "Treemap", TipoGrafico.Linea)]     // non compatibile -> predefinito
    [InlineData("andamento", "Area", TipoGrafico.Area)]
    [InlineData("copertura", "XYZ", TipoGrafico.BarreVerticali)] // sconosciuto -> predefinito
    [InlineData("copertura", null, TipoGrafico.BarreVerticali)]
    [InlineData("copertura", "", TipoGrafico.BarreVerticali)]
    public void Normalizza_ValoreSalvatoNonValidoTornaAlPredefinito(string id, string? salvato, TipoGrafico atteso)
    {
        Assert.Equal(atteso, HomeChartCatalog.Normalizza(id, salvato));
    }

    [Fact]
    public void RaggruppaAltri_SottoSoglia_NessunAltri_EScartaIZeri()
    {
        var r = HomeChartCatalog.RaggruppaAltri(new[] { ("B", 5L), ("A", 5L), ("C", 0L), ("D", 9L) }, 5);
        Assert.Equal(new[] { ("D", 9L), ("A", 5L), ("B", 5L) }, r);
    }

    [Fact]
    public void RaggruppaAltri_SopraSoglia_SommaIlResto_ConservaIlTotale()
    {
        var voci = Enumerable.Range(1, 1000).Select(i => ($"V{i:D4}", (long)i)).ToList();
        var r = HomeChartCatalog.RaggruppaAltri(voci, 12);
        Assert.Equal(13, r.Count);
        Assert.Equal(HomeChartCatalog.EtichettaAltri, r[^1].Nome);
        Assert.Equal("V1000", r[0].Nome);
        Assert.Equal(voci.Sum(v => v.Item2), r.Sum(v => v.Valore));
        Assert.Equal(Enumerable.Range(1, 988).Sum(i => (long)i), r[^1].Valore);
    }

    [Fact]
    public void RaggruppaAltri_EsattamenteLaSoglia_NessunAltri()
    {
        var voci = Enumerable.Range(1, 12).Select(i => ($"V{i}", (long)i)).ToList();
        Assert.Equal(12, HomeChartCatalog.RaggruppaAltri(voci, 12).Count);
    }

    [Fact]
    public void LayoutTreemap_AreeProporzionaliERiempimentoCompleto()
    {
        var valori = new long[] { 60, 30, 20, 10, 5, 5, 1 };
        var area = new SKRect(20, 50, 880, 465);
        var rett = ChartRenderer.LayoutTreemap(valori, area);
        Assert.Equal(valori.Length, rett.Length);
        var areaTot = (double)area.Width * area.Height;
        var somma = valori.Sum();
        for (int i = 0; i < valori.Length; i++)
        {
            Assert.True(rett[i].Left >= area.Left - 0.01f && rett[i].Right <= area.Right + 0.01f);
            Assert.True(rett[i].Top >= area.Top - 0.01f && rett[i].Bottom <= area.Bottom + 0.01f);
            Assert.Equal(areaTot * valori[i] / somma, (double)rett[i].Width * rett[i].Height, areaTot * 1e-4);
        }
        Assert.Equal(areaTot, rett.Sum(r => (double)r.Width * r.Height), areaTot * 1e-4);
    }

    [Fact]
    public void LayoutTreemap_InputVuotoOZero_NonLanciaEccezioni()
    {
        Assert.Empty(ChartRenderer.LayoutTreemap(Array.Empty<long>(), new SKRect(0, 0, 100, 100)));
        Assert.Single(ChartRenderer.LayoutTreemap(new long[] { 0 }, new SKRect(0, 0, 100, 100)));
    }

    [Fact]
    public void LayoutTreemap_ValoriZeroONegativi_RettangoloVuoto_SenzaNaNEdAltriInvariati()
    {
        var area = new SKRect(0, 0, 200, 100);
        var rett = ChartRenderer.LayoutTreemap(new long[] { 50, 30, 0, 20, 0, -5 }, area);
        Assert.Equal(6, rett.Length);
        Assert.All(rett, r => Assert.True(float.IsFinite(r.Left) && float.IsFinite(r.Top) && float.IsFinite(r.Right) && float.IsFinite(r.Bottom)));
        Assert.Equal(0f, rett[2].Width * rett[2].Height);
        Assert.Equal(0f, rett[4].Width * rett[4].Height);
        Assert.Equal(0f, rett[5].Width * rett[5].Height);
        var pos = new[] { rett[0], rett[1], rett[3] };
        Assert.Equal(200.0 * 100, pos.Sum(r => (double)r.Width * r.Height), 1.0);
        Assert.Equal(200.0 * 100 * 0.5, (double)rett[0].Width * rett[0].Height, 1.0);
    }

    [Fact]
    public void Esiti_TortaRiempieIlCentro_MentreCiambellaMantieneIlForo()
    {
        const float larghezza = 900;
        const float altezza = 480;
        const long conteggio = 5;
        var righe = new[] { ("V1", Esito.Conforme, conteggio) };
        var loc = LocalizationService.Instance;
        var vociLegenda = new List<VoceLegenda>
        {
            new($"{loc.Traduci("GraficoVeicoloLabel")} ({loc.Traduci("GraficoTotaleLabel")}: {conteggio})", null, true),
            new(ChartRenderer.EtichettaVoce("V1", conteggio, conteggio, 28), SKColors.Red),
            new(loc.Traduci("GraficoEsitoLabel"), null, true),
            new(ChartRenderer.EtichettaVoce(Esito.Conforme.Label(), conteggio, conteggio, 40), SKColors.Red),
        };
        using var font = ChartRenderer.CreaFont(ChartRenderer.FontMinimoDip);
        var corpo = ChartRenderer.DisponiLegenda(larghezza, altezza, 46,
            vociLegenda, font, (float)Math.Max(180d, 0.8d * (altezza - 46 - 12))).Corpo;
        var xCentro = (int)corpo.MidX;
        var yCentro = (int)corpo.MidY;

        using var torta = Assert.IsType<SKBitmap>(SKBitmap.Decode(
            ChartRenderer.SottoveicoloEsitiPerTipo(TipoGrafico.Torta, "Esiti", righe, larghezza, altezza)));
        using var ciambella = Assert.IsType<SKBitmap>(SKBitmap.Decode(
            ChartRenderer.SottoveicoloEsitiPerTipo(TipoGrafico.Ciambella, "Esiti", righe, larghezza, altezza)));

        Assert.NotEqual(SKColors.White, torta.GetPixel(xCentro, yCentro));
        Assert.Equal(SKColors.White, ciambella.GetPixel(xCentro, yCentro));
    }

    private static bool EPng(byte[] b) => b.Length > 100 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';

    [Theory]
    [InlineData(TipoGrafico.BarreVerticali)]
    [InlineData(TipoGrafico.BarreOrizzontali)]
    [InlineData(TipoGrafico.Torta)]
    [InlineData(TipoGrafico.Treemap)]
    public void Copertura_OgniTipoProduceUnPng(TipoGrafico tipo)
    {
        var bucket = new CoverageBucket { Conformi = 10, ConformiConDeviazioni = 3, NonConformi = 2, NonTestati = 7 };
        Assert.True(EPng(ChartRenderer.CoperturaPerTipo(tipo, "Copertura", bucket)));
        Assert.True(EPng(ChartRenderer.CoperturaPerTipo(tipo, "Copertura vuota", new CoverageBucket())));
    }

    [Theory]
    [InlineData(TipoGrafico.Ciambella)]
    [InlineData(TipoGrafico.Torta)]
    [InlineData(TipoGrafico.BarreOrizzontali)]
    [InlineData(TipoGrafico.Treemap)]
    public void Sottoveicolo_OgniTipoProduceUnPng_AncheConMilleCategorie(TipoGrafico tipo)
    {
        var molte = Enumerable.Range(0, 5000).Select(i => ($"Veicolo-{i:D4}", (long)(i % 97) + 1)).ToList();
        var esiti = new List<(Esito Esito, long Valore)> { (Esito.Conforme, 10), (Esito.NonConforme, 3) };
        var sw = Stopwatch.StartNew();
        var png = tipo == TipoGrafico.Ciambella
            ? ChartRenderer.DonutEsitiPerSottoveicolo("Esiti", molte, esiti, 900, 480)
            : ChartRenderer.SottoveicoloPerTipo(tipo, "Esiti", molte);
        Assert.True(EPng(png));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"render {tipo} con 5000 categorie: {sw.ElapsedMilliseconds} ms");
        Assert.True(EPng(ChartRenderer.SottoveicoloPerTipo(tipo == TipoGrafico.Ciambella ? TipoGrafico.Torta : tipo, "Vuoto", Array.Empty<(string, long)>())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Andamento_LineaEAreaProduconoUnPng(bool area)
    {
        var punti = Enumerable.Range(0, 30).Select(i => (new DateTime(2026, 1, 1).AddDays(i), i, i / 2)).ToList();
        Assert.True(EPng(ChartRenderer.AndamentoNonConformita("NC", punti, 900, 380, area)));
        Assert.True(EPng(ChartRenderer.AndamentoNonConformita("NC", Array.Empty<(DateTime, int, int)>(), 900, 380, area)));
    }
}

[Trait("Category", "Lento")]
public sealed class HomeGraficiTipoViewModelTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public HomeGraficiTipoViewModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoom_HomeGraficiTipo_" + Guid.NewGuid().ToString("N"));
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

    private static ChartOption Opzione(HomeViewModel home, string id) => home.ChartOptions.Single(c => c.Id == id);
    private static void ImpostaTipo(ChartOption o, TipoGrafico t) => o.TipoSelezionato = o.TipiDisponibili.Single(x => x.Tipo == t);

    [Fact]
    public void SelettoreTipo_MostraSoloITipiCompatibili_ConAutomationIdStabili()
    {
        var home = new HomeViewModel(_state, _ => { });
        Assert.Equal(new[] { TipoGrafico.Linea, TipoGrafico.Area }, Opzione(home, "andamento").TipiDisponibili.Select(t => t.Tipo));
        Assert.Contains(Opzione(home, "donut").TipiDisponibili, t => t.Tipo == TipoGrafico.Linea);
        Assert.Contains(Opzione(home, "donut").TipiDisponibili, t => t.Tipo == TipoGrafico.Area);
        Assert.DoesNotContain(Opzione(home, "copertura").TipiDisponibili, t => t.Tipo is TipoGrafico.Linea or TipoGrafico.Area);
        Assert.All(home.ChartOptions, o =>
        {
            Assert.True(o.HaSelettoreTipo);
            Assert.Equal($"Home_GraficoTipo_{o.Id}_ComboBox", o.AutomationIdSelettoreTipo);
            Assert.Equal($"Home_Grafico_{o.Id}_CheckBox", o.AutomationIdCheckBox);
            Assert.Equal(HomeChartCatalog.Predefinito(o.Id), o.Tipo);
        });
    }

    [Fact]
    public async Task CambioTipo_RigeneraUnaVolta_SenzaQuery_ERiusaConLoStessoTipo()
    {
        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var donut = Opzione(home, "donut");
        var base0 = PerfContatori.LeggiSnapshot();

        ImpostaTipo(donut, TipoGrafico.Treemap);
        await home.UltimoCambioTipoTask;
        var dopoTreemap = PerfContatori.LeggiSnapshot();
        Assert.Equal(base0.GraficoPngGenerato + 1, dopoTreemap.GraficoPngGenerato);
        // nessuna lettura di Test/cataloghi: riuso degli aggregati gia' calcolati
        Assert.Equal(base0.TestCaseGetAll, dopoTreemap.TestCaseGetAll);
        Assert.Equal(base0.MasterLoad, dopoTreemap.MasterLoad);
        Assert.Equal(base0.LoadCatalog, dopoTreemap.LoadCatalog);
        var grafico = Assert.Single(home.ChartsAttivi);
        Assert.Equal(TipoGrafico.Treemap, grafico.Tipo);

        // Aggiorna() senza cambi di dati col tipo gia' reso: chiave (dati + tipo) invariata -> riuso
        home.Aggiorna();
        var dopoAggiorna = PerfContatori.LeggiSnapshot();
        Assert.Equal(dopoTreemap.GraficoPngGenerato, dopoAggiorna.GraficoPngGenerato);
        Assert.True(dopoAggiorna.GraficoPngRiusato > dopoTreemap.GraficoPngRiusato);

        ImpostaTipo(donut, TipoGrafico.Ciambella);
        await home.UltimoCambioTipoTask;
        Assert.Equal(dopoAggiorna.GraficoPngGenerato + 1, PerfContatori.LeggiSnapshot().GraficoPngGenerato);
        Assert.Equal(TipoGrafico.Ciambella, Assert.Single(home.ChartsAttivi).Tipo);
    }

    [Fact]
    public async Task CambioTipo_DiUnGraficoNonSelezionato_NonRenderizzaNulla_EApplicaAllaSelezione()
    {
        PerfContatori.Reset();
        var home = new HomeViewModel(_state, _ => { });
        var copertura = Opzione(home, "copertura");
        Assert.False(copertura.Selezionato);
        var base0 = PerfContatori.LeggiSnapshot();

        ImpostaTipo(copertura, TipoGrafico.Torta);
        await home.UltimoCambioTipoTask;
        Assert.Equal(base0.GraficoPngGenerato, PerfContatori.LeggiSnapshot().GraficoPngGenerato);

        copertura.Selezionato = true;
        var attivo = home.ChartsAttivi.Single(c => c.Id == "copertura");
        Assert.Equal(TipoGrafico.Torta, attivo.Tipo);
    }

    [Fact]
    public async Task Preferenza_ELocale_Persistita_ERiletta_ENonScrittaNellaCartellaCondivisa()
    {
        var home = new HomeViewModel(_state, _ => { });
        ImpostaTipo(Opzione(home, "donut"), TipoGrafico.BarreOrizzontali);
        await home.UltimoCambioTipoTask;

        var salvato = ViewStateService.Leggi(_state, "Home.TipiGrafico");
        Assert.NotNull(salvato);
        Assert.Contains("BarreOrizzontali", salvato);
        // locale al PC: niente nella cartella condivisa
        Assert.DoesNotContain(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)),
            f => File.ReadAllText(f).Contains("Home.TipiGrafico"));

        var home2 = new HomeViewModel(_state, _ => { });
        Assert.Equal(TipoGrafico.BarreOrizzontali, Opzione(home2, "donut").Tipo);
        Assert.Equal(TipoGrafico.BarreOrizzontali, Assert.Single(home2.ChartsAttivi).Tipo);

        // tornare al predefinito rimuove la preferenza (nessun valore ridondante)
        ImpostaTipo(Opzione(home2, "donut"), TipoGrafico.Ciambella);
        await home2.UltimoCambioTipoTask;
        Assert.Null(ViewStateService.Leggi(_state, "Home.TipiGrafico"));
    }

    [Fact]
    public void Preferenza_NonValidaOIncompatibile_TornaAlPredefinito()
    {
        _state.Config.UltimaSelezionePagine["Home.TipiGrafico"] = "{\"donut\":\"Boh\",\"andamento\":\"Treemap\",\"copertura\":\"Boh\"}";
        var home = new HomeViewModel(_state, _ => { });
        Assert.All(home.ChartOptions, o => Assert.Equal(HomeChartCatalog.Predefinito(o.Id), o.Tipo));

        _state.Config.UltimaSelezionePagine["Home.TipiGrafico"] = "non json";
        var home2 = new HomeViewModel(_state, _ => { });
        Assert.All(home2.ChartOptions, o => Assert.Equal(HomeChartCatalog.Predefinito(o.Id), o.Tipo));
    }

    [Fact]
    public async Task TipoSelezionato_SiCombinaConIFiltriCorrenti()
    {
        var home = new HomeViewModel(_state, _ => { });
        ImpostaTipo(Opzione(home, "donut"), TipoGrafico.Treemap);
        await home.UltimoCambioTipoTask;
        var gamma = home.FiltroGammaDashboard.Opzioni.Select(o => o.Valore).FirstOrDefault();
        Assert.NotNull(gamma);

        PerfContatori.Reset();
        home.FiltraDashboardPerGammaCommand.Execute(gamma);

        // i dati cambiano (nuovo render) ma il tipo scelto resta quello dell'utente
        Assert.True(PerfContatori.LeggiSnapshot().GraficoPngGenerato >= 1);
        Assert.Equal(TipoGrafico.Treemap, Assert.Single(home.ChartsAttivi).Tipo);
    }

    [Fact]
    public async Task CambioTipo_AvbA_ConRenderDiBInVolo_NonSovrascriveA()
    {
        var home = new HomeViewModel(_state, _ => { });
        var donut = Opzione(home, "donut");
        Assert.Equal(TipoGrafico.Ciambella, Assert.Single(home.ChartsAttivi).Tipo);

        var rilascio = new TaskCompletionSource();
        var bloccato = new TaskCompletionSource();
        home.GateRenderPerTest = async t =>
        {
            if (t != TipoGrafico.Treemap) return;
            bloccato.TrySetResult();
            await rilascio.Task;
        };

        ImpostaTipo(donut, TipoGrafico.Treemap);       // A -> B (render di B in volo, bloccato)
        var taskB = home.UltimoCambioTipoTask;
        await bloccato.Task;
        ImpostaTipo(donut, TipoGrafico.Ciambella);     // B -> A mentre B e' in volo
        var taskA = home.UltimoCambioTipoTask;
        rilascio.SetResult();                          // B completa dopo A
        await Task.WhenAll(taskB, taskA);

        Assert.Equal(TipoGrafico.Ciambella, donut.Tipo);
        Assert.Equal(TipoGrafico.Ciambella, Assert.Single(home.ChartsAttivi).Tipo);
    }

    [Fact]
    public async Task CambioTipo_AvbC_ConRenderDiBInVolo_VinceC()
    {
        var home = new HomeViewModel(_state, _ => { });
        var donut = Opzione(home, "donut");
        var rilascio = new TaskCompletionSource();
        var bloccato = new TaskCompletionSource();
        home.GateRenderPerTest = async t =>
        {
            if (t != TipoGrafico.Treemap) return;
            bloccato.TrySetResult();
            await rilascio.Task;
        };
        ImpostaTipo(donut, TipoGrafico.Treemap);
        var taskB = home.UltimoCambioTipoTask;
        await bloccato.Task;
        ImpostaTipo(donut, TipoGrafico.Torta);
        var taskC = home.UltimoCambioTipoTask;
        await taskC;
        rilascio.SetResult();
        await taskB;
        Assert.Equal(TipoGrafico.Torta, Assert.Single(home.ChartsAttivi).Tipo);
    }

    [Fact]
    public async Task CambiTipoRapidi_ConvergonoSull_UltimaScelta()
    {
        var home = new HomeViewModel(_state, _ => { });
        var donut = Opzione(home, "donut");
        ImpostaTipo(donut, TipoGrafico.Treemap);
        var primo = home.UltimoCambioTipoTask;
        ImpostaTipo(donut, TipoGrafico.Torta);
        var secondo = home.UltimoCambioTipoTask;
        await Task.WhenAll(primo, secondo);
        Assert.Equal(TipoGrafico.Torta, Assert.Single(home.ChartsAttivi).Tipo);
    }
}
