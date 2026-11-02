using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Annullamento cooperativo della manutenzione alla chiusura dell'app: con il token già annullato nessuna fase tocca i
/// file (il file temporaneo vecchio resta), senza annullamento la stessa manutenzione lo elimina (controllo).
/// </summary>
public sealed class StorageMaintenanceAnnullamentoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_StorageAnnullamento_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Run_ConTokenGiaAnnullato_NonEseguePulizieENonLanciaEccezioni()
    {
        var (paths, localDir, staleTemp, now) = PreparaWorkspaceConTemporaneoVecchio();
        using var annullamento = new CancellationTokenSource();
        annullamento.Cancel();

        var result = StorageMaintenanceService.Run(
            paths, new LockService(), new NotificaRepository(paths), now,
            localWorkspaceDirectory: localDir, vacuumLocalSqlite: false, ottimizzaStatisticheSqlite: false,
            annullamento: annullamento.Token);

        Assert.True(File.Exists(staleTemp));
        Assert.Equal(0, result.TotaleArtefattiEliminati);
        Assert.Equal(0, result.Errori);
    }

    [Fact]
    public void Run_SenzaAnnullamento_EliminaIlTemporaneoVecchio_Controllo()
    {
        var (paths, localDir, staleTemp, now) = PreparaWorkspaceConTemporaneoVecchio();

        var result = StorageMaintenanceService.Run(
            paths, new LockService(), new NotificaRepository(paths), now,
            localWorkspaceDirectory: localDir, vacuumLocalSqlite: false, ottimizzaStatisticheSqlite: false);

        Assert.False(File.Exists(staleTemp));
        Assert.Equal(1, result.BackupTemporaneiEliminati);
    }

    private (DataPaths Paths, string LocalDir, string StaleTemp, DateTime Now) PreparaWorkspaceConTemporaneoVecchio()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        var localDir = Path.Combine(_root, "local-workspace");
        var backupDirLocale = Path.Combine(localDir, "backup");
        Directory.CreateDirectory(backupDirLocale);
        var now = DateTime.Now;
        var staleTemp = Path.Combine(backupDirLocale, "TestLoom_snapshot_20260101.tmp");
        File.WriteAllText(staleTemp, "partial-backup");
        File.SetLastWriteTime(staleTemp, now.AddDays(-2));
        return (paths, localDir, staleTemp, now);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
