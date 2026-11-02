using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PhaseDVersionCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Version_" + Guid.NewGuid().ToString("N"));

    public PhaseDVersionCompatibilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Client60_DopoManifest61_RestaSolaLetturaENonToccaPhaseD()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.VersionManifestFile, new VersionManifest
        {
            VersioneCorrente = "6.1.0",
            VersioneMinima = "6.1.0",
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion
        });
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>
        {
            new() { Id = "REQ-OLD-CLIENT", Testo = "non modificare" }
        });
        var before = File.ReadAllBytes(paths.RequisitiFile);
        var input = new EsitoStatoCondivisione
        {
            Stato = StatoCondivisione.Operativa,
            Messaggio = "ok",
            VerificatoIl = DateTime.Now
        };

        var result = CompatibilitaVersioneService.Applica(paths, input, new Version(6, 0, 0));

        Assert.Equal(StatoCondivisione.SolaLettura, result.Stato);
        Assert.Contains("6.1.0", result.Messaggio, StringComparison.Ordinal);
        Assert.False(File.Exists(PhaseDMasterJournalService.ManifestPath(_root)));
        Assert.False(PhaseDMasterBootstrapService.IsReady(_root));
        Assert.Equal(before, File.ReadAllBytes(paths.RequisitiFile));
    }

    [Fact]
    public void Client61_AlzaManifest60PrimaDelCutoverECompletaPhaseD()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.VersionManifestFile, new VersionManifest
        {
            VersioneCorrente = "6.0.0",
            VersioneMinima = "6.0.0",
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion
        });
        JsonIo.WriteAtomic(paths.RequisitiFile, new List<Requisito>
        {
            new() { Id = "REQ-UPGRADE", Testo = "migra" }
        });
        var input = new EsitoStatoCondivisione
        {
            Stato = StatoCondivisione.Operativa,
            Messaggio = "ok",
            VerificatoIl = DateTime.Now
        };

        var result = CompatibilitaVersioneService.Applica(paths, input, new Version(6, 1, 0));
        var manifest = JsonIo.TryRead<VersionManifest>(paths.VersionManifestFile, out var error);

        Assert.Equal(StatoCondivisione.Operativa, result.Stato);
        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal("6.1.0", manifest!.VersioneMinima);
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
        Assert.Single(new MasterDataRepository<Requisito>(paths.RequisitiFile).Load());
    }
}
