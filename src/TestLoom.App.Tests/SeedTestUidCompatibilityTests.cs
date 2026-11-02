using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public class SeedTestUidCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeedPublishesSchemaGateBeforeUidRecords_ForQaAndCleanDemo(bool clean)
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom_SeedUid_" + Guid.NewGuid().ToString("N"));
        try
        {
            if (clean) SeedDemoDataService.GeneraDemoPulita(root);
            else SeedDemoDataService.Genera(root);
            var paths = new DataPaths(root);
            CompatibilitaVersioneService.EnsureTestUidSchema(paths);
            Assert.Equal(TestCase.CurrentSchemaVersion, JsonIo.TryRead<VersionManifest>(paths.VersionManifestFile, out _)!.SchemaDatiMassimo);
            var nc = Directory.EnumerateFiles(paths.NonConformitaDir, "nc_*.json")
                .Select(p => JsonIo.TryRead<NonConformita>(p, out _)).OfType<NonConformita>().ToList();
            Assert.Contains(nc, n => n.TestUid.HasValue);
            var signal = Directory.EnumerateFiles(paths.SegnalazioniDir, "segnalazione_*.json")
                .Select(p => JsonIo.TryRead<SegnalazioneDaClassificare>(p, out _)).OfType<SegnalazioneDaClassificare>().ToList();
            Assert.NotEmpty(signal);
            Assert.All(signal, s => Assert.NotNull(s.TestUid));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
