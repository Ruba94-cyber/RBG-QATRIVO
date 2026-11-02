using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Grafici del Report nell'export Excel (Category "Grafici"): stessi dati dei grafici della Home, calcolati sul
/// filtro dell'export; riepilogo dei filtri attivi e formule di andamento ed esiti. La pagina Report non mostra grafici.</summary>
[Trait("Category", "Grafici")]
public class GraficiReportTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public GraficiReportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomGraficiReport_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void ReportFilter_Descrizione_MostraLEtichettaDelCampoNonLaChiave()
    {
        // "progetto-demo" e' la Chiave del campo di sistema "Progetto" (seme demo): nel riepilogo compare l'Etichetta.
        var filtro = new ReportFilter { CampiCustom = { ["progetto-demo"] = ["Phoenix"] } };
        var descrizione = filtro.Descrizione(CampoSistemaCatalogoService.EtichettePerChiave(_state));
        Assert.Contains("Progetto: Phoenix", descrizione);
        Assert.DoesNotContain("progetto-demo", descrizione);

        // Senza etichetta nota ricade sulla chiave (nessun campo mancante nel riepilogo).
        Assert.Contains("progetto-demo: Phoenix", filtro.Descrizione());
    }

    [Fact]
    public void Excel_FoglioGrafici_MostraLEtichettaDelCampoCustom()
    {
        var filtro = new ReportFilter { CampiCustom = { ["progetto-demo"] = ["Phoenix"] } };
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(ReportService.GeneraExcel(filtro, _state, "Operatore Prova")));
        var riepilogo = wb.Worksheet("Grafici").Cell(2, 1).GetString();
        Assert.Contains("Progetto: Phoenix", riepilogo);
        Assert.DoesNotContain("progetto-demo", riepilogo);
    }

    [Fact]
    public void ReportFilter_Descrizione_ElencaIFiltriAttivi()
    {
        var loc = LocalizationService.Instance;
        Assert.Equal(loc.Traduci("GraficoNessunFiltro"), new ReportFilter().Descrizione());

        var filtro = new ReportFilter
        {
            Componente = new List<string> { "Componente-A" },
            Veicoli = new List<string> { "VEH-1" },
            DataDa = new DateTime(2026, 1, 1),
        };
        var descrizione = filtro.Descrizione();
        Assert.Contains("Componente-A", descrizione);
        Assert.Contains("VEH-1", descrizione);
        Assert.Contains("01/01/2026", descrizione);
    }

    [Fact]
    public void Report_PrimoIngresso_NonCopiaLArchivioNc()
    {
        // Spia: il primo ingresso del Report non deve clonare l'archivio NC (GetAll); la pagina non mostra grafici.
        var copiePrima = _state.NonConformita.ChiamateGetAll;
        _ = new ReportViewModel(_state);
        Assert.Equal(copiePrima, _state.NonConformita.ChiamateGetAll);
    }

    [Fact]
    public void AndamentoNc_StessiPuntiDellaFormulaPrecedente()
    {
        var casuale = new Random(20261012);
        for (var caso = 0; caso < 25; caso++)
        {
            var archivio = new List<NonConformita>();
            var quante = caso == 0 ? 0 : casuale.Next(1, 600);
            for (var i = 0; i < quante; i++)
            {
                var apertura = new DateTime(2025, 1, 1).AddDays(casuale.Next(0, 520)).AddHours(casuale.Next(0, 24));
                // Chiusura assente, prima dell'apertura, stesso giorno o successiva: tutti i casi del confronto per giorno.
                DateTime? chiusura = casuale.Next(3) == 0 ? null : apertura.AddDays(casuale.Next(-40, 220)).AddHours(casuale.Next(0, 24));
                archivio.Add(new NonConformita { DataApertura = apertura, DataChiusura = chiusura });
            }
            Assert.Equal(AndamentoNcFormulaPrecedente(archivio), ReportService.AndamentoNc(archivio));
        }
    }

    [Fact]
    public void Esiti_StessiGruppiDellaFormulaPrecedente()
    {
        var casuale = new Random(7);
        // Maiuscole/minuscole diverse, nome vuoto, spazi finali e nome nullo (dato malformato): restano gruppi distinti come con GroupBy.
        var nomi = new string?[] { "VEH-A", "veh-a", "VEH-B", "Veicolo 1", "", null, "VEH-A " };
        var esiti = Enum.GetValues<Esito>();
        for (var caso = 0; caso < 20; caso++)
        {
            var test = new List<TestCase>();
            var quanti = caso == 0 ? 0 : casuale.Next(1, 1500);
            for (var i = 0; i < quanti; i++)
            {
                var tc = new TestCase { Id = $"TC-{i:D6}" };
                var veicoli = casuale.Next(0, 4); // zero: Test senza veicolo
                for (var v = 0; v < veicoli; v++) tc.Veicoli.Add(nomi[casuale.Next(nomi.Length)]!);
                if (casuale.Next(4) > 0) tc.Esecuzioni.Add(new Esecuzione { Esito = esiti[casuale.Next(esiti.Length)] });
                test.Add(tc);
            }
            var attesi = EsitiFormulaPrecedente(test);
            var ottenuti = GraficiDatiService.Esiti(test);
            Assert.Equal(attesi.PerSottoveicolo, ottenuti.PerSottoveicolo);
            Assert.Equal(attesi.PerEsito, ottenuti.PerEsito);
            Assert.Equal(attesi.PerVeicoloEsito, ottenuti.PerVeicoloEsito);
        }
    }

    [Fact]
    public void Donut_CaratteriCondivisiConRenderingInvariato()
    {
        // Spia: il tipo di carattere si risolve una sola volta. Equivalenza: due render dello stesso donut danno gli stessi byte PNG.
        Assert.Same(ChartRenderer.CaratteRegolare(), ChartRenderer.CaratteRegolare());
        Assert.Same(ChartRenderer.CaratteGrassetto(), ChartRenderer.CaratteGrassetto());
        var sottoveicoli = new List<(string Nome, long Valore)> { ("Daily", 3), ("VAN-A", 5), ("Bus", 1) };
        var esiti = new List<(Esito Esito, long Valore)> { (Esito.Conforme, 6), (Esito.NonConforme, 2), (Esito.NonEseguitoNA, 1) };
        var primo = ChartRenderer.DonutEsitiPerSottoveicolo("Esiti per sottoveicolo", sottoveicoli, esiti, 900, 480);
        var secondo = ChartRenderer.DonutEsitiPerSottoveicolo("Esiti per sottoveicolo", sottoveicoli, esiti, 900, 480);
        Assert.Equal(primo, secondo);
    }

    /// <summary>Formula di andamento NC prima dell'ottimizzazione (riferimento per il confronto di equivalenza).</summary>
    private static List<(DateTime Data, int Aperte, int Chiuse)> AndamentoNcFormulaPrecedente(IReadOnlyList<NonConformita> nc)
    {
        if (nc.Count == 0) return new();
        var inizio = nc.Min(n => n.DataApertura).Date;
        var fine = DateTime.Now.Date;
        var punti = new List<(DateTime Data, int Aperte, int Chiuse)>();
        for (var d = inizio; d <= fine; d = d.AddDays(Math.Max(1, (fine - inizio).Days / 20)))
        {
            var aperte = nc.Count(n => n.DataApertura.Date <= d && (n.DataChiusura == null || n.DataChiusura.Value.Date > d));
            var chiuse = nc.Count(n => n.DataChiusura.HasValue && n.DataChiusura.Value.Date <= d);
            punti.Add((d, aperte, chiuse));
        }
        return punti;
    }

    /// <summary>Raggruppamento esiti prima dell'ottimizzazione in un solo passaggio (riferimento per il confronto di equivalenza).</summary>
    private static DatiEsiti EsitiFormulaPrecedente(IEnumerable<TestCase> testCasesContati)
    {
        var perSottoveicolo = testCasesContati.SelectMany(tc => tc.VeicoliEffettivi.DefaultIfEmpty("—")).GroupBy(v => v)
            .Select(g => (Nome: g.Key, Valore: g.LongCount()))
            .OrderByDescending(x => x.Valore).ToList();
        var perEsito = testCasesContati.GroupBy(tc => tc.StatoCorrente ?? Esito.NonEseguitoNA)
            .Select(g => (Esito: g.Key, Valore: g.LongCount())).ToList();
        var perVeicoloEsito = testCasesContati
            .SelectMany(tc => tc.VeicoliEffettivi.Select(v => (Nome: v, Esito: tc.StatoCorrente ?? Esito.NonEseguitoNA)))
            .GroupBy(x => x).Select(g => (g.Key.Nome, g.Key.Esito, Valore: g.LongCount()))
            .OrderBy(x => x.Nome, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Esito).ToList();
        return new DatiEsiti(perSottoveicolo, perEsito, perVeicoloEsito);
    }
}
