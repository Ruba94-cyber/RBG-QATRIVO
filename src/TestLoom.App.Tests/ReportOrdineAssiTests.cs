using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Ordine personalizzato degli assi della matrice Report: funzione di ordinamento, persistenza
/// locale in AppConfig, applicazione a matrice/VM ed export Excel della vista corrente.</summary>
public sealed class ReportOrdineAssiTests
{
    private static readonly string[] Cat = ["Diag. avanzata", "Diag. base", "Programming"];

    [Fact]
    public void Ordina_SenzaOrdine_EAlfabetico()
    {
        Assert.Equal(new[] { "a", "B", "c" }, OrdineAssiReportService.Ordina(["c", "B", "a"], null));
        Assert.Equal(new[] { "a", "B", "c" }, OrdineAssiReportService.Ordina(["c", "B", "a"], []));
    }

    [Fact]
    public void Ordina_OrdinePersonalizzato_RispettaLaPosizioneSalvata()
    {
        var r = OrdineAssiReportService.Ordina(Cat, ["Diag. base", "Diag. avanzata", "Programming"]);
        Assert.Equal(new[] { "Diag. base", "Diag. avanzata", "Programming" }, r);
    }

    [Fact]
    public void Ordina_ValoriNuoviInCodaInOrdineAlfabetico_ERimossiIgnorati()
    {
        var r = OrdineAssiReportService.Ordina(["Zeta", "Programming", "Alfa", "Diag. base"], ["Programming", "Scomparso", "Diag. base"]);
        Assert.Equal(new[] { "Programming", "Diag. base", "Alfa", "Zeta" }, r);
    }

    [Fact]
    public void Ordina_ConfrontoSenzaDistinzioneMaiuscole()
    {
        Assert.Equal(new[] { "b", "A" }, OrdineAssiReportService.Ordina(["A", "b"], ["B", "a"]));
    }

    [Fact]
    public void Sposta_SuGiuEFuoriLimite()
    {
        var l = new List<string> { "a", "b", "c" };
        Assert.Equal(0, OrdineAssiReportService.Sposta(l, 1, -1));
        Assert.Equal(new[] { "b", "a", "c" }, l);
        Assert.Equal(0, OrdineAssiReportService.Sposta(l, 0, -1));
        Assert.Equal(2, OrdineAssiReportService.Sposta(l, 1, +1));
        Assert.Equal(2, OrdineAssiReportService.Sposta(l, 2, +1));
        Assert.Equal(new[] { "b", "c", "a" }, l);
    }

    [Fact]
    public void Unisci_MantieneInCodaIValoriSalvatiNonPresentiNelDialogo()
    {
        var r = OrdineAssiReportService.Unisci(["B", "A"], ["X", "A", "Y"]);
        Assert.Equal(new[] { "B", "A", "X", "Y" }, r);
    }

    [Fact]
    public void AppConfig_RoundTrip_PerOperatoreRuoloEDimensione()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoom_OrdAssi_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "config.json");
            var service = new AppConfigService(path);
            var config = new AppConfig { OperatoreAttivo = "Mario" };
            Assert.True(OrdineAssiReportService.Imposta(config, RuoloOperatore.Master, DimensioniMatrice.CategoriaFunzionalita, ["Programming", "Diag. base"]));
            Assert.False(OrdineAssiReportService.Imposta(config, RuoloOperatore.Master, DimensioniMatrice.CategoriaFunzionalita, ["Programming", "Diag. base"]));
            service.Save(config);

            var caricata = new AppConfigService(path).Load();
            Assert.Equal(new[] { "Programming", "Diag. base" },
                OrdineAssiReportService.Leggi(caricata, RuoloOperatore.Master, DimensioniMatrice.CategoriaFunzionalita));
            // Altro ruolo/altra dimensione/altro operatore: nessun ordine.
            Assert.Null(OrdineAssiReportService.Leggi(caricata, RuoloOperatore.Viewer, DimensioniMatrice.CategoriaFunzionalita));
            Assert.Null(OrdineAssiReportService.Leggi(caricata, RuoloOperatore.Master, DimensioniMatrice.Componente));
            caricata.OperatoreAttivo = "Luigi";
            Assert.Null(OrdineAssiReportService.Leggi(caricata, RuoloOperatore.Master, DimensioniMatrice.CategoriaFunzionalita));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Imposta_Vuoto_RimuoveLOrdine()
    {
        var config = new AppConfig { OperatoreAttivo = "Mario" };
        OrdineAssiReportService.Imposta(config, RuoloOperatore.Master, DimensioniMatrice.Componente, ["B", "A"]);
        Assert.True(OrdineAssiReportService.Imposta(config, RuoloOperatore.Master, DimensioniMatrice.Componente, []));
        Assert.Empty(config.OrdineAssiReport);
        Assert.False(OrdineAssiReportService.Imposta(config, RuoloOperatore.Master, DimensioniMatrice.Componente, null));
    }

    private static TestCase T(string id, string componente, string funz) => new()
    {
        Id = id, Componente = new EcuSelection([componente]), Funzionalita = [funz]
    };

    private static readonly List<Funzionalita> Funzioni =
    [
        new() { Nome = "F1", Categorie = ["Diag. avanzata"] },
        new() { Nome = "F2", Categorie = ["Diag. base"] },
        new() { Nome = "F3", Categorie = ["Programming"] }
    ];

    [Fact]
    public void Genera_ApplicaOrdineSuColonneERighe_ETotaliRestanoAllineati()
    {
        var tests = new[] { T("1", "BCM", "F1"), T("2", "EDC", "F2"), T("3", "BCM", "F3") };
        var config = new ConfigurazioneMatriceReport
        {
            AsseX = DimensioniMatrice.CategoriaFunzionalita, AsseY = DimensioniMatrice.Componente,
            OrdineAsseX = ["Programming", "Diag. base"], OrdineAsseY = ["EDC", "BCM"]
        };
        var r = ReportMatriceService.Genera(tests, config, [], Funzioni, []);
        Assert.Equal(new[] { "Programming", "Diag. base", "Diag. avanzata" }, r.Colonne);
        Assert.Equal(new[] { "EDC", "BCM" }, r.Righe.Select(x => x.Riga));
        Assert.Equal(r.Colonne.Count, r.TotaliColonna.Count);
        // La cella (BCM, Programming) e' nella colonna 0 della riga BCM, con il suo conteggio.
        var bcm = r.Righe.Single(x => x.Riga == "BCM");
        Assert.Equal("Programming", bcm.Celle[0].Colonna);
        Assert.Equal(1, bcm.Celle[0].Totali);
        Assert.Equal(0, bcm.Celle[1].Totali);
    }

    [Fact]
    public void Riordina_SuAggregatoEsistente_NonCambiaIConteggi()
    {
        var tests = new[] { T("1", "BCM", "F1"), T("2", "EDC", "F2") };
        var config = new ConfigurazioneMatriceReport { AsseX = DimensioniMatrice.CategoriaFunzionalita, AsseY = DimensioniMatrice.Componente };
        var prep = ReportMatriceService.Prepara(tests, config, [], Funzioni, []);
        Assert.Equal(new[] { "Diag. avanzata", "Diag. base" }, prep.TutteLeColonne);
        var generaleDopo = prep.Completa().TotaleGenerale.Testo;
        prep.Riordina(["Diag. base"], null);
        Assert.Equal(new[] { "Diag. base", "Diag. avanzata" }, prep.TutteLeColonne);
        Assert.Equal(generaleDopo, prep.Completa().TotaleGenerale.Testo);
        prep.Riordina(null, null);
        Assert.Equal(new[] { "Diag. avanzata", "Diag. base" }, prep.TutteLeColonne);
    }

    [Fact]
    public void Ordina_ScalaGrande_RestaVeloce()
    {
        var valori = Enumerable.Range(0, 200_000).Select(i => "V" + (i * 7919 % 200_000)).ToList();
        var ordine = valori.Take(1000).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = OrdineAssiReportService.Ordina(valori, ordine);
        sw.Stop();
        Assert.Equal(ordine, r.Take(1000));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Ordina 200k valori: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ViewModel_ImpostaOrdine_CambiaLOrdineInVistaEdInExcel_ERipristina()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoomOrdAssi_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(dir);
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        var xlsx = Path.Combine(dir, "ordine.xlsx");
        try
        {
            var vm = new ReportViewModel(state);
            vm.AggiornaDimensioniViewportMatrice(larghezza: 4_000, altezza: 2_000);
            var alfabetico = vm.ValoriOrdinabili(false).ToList();
            Assert.True(alfabetico.Count >= 2, "il dataset demo deve avere almeno 2 colonne");

            var inverso = Enumerable.Reverse(alfabetico).ToList();
            vm.ImpostaOrdineAsse(false, inverso);
            Assert.Equal(inverso.Take(12), vm.ColonneMatrice);
            Assert.Equal(inverso, vm.ValoriOrdinabili(false));
            Assert.NotEmpty(state.Config.OrdineAssiReport);

            // Excel della vista corrente: stessa sequenza di colonne.
            await vm.EsportaExcelVerso(xlsx);
            Assert.Equal(inverso, IntestazioniColonneExcel(xlsx, inverso.Count));

            // Un nuovo ViewModel (nuova sessione) rilegge l'ordine da AppConfig.
            var vm2 = new ReportViewModel(state);
            vm2.AggiornaDimensioniViewportMatrice(larghezza: 4_000, altezza: 2_000);
            Assert.Equal(inverso, vm2.ValoriOrdinabili(false));

            vm.ImpostaOrdineAsse(false, []);
            Assert.Equal(alfabetico, vm.ValoriOrdinabili(false));
            Assert.Empty(state.Config.OrdineAssiReport);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ViewModel_ImpostaOrdine_SalvataggioFallito_AggiornaComunqueLaVista()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TestLoomOrdAssiKo_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(dir);
        using var state = TestStateFactory.Create();
        Directory.CreateDirectory(dir);
        state.InizializzaCartella(dir);
        try
        {
            var vm = new ReportViewModel(state);
            vm.AggiornaDimensioniViewportMatrice(larghezza: 4_000, altezza: 2_000);
            var inverso = Enumerable.Reverse(vm.ValoriOrdinabili(false).ToList()).ToList();
            // config.json sostituito da una cartella: Save lancia.
            var cfg = Path.Combine(state.ConfigService.CartellaLocale, "config.json");
            if (File.Exists(cfg)) File.Delete(cfg);
            Directory.CreateDirectory(cfg);
            vm.ImpostaOrdineAsse(false, inverso);
            Assert.Equal(inverso.Take(12), vm.ColonneMatrice);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static List<string> IntestazioniColonneExcel(string percorso, int attese)
    {
        using var wb = new XLWorkbook(percorso);
        var ws = wb.Worksheet("Matrice configurabile");
        var risultato = new List<string>();
        for (var c = 0; c < attese; c++)
            risultato.Add(ws.Cell(4, c + 2).GetString().Split('\n')[0]);
        return risultato;
    }
}
