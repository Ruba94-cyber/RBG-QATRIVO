using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class IndexedStorageFinalHardeningTests
{
    [Fact]
    public void VersionManifest_PreJournal_Minimum_Is_Raised_To_Safe_Baseline()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.VersionManifestFile, new VersionManifest
        {
            VersioneCorrente = "3.3.4",
            VersioneMinima = "3.3.0",
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion
        });

        var input = new EsitoStatoCondivisione
        {
            Stato = StatoCondivisione.Operativa,
            Messaggio = "ok",
            VerificatoIl = DateTime.Now
        };
        var result = CompatibilitaVersioneService.Applica(paths, input);
        var manifest = JsonIo.TryRead<VersionManifest>(paths.VersionManifestFile, out var error);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal(CompatibilitaVersioneService.JournalSafetyMinimumVersion, manifest!.VersioneMinima);
        Assert.Equal(StatoCondivisione.Operativa, result.Stato);
    }

    [Fact]
    public void VersionManifest_Client_Precedente_A_6_0_Resta_In_Sola_Lettura()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        JsonIo.WriteAtomic(paths.VersionManifestFile, new VersionManifest
        {
            VersioneCorrente = "6.0.0",
            VersioneMinima = "6.0.0",
            SchemaDatiMassimo = TestCase.CurrentSchemaVersion
        });

        var input = new EsitoStatoCondivisione
        {
            Stato = StatoCondivisione.Operativa,
            Messaggio = "ok",
            VerificatoIl = DateTime.Now
        };

        var result = CompatibilitaVersioneService.Applica(paths, input, new Version(5, 5, 0));

        Assert.Equal(StatoCondivisione.SolaLettura, result.Stato);
        Assert.Contains("6.0.0", result.Messaggio, StringComparison.Ordinal);
    }

    [Fact]
    public void AppState_Dispose_AttendeManutenzioneERilasciaIlWorkspace()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TestLoom_Dispose_" + Guid.NewGuid().ToString("N"));
        var configRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TestLoom_DisposeConfig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var state = new AppState(
                       new AppConfigService(System.IO.Path.Combine(configRoot, "config.json")),
                       runBackgroundMaintenance: true))
                state.InizializzaCartella(root);

            Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(configRoot)) Directory.Delete(configRoot, recursive: true);
        }
    }

    [Fact]
    public void Deterministic_Notification_Deleted_Does_Not_Append_Create_Again_After_Reopen()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        paths.EnsureStructure();
        var uid = Guid.NewGuid();
        var item = new NotificaApp
        {
            Uid = uid,
            Data = DateTime.Now,
            Destinatario = "Master",
            Tipo = "ConflittoSincronizzazione",
            Titolo = "Fork",
            Messaggio = "test"
        };

        var first = new NotificaRepository(paths);
        first.AddDeterministic(item);
        first.Delete(item);
        // Stabilizza il formato fisico prima del confronto: current può diventare checkpoint senza che lo
        // stato logico cambi. Dopo questa compattazione, un nuovo Create sarebbe invece una nuova riga reale.
        first.RunMaintenance(force: true);
        var before = CountNotificationLedgerLines(temp.Path);

        // Simula una nuova istanza/app restart: il tombstone condiviso deve rendere Add idempotente.
        var reopened = new NotificaRepository(paths);
        reopened.AddDeterministic(new NotificaApp
        {
            Uid = uid,
            Data = DateTime.Now,
            Destinatario = "Master",
            Tipo = "ConflittoSincronizzazione",
            Titolo = "Fork",
            Messaggio = "test"
        });
        var after = CountNotificationLedgerLines(temp.Path);

        Assert.Equal(before, after);
        Assert.DoesNotContain(reopened.GetAll("Master"), n => n.Uid == uid);
    }

    [Fact]
    public void Old_Lock_Session_Cannot_Heartbeat_Or_Release_A_New_Session()
    {
        using var temp = new TempDirectory();
        var target = System.IO.Path.Combine(temp.Path, "testcase_X.json");
        File.WriteAllText(target, "{}");
        var oldSession = new LockService("PC-A"); // device diversi: altro PC con lo stesso operatore
        var newSession = new LockService("PC-B");

        var first = oldSession.TryAcquire(target, "Paolo");
        Assert.True(first.Acquisito);
        Assert.False(string.IsNullOrWhiteSpace(first.DetenutoDa?.SessionId));

        // Stesso nome operatore, altra istanza: non può assumere ownership.
        var denied = newSession.TryAcquire(target, "Paolo");
        Assert.False(denied.Acquisito);

        newSession.ForceUnlock(target);
        var second = newSession.TryAcquire(target, "Paolo");
        Assert.True(second.Acquisito);
        var secondSessionId = second.DetenutoDa!.SessionId;

        // La vecchia finestra rimasta viva non deve toccare la sessione nuova.
        oldSession.Heartbeat(target, "Paolo");
        oldSession.Release(target);
        var active = newSession.GetActiveLock(target);

        Assert.NotNull(active);
        Assert.Equal(secondSessionId, active!.SessionId);
        newSession.Release(target);
        Assert.False(File.Exists(LockService.LockPathFor(target)));
    }

    [Fact]
    public void Fts_Search_Uses_Local_Index_Without_Shared_Scan_When_Fts5_Is_Available()
    {
        using var shared = new TempDirectory();
        using var local = new TempDirectory();
        var store = new LocalTestIndexStore(shared.Path, local.Path);
        // FTS è accelerazione opzionale per design: su una build SQLite priva di FTS5 il fallback in memoria
        // è il comportamento corretto e questo test specifico non ha nulla da verificare.
        if (!store.FtsEnabled) return;

        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "T-FTS-001",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
            DescrizioneBreve = "Diagnosi freno motore",
            Descrizione = "Verifica funzione diagnostica freno motore",
            Componente = new EcuSelection { "ECM" },
            Veicoli = new List<string> { "Daily MY26" }
        };
        tc.NormalizzaVeicoli();
        var path = System.IO.Path.Combine(shared.Path, "testcase_T-FTS-001.json");
        JsonIo.WriteAtomic(path, tc);
        store.Upsert(path, File.GetLastWriteTimeUtc(path), new FileInfo(path).Length, tc);

        var hits = LocalTestSearchService.Search(store.DatabasePath, "freno", 10);

        Assert.NotNull(hits);
        Assert.Contains(hits!, h => h.Uid == tc.Uid && h.Id == tc.Id);
    }

    [Fact]
    public void Local_Sqlite_Maintenance_Optimizes_Existing_Workspace_Only()
    {
        using var shared = new TempDirectory();
        using var local = new TempDirectory();
        var store = new LocalTestIndexStore(shared.Path, local.Path);

        var result = LocalSqliteMaintenanceService.OptimizeWorkspace(store.WorkspaceDirectory, vacuum: true);

        Assert.True(result.DatabaseOttimizzati >= 1);
        Assert.Equal(0, result.Errori);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public void Performance_Diagnostics_Remain_Bounded_To_Current_Plus_Two_Rotations()
    {
        using var temp = new TempDirectory();
        var detail = new string('x', 12_000);
        for (var i = 0; i < 260; i++)
            PerformanceDiagnosticsService.Record(temp.Path, "benchmark", TimeSpan.FromMilliseconds(i), i, detail);

        var files = Directory.EnumerateFiles(temp.Path, "performance*.ndjson", SearchOption.TopDirectoryOnly).ToList();
        var recent = PerformanceDiagnosticsService.ReadRecent(temp.Path, 25);

        Assert.InRange(files.Count, 1, 3);
        Assert.InRange(recent.Count, 1, 25);
        Assert.All(recent, e => Assert.Equal("benchmark", e.Operation));
    }

    private static int CountNotificationLedgerLines(string root)
    {
        var ledgerRoot = System.IO.Path.Combine(root, "_ditm", "notifications");
        if (!Directory.Exists(ledgerRoot)) return 0;
        return Directory.EnumerateFiles(ledgerRoot, "*.ndjson", SearchOption.AllDirectories)
            .Sum(file => File.ReadLines(file).Count(line => !string.IsNullOrWhiteSpace(line)));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "testloom-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
