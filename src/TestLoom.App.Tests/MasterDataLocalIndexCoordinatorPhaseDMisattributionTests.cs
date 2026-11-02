using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

[CollectionDefinition("Master index recovery", DisableParallelization = true)]
public sealed class MasterIndexRecoveryCollection;

/// <summary>Revisione dedicata di 698ed36 richiesta dal piano Codex (handoff 2026-09-18): "verifica che un
/// eventuale SqliteException del reader Phase D non faccia quarantinare erroneamente master-index.db".
///
/// Difetto trovato: MasterDataLocalIndexCoordinator.RefreshCatalogCore, nel ramo Phase D, chiamava
/// _phaseDReader.GetCatalogState/EnumerateCatalog SENZA avvolgerne i guasti. Quelle chiamate leggono
/// master-v2.db (PhaseDMasterMaterializer.DatabasePath) - un file SQLite DIVERSO da master-index.db
/// (LocalMasterIndexStore.DatabasePath), nella stessa cartella workspace. Il filtro
/// "catch (Exception ex) when (ex is not SqliteException && ...)" in RefreshCatalog escludeva
/// deliberatamente SqliteException dal bucket "problema della fonte" per lasciarla classificare da
/// IndexRecovery - ma IndexRecovery qui e' quello di MasterDataLocalIndexCoordinator, scoped su
/// Store.DatabasePath = master-index.db. Risultato: una corruzione di master-v2.db (letta durante
/// GetCatalogState/EnumerateCatalog) veniva classificata Corruption e innescava la ricostruzione di
/// master-index.db, un database completamente sano e non responsabile del guasto.
///
/// Correzione: LeggiStatoPhaseD/EnumeraCatalogoPhaseD (MasterDataLocalIndexCoordinator.cs) avvolgono
/// qualunque guasto delle chiamate a _phaseDReader in MasterDataReadException - lo stesso tipo gia' usato
/// per un JSON master legacy invalido. MasterDataReadException eredita da IOException, quindi
/// LocalIndexFaultClassifier.Classify la classifica Io (non Corruption) al primo livello, senza mai
/// ispezionare la SqliteException interna: il filtro di RefreshCatalog la riconosce come "fonte" e la
/// rilancia SENZA mai passare da IndexRecovery. master-index.db resta quindi Healthy.</summary>
[Collection("Master index recovery")]
public sealed class MasterDataLocalIndexCoordinatorPhaseDMisattributionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseDMisattribution_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (MasterDataLocalIndexCoordinator Coordinator, string MasterV2Path) CreaCoordinatorePhaseD()
    {
        var shared = Path.Combine(_root, "shared");
        var local = Path.Combine(_root, "local");
        Directory.CreateDirectory(shared);
        var paths = new DataPaths(shared);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.EcuFile, new List<Componente> { new() { Nome = "BCM", ModificatoDa = "Seed" } });

        PhaseDMasterBootstrapService.EnsureReady(paths);
        Assert.True(PhaseDMasterJournalService.IsInitialized(shared));

        var coordinator = new MasterDataLocalIndexCoordinator(paths, local);
        var primo = coordinator.RefreshEcu();
        Assert.Equal(1, primo.AuthoritativeCount);
        Assert.Equal(LocalIndexHealth.Healthy, coordinator.IndexRecovery.Salute);

        var masterV2Path = Path.Combine(Path.GetDirectoryName(coordinator.Store.DatabasePath)!, "master-v2.db");
        Assert.True(File.Exists(masterV2Path));
        return (coordinator, masterV2Path);
    }

    [Fact]
    public void MasterV2Corrotto_NonMetteInQuarantenaMasterIndexDb()
    {
        var (coordinator, masterV2Path) = CreaCoordinatorePhaseD();

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(masterV2Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            file.Write(new byte[200]); // SQLITE_NOTADB: non e' piu' un file SQLite valido.

        var errore = Assert.Throws<MasterDataReadException>(() => coordinator.RefreshEcu());
        Assert.Contains("master-v2.db", errore.Message);

        // L'assert che dimostra la correzione: master-index.db (un DB diverso, sano) non deve MAI passare da
        // IndexRecovery per un guasto che appartiene a master-v2.db.
        Assert.Equal(LocalIndexHealth.Healthy, coordinator.IndexRecovery.Salute);
        Assert.Equal(1, coordinator.Store.Count(MasterDataLocalIndexCoordinator.EcuCatalog, true));
    }

    [Fact]
    public void MasterV2Corrotto_LaFotografiaLocalePrecedenteRestaConsultabile()
    {
        var (coordinator, masterV2Path) = CreaCoordinatorePhaseD();

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(masterV2Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            file.Write(new byte[200]);

        Assert.Throws<MasterDataReadException>(() => coordinator.RefreshEcu());

        // Anche dopo il guasto sulla fonte Phase D, la query sull'indice locale (mai toccato) funziona ancora.
        Assert.Equal("BCM", Assert.Single(coordinator.Store.Query<Componente>(
            MasterDataLocalIndexCoordinator.EcuCatalog, "BCM", true, 0, 20)).Key);
    }
}
