using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using System.IO.Compression;

namespace TestLoom.App.Tests;

/// <summary>Export Report completo sul dataset demo: fogli per elaborazioni successive, grafici Excel nativi accanto alle immagini e
/// file valido per l'OpenXml SDK (Excel apre solo pacchetti ben formati).</summary>
[Trait("Category", "Lento")]
public sealed class ReportExportCompletoTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public ReportExportCompletoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomExportCompleto_" + Guid.NewGuid().ToString("N"));
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void ExportCompleto_HaFogliDatiRiepilogoInfoEGraficiNativi()
    {
        var bytes = ReportService.GeneraExcelVista(new ReportFilter(), _state, "Operatore Prova A");
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var nomi = wb.Worksheets.Select(w => w.Name).ToList();
            var loc = LocalizationService.Instance;
            Assert.Contains(loc.Traduci("ExpFoglioDati"), nomi);
            Assert.Contains(loc.Traduci("ExpFoglioRiepilogo"), nomi);
            Assert.Contains(loc.Traduci("ExpFoglioInfo"), nomi);
            Assert.Contains("Grafici", nomi);
            var dati = wb.Worksheet(loc.Traduci("ExpFoglioDati"));
            Assert.Equal("Test UID", dati.Cell(1, 1).GetString());
            Assert.True(dati.LastRowUsed()!.RowNumber() > 1);
        }

        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var graficiSheet = doc.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single(s => s.Name == "Grafici");
        var wsPart = (WorksheetPart)doc.WorkbookPart.GetPartById(graficiSheet.Id!.Value!);
        Assert.Equal(3, wsPart.DrawingsPart!.ChartParts.Count());
        Assert.True(wsPart.DrawingsPart.ImageParts.Count() >= 3, "le immagini dei grafici restano accanto ai grafici nativi");

        var errori = new OpenXmlValidator().Validate(doc).Take(5).Select(e => e.Description).ToList();
        Assert.True(errori.Count == 0, string.Join(" | ", errori));
    }

    private static ExportConfigurazione Scelta(string[] sezioni, params (string Sezione, string[] Colonne)[] colonne)
    {
        var c = new ExportConfigurazione { Nome = "t", Sezioni = sezioni.ToList() };
        foreach (var (s, k) in colonne) c.Colonne[s] = k.ToList();
        return c;
    }

    private List<string> Fogli(ExportConfigurazione scelta, out XLWorkbook wb)
    {
        var bytes = ReportService.GeneraExcelVista(new ReportFilter(), _state, "Op", null, null, CancellationToken.None, scelta);
        wb = new XLWorkbook(new MemoryStream(bytes));
        return wb.Worksheets.Select(w => w.Name).ToList();
    }

    [Fact]
    public void PivotPersonalizzata_UsaFonteEAssiSceltiNelFilePrincipale()
    {
        var scelta = Scelta(["vista", "pivot"]);
        scelta.PivotFonte = "categorie";
        scelta.PivotRighe = "categoria";
        scelta.PivotColonne = "stato";
        scelta.PivotPercentuale = "colonna";
        Fogli(scelta, out var wb);
        using (wb)
        {
            var ws = wb.Worksheet("Pivot");
            var pivot = Assert.Single(ws.PivotTables);
            Assert.Contains(pivot.RowLabels, f => f.SourceName == LocalizationService.Instance.Scegli("Categoria", "Category", "Catégorie"));
            Assert.Contains(pivot.ColumnLabels, f => f.SourceName == LocalizationService.Instance.Scegli("Stato", "Status", "Statut"));
            Assert.Single(wb.Worksheets, w => w.PivotTables.Any());
            foreach (var nome in ReportPivotStandardTests.NomiFogliStandard()) Assert.Contains(wb.Worksheets, w => w.Name == nome);
        }
    }

    [Fact]
    public void PivotPersonalizzata_CoreUsaStatoSenzaCategoria()
    {
        var scelta = Scelta(["vista", "pivot"]);
        scelta.PivotFonte = "core";
        scelta.PivotRighe = "componente";
        scelta.PivotColonne = "stato";
        scelta.PivotPercentuale = "totale";
        Fogli(scelta, out var wb);
        using (wb)
        {
            var pivot = Assert.Single(wb.Worksheet("Pivot").PivotTables);
            var loc = LocalizationService.Instance;
            Assert.Contains(pivot.RowLabels, f => f.SourceName == "Componente");
            Assert.Contains(pivot.ColumnLabels, f => f.SourceName == loc.Scegli("Stato", "Status", "Statut"));
            Assert.Contains(pivot.Values, v => v.Calculation == XLPivotCalculation.PercentageOfTotal);
            var origine = wb.Worksheet(loc.Scegli("Origine pivot (2)", "Pivot source (2)", "Source pivot (2)"));
            Assert.DoesNotContain(origine.Row(1).CellsUsed(), c => c.GetString() == loc.Scegli("Categoria", "Category", "Catégorie"));
            Assert.Contains(origine.Row(1).CellsUsed(), c => c.GetString() == loc.Scegli("Stato", "Status", "Statut"));
        }
    }

    [Fact]
    public async Task ReportPartizionato_ConservaSezioniColonneEPivotScelte()
    {
        var vm = new TestLoom.App.ViewModels.ReportViewModel(_state);
        await vm.AttendiAggiornamentoAsync();
        var (filtro, config, origine) = await vm.PreparaOrigineExportAsync();
        Assert.True(origine.Righe.Count > 1);
        var scelta = Scelta(["dati", "pivot"], ("dati", ["uid", "test"]));
        scelta.PivotFonte = "core";
        scelta.PivotRighe = "componente";
        scelta.PivotColonne = "stato";
        scelta.PivotPercentuale = "colonna";
        using var output = new MemoryStream();
        ReportService.ScriviExcelVistaPartizionato(output, filtro, _state, "Op", config, origine.Righe,
            origine.TotaleCompleto, Math.Max(1, origine.Righe.Count / 2), CancellationToken.None, origine, scelta);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.True(zip.Entries.Count >= 2);
        // Le pivot (standard e personalizzata) dell'universo completo sono calcolate una sola volta e scritte nella sola parte 1.
        var voci = zip.Entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < voci.Count; i++)
        {
            using var stream = voci[i].Open();
            using var wb = new XLWorkbook(stream);
            Assert.DoesNotContain(wb.Worksheets, w => w.Name == "Matrice configurabile" || w.Name == "Grafici");
            Assert.Equal(2, wb.Worksheet(LocalizationService.Instance.Traduci("ExpFoglioDati")).LastColumnUsed()!.ColumnNumber());
            if (i > 0)
            {
                Assert.DoesNotContain(wb.Worksheets, w => w.Name == "Pivot");
                foreach (var nome in ReportPivotStandardTests.NomiFogliStandard())
                    Assert.DoesNotContain(wb.Worksheets, w => w.Name == nome);
                continue;
            }
            var pivot = Assert.Single(wb.Worksheet("Pivot").PivotTables);
            Assert.Contains(pivot.RowLabels, f => f.SourceName == "Componente");
            Assert.Contains(pivot.Values, v => v.Calculation == XLPivotCalculation.PercentageOfColumn);
            foreach (var nome in ReportPivotStandardTests.NomiFogliStandard())
                Assert.Contains(wb.Worksheets, w => w.Name == nome);
        }
    }

    [Fact]
    public void Scelta_SoloVistaEDatiConTreColonne_ToglieIlResto()
    {
        var nomi = Fogli(Scelta(["vista", "dati"], ("dati", ["uid", "test", "gamma"])), out var wb);
        using (wb)
        {
            var loc = LocalizationService.Instance;
            Assert.Contains("Matrice configurabile", nomi);
            Assert.Contains(loc.Traduci("ExpFoglioDati"), nomi);
            Assert.DoesNotContain("Grafici", nomi);
            Assert.DoesNotContain("NC matrice", nomi);
            Assert.DoesNotContain(loc.Traduci("ExpFoglioRiepilogo"), nomi);
            Assert.DoesNotContain(loc.Traduci("ExpFoglioInfo"), nomi);
            Assert.DoesNotContain(nomi, n => n.StartsWith(loc.Traduci("OrigineFoglioOrigine"))
                && n != loc.Scegli("Origine pivot", "Pivot source", "Source pivot")
                && n != loc.Scegli("Origine categorie", "Category source", "Source catégories"));
            var dati = wb.Worksheet(loc.Traduci("ExpFoglioDati"));
            Assert.Equal(3, dati.LastColumnUsed()!.ColumnNumber());
            Assert.Equal(new[] { "Test UID", loc.Traduci("OrigineColTestId"), loc.Traduci("OrigineColGamma") },
                Enumerable.Range(1, 3).Select(c => dati.Cell(1, c).GetString()));
        }
    }

    [Fact]
    public void Scelta_RiepilogoSenzaColonnaGamma_NonHaIlBloccoGammaMaHaGliAltri()
    {
        var nomi = Fogli(Scelta(["riepilogo"], ("dati", ["test", "veicolo", "determina"])), out var wb);
        using (wb)
        {
            var loc = LocalizationService.Instance;
            // Il riepilogo legge il foglio Dati: viene scritto anche se non scelto.
            Assert.Contains(loc.Traduci("ExpFoglioDati"), nomi);
            var ws = wb.Worksheet(loc.Traduci("ExpFoglioRiepilogo"));
            var testo = string.Join("|", ws.CellsUsed().Select(c => c.GetString()));
            Assert.Contains(loc.Traduci("ExpRiepilogoPerVeicolo"), testo);
            Assert.DoesNotContain(loc.Traduci("ExpRiepilogoPerGamma"), testo);
            Assert.DoesNotContain(loc.Traduci("ExpRiepilogoGammaPerStato"), testo);
        }
    }

    [Fact]
    public void Scelta_SoloGrafici_TieneGliOggettiGraficiEValidaIlPacchetto()
    {
        var bytes = ReportService.GeneraExcelVista(new ReportFilter(), _state, "Op", null, null, CancellationToken.None, Scelta(["grafici"]));
        using var ms = new MemoryStream(bytes);
        using var doc = SpreadsheetDocument.Open(ms, false);
        var nomi = doc.WorkbookPart!.Workbook.Descendants<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(s => s.Name!.Value).ToList();
        Assert.Contains("Grafici", nomi);
        foreach (var nome in ReportPivotStandardTests.NomiFogliStandard()) Assert.Contains(nome, nomi);
        Assert.Empty(new OpenXmlValidator().Validate(doc).Take(3).Select(e => e.Description));
    }

    [Fact]
    public void Scelta_SoloPivot_LAExcelPrincipaleHaAlmenoIlFoglioInfo()
    {
        var nomi = Fogli(Scelta(["pivot"]), out var wb);
        using (wb)
        {
            Assert.Contains(LocalizationService.Instance.Traduci("ExpFoglioInfo"), nomi);
            Assert.Single(wb.Worksheets, w => w.PivotTables.Any());
            foreach (var nome in ReportPivotStandardTests.NomiFogliStandard()) Assert.Contains(nomi, n => n == nome);
        }
    }
}
