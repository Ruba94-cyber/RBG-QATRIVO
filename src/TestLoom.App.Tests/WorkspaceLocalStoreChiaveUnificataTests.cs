using System.Security.Cryptography;
using System.Text;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.7.1: tutti gli store locali per-workspace (indici SQLite, materializzazione Phase D, contatore journal,
/// transazioni workflow, manutenzione SQLite, traccia pilota) usano la STESSA chiave di <see cref="LocalTestIndexStore"/>
/// (<see cref="WorkspaceLocalPaths.ComputeWorkspaceKey"/>: chiave fissata all'apertura o canonica). Prima calcolavano la chiave
/// dal percorso grezzo e, per una cartella su unità mappata, finivano in una cartella locale diversa. Per le cartelle non su
/// unità mappata la chiave resta quella storica. I dati NON ancora pubblicati sotto la chiave grezza precedente (transazioni,
/// contatore) si leggono in sola lettura dalla loro posizione: nessuno spostamento.</summary>
[Collection("CondivisioneRete")]
public class WorkspaceLocalStoreChiaveUnificataTests : IDisposable
{
    public WorkspaceLocalStoreChiaveUnificataTests() => WorkspaceLocalPaths.ReimpostaChiaviSessione();

    public void Dispose() => WorkspaceLocalPaths.ReimpostaChiaviSessione();

    /// <summary>Chiave con la formula storica (SHA-256 del percorso grezzo in maiuscolo, 32 esadecimali).</summary>
    private static string ChiaveStorica(string percorso)
    {
        var normalizzato = Path.GetFullPath(percorso).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizzato)))[..32];
    }

    private static string PercorsoUnico() => @"Z:\testloom-" + Guid.NewGuid().ToString("N");

    private static string UncPer(string percorsoConLettera) => @"\" + @"\srv\dati\" + Path.GetFileName(percorsoConLettera);

    private static string LocalBase(string sottocartella) =>
        Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", sottocartella);

    [Fact]
    public void CartellaFissataConAlias_TuttiGliStoreUsanLaStessaDirectoryDiLocalTestIndexStore()
    {
        var z = PercorsoUnico();
        var unc = UncPer(z);
        var salvate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var risolutore = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));
        WorkspaceLocalPaths.FissaChiaveAllApertura(z, salvate, out var modificataIgnorata);

        var atteso = new LocalTestIndexStore(z).WorkspaceDirectory;
        var chiave = WorkspaceLocalPaths.ComputeWorkspaceKey(z);
        Assert.Equal(chiave, Path.GetFileName(atteso));

        Assert.Equal(atteso, new LocalEntityIndexStore(z).WorkspaceDirectory);
        Assert.Equal(atteso, new LocalEntityIndexStore(unc).WorkspaceDirectory);
        Assert.Equal(atteso, new LocalMasterIndexStore(z).WorkspaceDirectory);
        Assert.Equal(atteso, new LocalMasterIndexStore(unc).WorkspaceDirectory);
        Assert.Equal(atteso, new LocalNonConformitaIndexStore(z).WorkspaceDirectory);
        Assert.Equal(atteso, new LocalNonConformitaIndexStore(unc).WorkspaceDirectory);
        Assert.Equal(atteso, Path.GetDirectoryName(new PhaseDMasterMaterializer(z).DatabasePath));
        Assert.Equal(atteso, LocalSqliteMaintenanceService.ResolveWorkspaceDirectory(unc));

        Assert.Equal(Path.Combine(LocalBase("pilot-trace"), chiave), PilotConcurrencyTraceService.WorkspaceDirectory(z, null));
        Assert.Equal(Path.Combine(LocalBase("workflow-transactions"), chiave), WorkflowTransactionService.WorkspaceDirectory(z));
    }

    [Fact]
    public void CartellaNonMappata_LaChiaveRestaQuellaStoricaPerTuttiGliStore()
    {
        var percorso = Path.Combine(Path.GetTempPath(), "testloom-chiave-" + Guid.NewGuid().ToString("N"));
        var storica = ChiaveStorica(percorso);
        using var _ = new RisolutoreUnitaFinto();

        Assert.Equal(storica, WorkspaceLocalPaths.ComputeWorkspaceKey(percorso));
        Assert.Equal(Path.Combine(LocalBase("workspaces"), storica), new LocalEntityIndexStore(percorso).WorkspaceDirectory);
        Assert.Equal(Path.Combine(LocalBase("workspaces"), storica), new LocalMasterIndexStore(percorso).WorkspaceDirectory);
        Assert.Equal(Path.Combine(LocalBase("workspaces"), storica), new LocalNonConformitaIndexStore(percorso).WorkspaceDirectory);
        Assert.Equal(Path.Combine(LocalBase("workspaces"), storica), Path.GetDirectoryName(new PhaseDMasterMaterializer(percorso).DatabasePath));
        Assert.Equal(Path.Combine(LocalBase("workflow-transactions"), storica), WorkflowTransactionService.WorkspaceDirectory(percorso));
        Assert.Equal(Path.Combine(LocalBase("pilot-trace"), storica), PilotConcurrencyTraceService.WorkspaceDirectory(percorso, null));
        Assert.Null(WorkspaceLocalPaths.CartellaLegacyDaLeggere(LocalBase("workflow-transactions"), percorso));
    }

    [Fact]
    public void TransazioneLegacyDellaChiaveGrezza_ElencataPerIlRecuperoSenzaSpostarla()
    {
        var z = PercorsoUnico();
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));   // cartella NON fissata: chiave corrente = canonica (UNC)
        var legacyTx = Path.Combine(LocalBase("workflow-transactions"), ChiaveStorica(z), "tx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacyTx);
        File.WriteAllText(Path.Combine(legacyTx, "manifest.json"), "{}");
        var nuovaTx = Path.Combine(WorkflowTransactionService.WorkspaceDirectory(z), "tx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nuovaTx);

        var elenco = WorkflowTransactionService.TransactionDirectoriesForRecovery(z);

        Assert.Contains(legacyTx, elenco);
        Assert.Contains(nuovaTx, elenco);
        Assert.True(File.Exists(Path.Combine(legacyTx, "manifest.json")));   // letta, non spostata
    }

    [Fact]
    public void ContatoreLegacyDellaChiaveGrezza_NonRiassegnaLeSequenzeGiaAllocate()
    {
        var z = PercorsoUnico();
        using var _ = new RisolutoreUnitaFinto(("Z:", @"\" + @"\srv\dati"));   // cartella NON fissata: chiave corrente != chiave grezza
        var baseDir = Path.Combine(Path.GetTempPath(), "testloom-seq-" + Guid.NewGuid().ToString("N"));
        var deviceId = DeviceIdentityService.GetOrCreate();
        var legacyDir = Path.Combine(baseDir, WorkspaceLocalPaths.ChiaveGrezzaPrecedente(z), deviceId);
        Directory.CreateDirectory(legacyDir);
        var legacyFile = Path.Combine(legacyDir, "sequence.txt");
        File.WriteAllText(legacyFile, "41");

        var next = PhaseDMasterJournalService.NextDeviceSequence(z, baseDir);

        Assert.Equal(42, next);                                  // il contatore legacy e' un limite inferiore
        Assert.Equal("41", File.ReadAllText(legacyFile).Trim()); // il file legacy non viene riscritto ne' spostato
    }
}
