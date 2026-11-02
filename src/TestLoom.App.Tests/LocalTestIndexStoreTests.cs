using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class LocalTestIndexStoreTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_LocalIndex_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_LocalIndex_Local_" + Guid.NewGuid().ToString("N"));

    public LocalTestIndexStoreTests()
    {
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(_shared);
        TryDelete(_local);
    }

    [Fact]
    public void DatabaseRestaFuoriDallaCartellaCondivisa()
    {
        var store = new LocalTestIndexStore(_shared, _local);

        Assert.StartsWith(Path.GetFullPath(_local), Path.GetFullPath(store.DatabasePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(_shared), Path.GetFullPath(store.DatabasePath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store.DatabasePath));
    }

    /// <summary>Regressione 6.4.2: l'estrazione dell'hashing in WorkspaceLocalPaths (condiviso con BackupService
    /// per la cartella backup locale) non deve cambiare la chiave di un workspace già esistente. Ricalcola qui
    /// l'algoritmo storico (SHA-256 del percorso assoluto normalizzato, upper-case su Windows) in modo
    /// indipendente da WorkspaceLocalPaths: un refactor futuro che ne cambiasse il risultato verrebbe rilevato,
    /// invalidando gli indici locali già presenti sui PC degli utenti.</summary>
    [Fact]
    public void WorkspaceKey_InvariataDopoEstrazioneHelperCondiviso()
    {
        var full = Path.GetFullPath(_shared).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalized = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var atteso = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..32];

        var store = new LocalTestIndexStore(_shared, _local);

        Assert.Equal(atteso, store.WorkspaceKey);
        Assert.Equal(atteso, WorkspaceLocalPaths.ComputeWorkspaceKey(_shared));
        Assert.Equal(Path.Combine(_local, atteso), store.WorkspaceDirectory);
        Assert.Equal(store.WorkspaceDirectory, WorkspaceLocalPaths.GetWorkspaceDirectory(_shared, _local));
        // La cartella backup locale (BackupService, decisione utente 6.4.2) vive SOTTO la stessa cartella
        // per-workspace dell'indice SQLite: stesso hash, stessa radice locale di test.
        Assert.Equal(Path.Combine(store.WorkspaceDirectory, "backup"), WorkspaceLocalPaths.GetBackupDirectory(_shared, _local));
    }

    [Fact]
    public void FullReconcileVuoto_EUnWarmSnapshotValido()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        Assert.False(store.HasCompletedFullScan);

        store.ReconcileFull(Array.Empty<LocalIndexWriteEntry>(), DateTime.UtcNow);

        var reopened = new LocalTestIndexStore(_shared, _local);
        Assert.True(reopened.HasCompletedFullScan);
        Assert.Empty(reopened.LoadAll());
    }

    [Fact]
    public void MigrazioneLettura_OperazionePianificataConformeSenzaEvidenza_NonConta()
    {
        // Decisione utente 6.4.0, punto 1 (TestCase.MigraOperazioniPianificateNonEseguite): su un Test ancora
        // Pianificato, un'esito Conforme senza nota/motivazione/descrizione è il default silenzioso
        // pre-6.4.0, non una scelta reale dell'utente — la proiezione locale lo tratta come "Da eseguire".
        // SchemaVersion=5 (< CurrentSchemaVersion) simula un file scritto da un client pre-6.4.0: è la
        // guardia che impedisce alla migrazione di scambiare un Conforme genuino appena inserito da un
        // client 6.4.0+ per l'artefatto legacy (vedi il commento sul metodo).
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-MIG-1");
        tc.SchemaVersion = 5;
        tc.StatoLavoro = StatoLavoroTest.Pianificato;
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "Paolo",
            Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.Conforme } }
        });
        var path = Path.Combine(_shared, "testcase_TC-MIG-1.json");

        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 1, tc)], DateTime.UtcNow);

        var stats = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Equal(0, stats.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void MigrazioneLettura_NonToccaTestConcluso()
    {
        // Simmetrico al test sopra: la stessa combinazione (Esito=Conforme, nessuna nota) su un Test già
        // Concluso NON è un artefatto — un test concluso è, per definizione, davvero stato eseguito — quindi
        // l'operazione deve continuare a contare normalmente.
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-MIG-2");
        tc.SchemaVersion = 5;
        tc.StatoLavoro = StatoLavoroTest.Concluso;
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "Paolo",
            Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.Conforme } }
        });
        var path = Path.Combine(_shared, "testcase_TC-MIG-2.json");

        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 1, tc)], DateTime.UtcNow);

        var stats = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Equal(1, stats.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void MigrazioneLettura_NonToccaOperazioneConNotaReale()
    {
        // Una nota già scritta dall'utente su un test Pianificato non è più un'operazione vuota: la
        // migrazione deve lasciarla come Conforme, non riscriverla a "Da eseguire".
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-MIG-3");
        tc.SchemaVersion = 5;
        tc.StatoLavoro = StatoLavoroTest.Pianificato;
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "Paolo",
            Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.Conforme, Nota = "Verificato a banco prima della ripianificazione." } }
        });
        var path = Path.Combine(_shared, "testcase_TC-MIG-3.json");

        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 1, tc)], DateTime.UtcNow);

        var stats = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Equal(1, stats.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void MigrazioneLettura_NonToccaSchemaVersionCorrente()
    {
        // Guardia critica: un TestCase con SchemaVersion == CurrentSchemaVersion (il default per qualunque
        // oggetto costruito/risalvato da un client 6.4.0+) NON va mai migrato, anche se ancora Pianificato con
        // Esito=Conforme e nessuna nota — in un client 6.4.0+ questo può accadere SOLO per una scelta reale
        // dell'operatore (il selettore parte sempre da "Da eseguire"). Senza questa guardia un Conforme
        // genuino verrebbe silenziosamente riportato a "Da eseguire".
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-MIG-4"); // SchemaVersion di default = TestCase.CurrentSchemaVersion
        tc.StatoLavoro = StatoLavoroTest.Pianificato;
        tc.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "Paolo",
            Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.Conforme } }
        });
        var path = Path.Combine(_shared, "testcase_TC-MIG-4.json");

        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 1, tc)], DateTime.UtcNow);

        var stats = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Equal(1, stats.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void Reopen_RipristinaTestCaseSenzaLeggereIlJsonCondiviso()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var tc = NuovoTest("TC-0100");
        var path = Path.Combine(_shared, "testcase_TC-0100.json");
        var when = DateTime.UtcNow.AddMinutes(-1);

        store.ReconcileFull([new LocalIndexWriteEntry(path, when, 123, tc)], DateTime.UtcNow);

        var reopened = new LocalTestIndexStore(_shared, _local);
        var rows = reopened.LoadAll();
        var loaded = Assert.Single(rows);
        Assert.Equal(path, loaded.Path);
        Assert.Equal(tc.Uid, loaded.TestCase.Uid);
        Assert.Equal("TC-0100", loaded.TestCase.Id);
        Assert.Equal(123, loaded.FileLength);
        Assert.Equal(when.Ticks, loaded.MtimeUtc.Ticks);
    }

    [Fact]
    public void ReconcileSuccessivo_RimuoveRecordNonPiuPresenti()
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var a = NuovoTest("TC-0001");
        var b = NuovoTest("TC-0002");
        var pathA = Path.Combine(_shared, "testcase_TC-0001.json");
        var pathB = Path.Combine(_shared, "testcase_TC-0002.json");
        var now = DateTime.UtcNow;

        store.ReconcileFull(
        [
            new LocalIndexWriteEntry(pathA, now, 10, a),
            new LocalIndexWriteEntry(pathB, now, 20, b)
        ], now);

        store.ReconcileFull([new LocalIndexWriteEntry(pathB, now.AddSeconds(1), 21, b)], now.AddSeconds(1));

        var rows = store.LoadAll();
        var only = Assert.Single(rows);
        Assert.Equal("TC-0002", only.TestCase.Id);
        Assert.Equal(21, only.FileLength);
    }

    [Fact]
    public void Repository_WarmStartERiconciliazioneRestanoAutoritativiSuiJson()
    {
        var paths = new DataPaths(_shared);
        paths.EnsureStructure();
        var localStore = new LocalTestIndexStore(_shared, _local);
        var repo = new TestCaseRepository(paths, localStore);
        var tc = NuovoTest("TC-0200");

        repo.Save(tc, isNew: true);
        repo.RefreshIndex();
        Assert.True(repo.HasPersistentSnapshot);

        var reopened = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        Assert.True(reopened.HasPersistentSnapshot);
        Assert.Equal("TC-0200", Assert.Single(reopened.GetAll()).Id);

        File.Delete(reopened.PercorsoPer(reopened.GetById("TC-0200")!));
        reopened.RefreshIndex();
        Assert.Empty(reopened.GetAll(includeEliminati: true));

        var afterReconcile = new TestCaseRepository(paths, new LocalTestIndexStore(_shared, _local));
        Assert.Empty(afterReconcile.GetAll(includeEliminati: true));
    }

    /// <summary>Riuso del fingerprint al warm start (TestCaseRepository, costruttore): l'hash memorizzato nella riga viene
    /// riusato solo con il marcatore dell'algoritmo attuale e con forma SHA-256 valida; in ogni altro caso viene
    /// ricalcolato. L'hash sentinella (tutti zero) rende osservabile il riuso senza contatori globali, che sarebbero
    /// condivisi con gli altri test in esecuzione parallela.</summary>
    [Fact]
    public void WarmStart_RiusaHashMemorizzatoSoloConMarcatoreEForma()
    {
        const string sentinella = "0000000000000000000000000000000000000000000000000000000000000000";
        var store = new LocalTestIndexStore(_shared, _local);
        var path = Path.Combine(_shared, "testcase_TC-FP-1.json");
        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 1, NuovoTest("TC-FP-1"))], DateTime.UtcNow);

        // Il marcatore lo scrive la riconciliazione completa; l'hash memorizzato coincide con il ricalcolo sul Test caricato.
        Assert.True(store.HashIndicizzatiRiusabili);
        var riga = Assert.Single(store.LoadAll());
        Assert.Equal(TestCaseFingerprint.Compute(riga.TestCase), riga.ContentHash);

        // 1) Marcatore presente e hash valido: il warm start riusa l'hash memorizzato.
        ScriviHashIndice(store, sentinella);
        Assert.Equal(sentinella, StampAlWarmStart(store));

        // 2) Hash non in forma SHA-256: ricalcolo.
        ScriviHashIndice(store, "non-un-hash");
        var ricalcolato = StampAlWarmStart(store);
        Assert.True(TestCaseFingerprint.IsWellFormed(ricalcolato));
        Assert.Equal(TestCaseFingerprint.Compute(riga.TestCase), ricalcolato);

        // 3) Marcatore assente (indice scritto da un altro algoritmo): nessun riuso, anche con hash valido.
        ScriviHashIndice(store, sentinella);
        RimuoviMarcatoreIndice(store);
        Assert.False(store.HashIndicizzatiRiusabili);
        Assert.Equal(TestCaseFingerprint.Compute(riga.TestCase), StampAlWarmStart(store));
    }

    private string? StampAlWarmStart(LocalTestIndexStore store)
    {
        var repository = new TestCaseRepository(new DataPaths(_shared), store);
        return TestCaseFingerprint.GetStored(repository.GetById("TC-FP-1")!);
    }

    private static void ScriviHashIndice(LocalTestIndexStore store, string valore)
    {
        using var connessione = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        connessione.Open();
        using var comando = connessione.CreateCommand();
        comando.CommandText = "UPDATE test_case_cache SET content_hash = $hash;";
        comando.Parameters.AddWithValue("$hash", valore);
        comando.ExecuteNonQuery();
    }

    private static void RimuoviMarcatoreIndice(LocalTestIndexStore store)
    {
        using var connessione = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath}");
        connessione.Open();
        using var comando = connessione.CreateCommand();
        comando.CommandText = "DELETE FROM meta WHERE key = 'content_hash_algo';";
        comando.ExecuteNonQuery();
    }

    private static TestCase NuovoTest(string id) => new()
    {
        Id = id,
        Componente = new EcuSelection { "Componente-A" },
        Veicolo = "Vehicle-A",
        Veicoli = ["Vehicle-A"],
        DescrizioneBreve = "sqlite index test",
        StatoLavoro = StatoLavoroTest.Pianificato,
        RevisioneDati = 1
    };

    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
