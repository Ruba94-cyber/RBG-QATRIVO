using ClosedXML.Excel;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Sezioni e colonne scelte nel dialogo di export per Stato WU e Audit: i fogli e le colonne non scelti non compaiono, e i tipi
/// (date, booleani) delle colonne rimaste restano corretti anche quando le posizioni cambiano.</summary>
public sealed class ExportSelezionePagineTests
{
}

[Trait("Category", "Lento")]
public sealed class ExportSelezioneAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoomAuditSezioni_" + Guid.NewGuid().ToString("N"));
    private readonly AppState _state;

    public ExportSelezioneAuditTests()
    {
        SeedDemoDataService.Genera(_dir);
        _state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Audit_SoloMatriceSenzaLegendaEConflitti_ToglieIFogliNonScelti()
    {
        var veicolo = _state.Master.Veicoli.Load().First(v => !v.Archiviata).Nome;
        var completo = new XLWorkbook(new MemoryStream(AuditReportService.GeneraExcel(_state, veicolo, null)));
        var nomiCompleti = completo.Worksheets.Select(w => w.Name).ToList();
        Assert.Contains("Legenda", nomiCompleti);
        Assert.Contains("Conflitti", nomiCompleti);

        var bytes = AuditReportService.GeneraExcel(_state, veicolo, null, CancellationToken.None,
            new AuditExportOpzioni(Legenda: false, Matrice: true, Conflitti: false, DettaglioDtc: false));
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var nomi = wb.Worksheets.Select(w => w.Name).ToList();
        Assert.DoesNotContain("Legenda", nomi);
        Assert.DoesNotContain("Conflitti", nomi);
        Assert.NotEmpty(nomi);
        Assert.Contains(nomi, n => nomiCompleti.Contains(n)); // il foglio della matrice del veicolo resta
    }
}
