using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using SkiaSharp;

namespace TestLoom.App.Tests;

/// <summary>Foglio "Grafici" dell'export Excel del Report (Category "Grafici"): tre immagini ad alta risoluzione, filtri
/// attivi e data in testa, tabelle dei dati coerenti con lo stesso filtro dell'export.</summary>
[Trait("Category", "Grafici")]
public class GraficiExcelFoglioTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public GraficiExcelFoglioTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomGraficiExcel_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void FoglioGrafici_TreImmaginiAdAltaRisoluzioneEFiltriInTesta()
    {
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Grafici");
        var loc = LocalizationService.Instance;

        Assert.Equal(3, ws.Pictures.Count());
        Assert.Equal(loc.Traduci("FoglioGraficiTitolo"), ws.Cell(1, 1).GetString());
        Assert.Contains(loc.Traduci("GraficoNessunFiltro"), ws.Cell(2, 1).GetString());
        Assert.Contains("Operatore Prova A", ws.Cell(3, 1).GetString());
        foreach (var immagine in ws.Pictures)
        {
            using var bmp = SKBitmap.Decode(immagine.ImageStream);
            Assert.NotNull(bmp);
            Assert.True(bmp.Width >= GraficoRenderer.LarghezzaMinimaEsportazionePx, $"immagine {bmp.Width} px");
        }
    }

    [Fact]
    public void FoglioGrafici_ConFiltroEcu_TabellaCoperturaUgualeALoSnapshotDelFiltro()
    {
        var componente = _state.TestCases.GetAll().SelectMany(tc => tc.Componente).Distinct().First();
        var filtro = new ReportFilter { Componente = new List<string> { componente } };
        var bytes = ReportService.GeneraExcel(filtro, _state, "Operatore Prova A");

        var testFiltrati = _state.TestCases.GetAll().Where(filtro.Corrisponde).ToList();
        var attesi = GraficiDatiService.PerReport(_state.Master.Requisiti.Load(), testFiltrati,
            _state.NonConformita.GetByTestIds(testFiltrati.Select(tc => tc.Id).ToList()));
        var copertura = Assert.IsType<DatiCopertura>(attesi[0].Dati);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Grafici");
        Assert.Contains(componente, ws.Cell(2, 1).GetString());

        var riga = TrovaRiga(ws, "Conforme");
        Assert.Equal(copertura.Conformi, ws.Cell(riga, 2).GetValue<long>());
        Assert.Equal(copertura.ConformiConDeviazioni, ws.Cell(riga + 1, 2).GetValue<long>());
        Assert.Equal(copertura.NonConformi, ws.Cell(riga + 2, 2).GetValue<long>());
        Assert.Equal(copertura.NonTestati, ws.Cell(riga + 3, 2).GetValue<long>());
    }

    [Fact]
    public void FoglioGrafici_TabellaEsitiConLaRigaTotaleDelloSnapshot()
    {
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        var tuttiTest = _state.TestCases.GetAll().ToList();
        var attesi = GraficiDatiService.PerReport(_state.Master.Requisiti.Load(), tuttiTest, _state.NonConformita.GetAll());
        var esiti = Assert.IsType<DatiEsiti>(attesi[2].Dati);

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Grafici");
        var rigaTotale = Enumerable.Range(1, ws.LastRowUsed()!.RowNumber())
            .Last(r => ws.Cell(r, 1).GetString() == LocalizationService.Instance.Traduci("FoglioGraficiTabellaTotale"));
        Assert.Equal(esiti.PerVeicoloEsito.Sum(x => x.Valore), ws.Cell(rigaTotale, 2).GetValue<long>());
    }

    [Fact]
    public void FoglioGrafici_ImmaginiPiazzateSottoLeAltreSenzaSovrapporreLeTabelle()
    {
        var bytes = ReportService.GeneraExcel(new ReportFilter(), _state, "Operatore Prova A");
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Grafici");
        var ancore = ws.Pictures.Select(p => p.TopLeftCell.Address.RowNumber).ToList();
        Assert.Equal(ancore.OrderBy(r => r), ancore);
        Assert.True(ancore[1] - ancore[0] >= 20, $"immagini troppo vicine: righe {string.Join(", ", ancore)}");
        Assert.True(ancore[2] - ancore[1] >= 20, $"immagini troppo vicine: righe {string.Join(", ", ancore)}");
    }

    private static int TrovaRiga(IXLWorksheet ws, string testo)
    {
        var ultima = ws.LastRowUsed()!.RowNumber();
        for (var r = 1; r <= ultima; r++)
            if (ws.Cell(r, 1).GetString() == testo) return r;
        throw new InvalidOperationException($"Riga «{testo}» non trovata nel foglio Grafici.");
    }
}
