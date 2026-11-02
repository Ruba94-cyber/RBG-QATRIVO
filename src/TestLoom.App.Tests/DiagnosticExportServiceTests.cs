using System.IO.Compression;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>DiagnosticExportService (feedback utente 2026-09-17: "è presente un log da poter inviare... genera
/// un sistema di logging circolare") — impacchetta i log applicativi già esistenti (CrashLogService,
/// PerformanceDiagnosticsService, entrambi già bounded/circolari) in un unico zip, senza mai includere i JSON
/// autorevoli (Test/NC/master).</summary>
[Collection("TracciaStatica")]
public class DiagnosticExportServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppState _state;

    public DiagnosticExportServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TestLoomDiagExportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _state = TestStateFactory.Create();
        _state.InizializzaCartella(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void GeneraZip_NonLanciaEProduceUnoZipValidoConManifesto()
    {
        var bytes = DiagnosticExportService.GeneraZip(_state);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var manifesto = archive.GetEntry("manifest.txt");
        Assert.NotNull(manifesto);
        using var reader = new StreamReader(manifesto!.Open());
        var testo = reader.ReadToEnd();
        Assert.Contains("TestLoom - Pacchetto diagnostico", testo);
        Assert.Contains("Nessun file JSON autorevole", testo);
    }

    [Fact]
    public void GeneraZip_IncludeUnErroreRegistratoDaCrashLogService()
    {
        CrashLogService.Registra("Test diagnostico", new InvalidOperationException("errore di prova per il test"));

        var bytes = DiagnosticExportService.GeneraZip(_state);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var crashEntry = archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("crash-log/", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(crashEntry);
        using var reader = new StreamReader(crashEntry!.Open());
        var testo = reader.ReadToEnd();
        Assert.Contains("errore di prova per il test", testo);
    }

    [Fact]
    public void GeneraZip_IncludeLeAzioniUiGiornaliereSenzaPayloadTestuali()
    {
        var destination = Path.Combine(_dir, "ui-trace");
        Directory.CreateDirectory(destination);
        UiInteractionTraceService.DirectoryForTest = destination;
        try
        {
            UiInteractionTraceService.Record("Test", "click", "Test_Export");
            using var archive = new ZipArchive(
                new MemoryStream(DiagnosticExportService.GeneraZip(_state)), ZipArchiveMode.Read);
            var entry = Assert.Single(archive.Entries,
                item => item.FullName.StartsWith("ui-actions/", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            Assert.Contains("Test click Test_Export", reader.ReadToEnd());
        }
        finally { UiInteractionTraceService.DirectoryForTest = null; }
    }

    [Fact]
    public void GeneraZip_NonContieneAlcunFileTestCaseONonConformitaJson()
    {
        var bytes = DiagnosticExportService.GeneraZip(_state);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.DoesNotContain(archive.Entries, e => e.Name.StartsWith("testcase_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, e => e.Name.StartsWith("nc_", StringComparison.OrdinalIgnoreCase));
    }
}
