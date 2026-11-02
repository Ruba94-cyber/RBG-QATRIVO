using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Recupero SQLite a RUNTIME e invalidazione dell'indice obsoleto.
///
/// Tutta la fault injection avviene su workspace e database temporanei dedicati a ciascun test: nessuna di
/// queste prove tocca mai in scrittura una cartella condivisa reale ne' il config utente. Gli unici dati
/// autorevoli restano i JSON, che i test verificano esplicitamente essere intatti dopo ogni guasto.
/// </summary>
public sealed class LocalIndexRecoveryTests : IDisposable
{
    private readonly string _shared = Path.Combine(Path.GetTempPath(), "TestLoom_IdxRec_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_IdxRec_Local_" + Guid.NewGuid().ToString("N"));

    public LocalIndexRecoveryTests()
    {
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(_local);
        new DataPaths(_shared).EnsureStructure();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        RimuoviSolaLettura(_local);
        TryDelete(_shared);
        TryDelete(_local);
    }

    [Fact]
    public void ChangeRequest_CorruzioneDuranteQuery_RipristinaIndiceDaJson()
    {
        var paths = new DataPaths(_shared);
        var repo = new ChangeRequestRepository(paths, TimeSpan.Zero, _local);
        var cr = new ChangeRequest { Id = "CR-2026-0001", Titolo = "CR di prova" };
        repo.Save(cr);
        repo.RefreshIndex();
        var jsonPath = paths.ChangeRequestFile(cr.Id);
        var json = File.ReadAllBytes(jsonPath);

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(repo.LocalIndexDatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            file.Write(new byte[100]);

        Assert.Equal(1, repo.Count());
        Assert.True(repo.IndexRecovery.AttendiRecupero(TimeSpan.FromSeconds(15)));
        Assert.Equal(LocalIndexHealth.Healthy, repo.IndexRecovery.Salute);
        Assert.Equal(cr.Id, Assert.Single(repo.QueryActiveSlice(0, 20).Items).Id);
        Assert.Equal(json, File.ReadAllBytes(jsonPath));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(repo.LocalIndexDatabasePath)!, "entity-index.db.corrupt-*"));
    }

    // ------------------------------------------------------------- classificazione

    [Theory]
    [InlineData(LocalIndexFaultClassifier.SqliteCorrupt, LocalIndexFaultKind.Corruption)]
    [InlineData(LocalIndexFaultClassifier.SqliteNotADb, LocalIndexFaultKind.Corruption)]
    [InlineData(LocalIndexFaultClassifier.SqliteBusy, LocalIndexFaultKind.Busy)]
    [InlineData(LocalIndexFaultClassifier.SqliteLocked, LocalIndexFaultKind.Busy)]
    [InlineData(LocalIndexFaultClassifier.SqliteFull, LocalIndexFaultKind.DiskFull)]
    [InlineData(LocalIndexFaultClassifier.SqliteIoErr, LocalIndexFaultKind.Io)]
    [InlineData(LocalIndexFaultClassifier.SqlitePerm, LocalIndexFaultKind.AccessDenied)]
    [InlineData(LocalIndexFaultClassifier.SqliteReadOnly, LocalIndexFaultKind.AccessDenied)]
    [InlineData(LocalIndexFaultClassifier.SqliteCantOpen, LocalIndexFaultKind.AccessDenied)]
    [InlineData(LocalIndexFaultClassifier.SqliteAuth, LocalIndexFaultKind.AccessDenied)]
    [InlineData(LocalIndexFaultClassifier.SqliteError, LocalIndexFaultKind.Other)]
    public void Classificazione_UsaIlCodicePrimarioNonIlMessaggio(int codice, LocalIndexFaultKind atteso)
    {
        // Il messaggio mente di proposito: se la classificazione guardasse il testo, questi test fallirebbero.
        var ex = new SqliteException("database disk image is malformed / corrupt / not a database", codice);
        Assert.Equal(atteso, LocalIndexFaultClassifier.Classify(ex));
    }

    [Fact]
    public void Classificazione_CodiciEstesiRicadonoNellaFamigliaPrimaria()
    {
        // SQLITE_CORRUPT_VTAB = 267 -> primario 11; SQLITE_IOERR_SHORT_READ = 522 -> primario 10;
        // SQLITE_READONLY_DBMOVED = 1032 -> primario 8; SQLITE_BUSY_SNAPSHOT = 517 -> primario 5.
        Assert.Equal(LocalIndexFaultKind.Corruption, LocalIndexFaultClassifier.Classify(new SqliteException("x", 11, 267)));
        Assert.Equal(LocalIndexFaultKind.Io, LocalIndexFaultClassifier.Classify(new SqliteException("x", 10, 522)));
        Assert.Equal(LocalIndexFaultKind.AccessDenied, LocalIndexFaultClassifier.Classify(new SqliteException("x", 8, 1032)));
        Assert.Equal(LocalIndexFaultKind.Busy, LocalIndexFaultClassifier.Classify(new SqliteException("x", 5, 517)));
    }

    [Fact]
    public void Classificazione_SchemaIncompatibileNonEMaiCorruzione()
    {
        var ex = new LocalIndexSchemaIncompatibleException("Schema indice locale non supportato: 99.");
        Assert.Equal(LocalIndexFaultKind.SchemaIncompatible, LocalIndexFaultClassifier.Classify(ex));
        Assert.False(LocalIndexFaultClassifier.IsCorruption(ex));
    }

    [Fact]
    public void Classificazione_AmbienteFilesystem()
    {
        Assert.Equal(LocalIndexFaultKind.AccessDenied, LocalIndexFaultClassifier.Classify(new UnauthorizedAccessException()));
        Assert.Equal(LocalIndexFaultKind.DiskFull,
            LocalIndexFaultClassifier.Classify(new IOException("disco pieno") { HResult = unchecked((int)0x80070070) }));
        Assert.Equal(LocalIndexFaultKind.Corruption,
            LocalIndexFaultClassifier.Classify(new InvalidOperationException("wrap", new SqliteException("x", 11))));
    }

    // ----------------------------------------- guasti che NON sono corruzione

    [Theory]
    [InlineData(LocalIndexFaultClassifier.SqliteBusy)]
    [InlineData(LocalIndexFaultClassifier.SqliteFull)]
    [InlineData(LocalIndexFaultClassifier.SqlitePerm)]
    [InlineData(LocalIndexFaultClassifier.SqliteIoErr)]
    public void GuastoNonCorruzione_NonMetteInQuarantenaUnDatabaseSano(int codice)
    {
        var store = PreparaIndiceConUnTest("TC-0001");
        var ricostruzioni = 0;
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () =>
        {
            ricostruzioni++;
            return LocalIndexRebuildOutcome.Riuscita(0);
        });

        var ok = coordinatore.TryRead("lettura di prova", () => throw new SqliteException("guasto iniettato", codice));

        Assert.False(ok);
        Assert.Equal(0, ricostruzioni);
        Assert.Equal(0, coordinatore.RicostruzioniEseguite);
        Assert.True(File.Exists(store.DatabasePath));
        Assert.Empty(Directory.GetFiles(store.WorkspaceDirectory, "*.corrupt-*"));
        // Il database sano resta leggibile: la quarantena avrebbe buttato via la fotografia calda per nulla.
        Assert.Single(new LocalTestIndexStore(_shared, _local).LoadAll());
    }

    [Fact]
    public void Busy_NonDegradaNemmenoLoStato()
    {
        var store = PreparaIndiceConUnTest("TC-0002");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () => LocalIndexRebuildOutcome.Riuscita(0));

        coordinatore.TryRead("lettura occupata", () => throw new SqliteException("busy", LocalIndexFaultClassifier.SqliteBusy));

        // Contesa transitoria: il giro dopo si riprova sul percorso indicizzato, senza avvisi ne' ricostruzioni.
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.Salute);
        Assert.True(coordinatore.IsUsable);
    }

    [Fact]
    public void AccessoNegato_DegradaSenzaRicostruireNeQuarantenare()
    {
        var store = PreparaIndiceConUnTest("TC-0003");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () => LocalIndexRebuildOutcome.Riuscita(0));

        coordinatore.TryRead("lettura negata", () => throw new SqliteException("perm", LocalIndexFaultClassifier.SqlitePerm));

        Assert.Equal(LocalIndexHealth.Unavailable, coordinatore.Salute);
        Assert.Equal(0, coordinatore.RicostruzioniEseguite);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public void ErroreSqlNellaLettura_RipiegaSenzaDegradareLIndice()
    {
        // Bug del programma (SQL non valido), non un guasto dell'ambiente: il database e' sano, niente degrado ne' ricostruzione.
        var store = PreparaIndiceConUnTest("TC-0004");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () => LocalIndexRebuildOutcome.Riuscita(0));

        var ok = coordinatore.TryRead("lettura con SQL errato", () => throw new SqliteException("ESCAPE expression must be a single character", LocalIndexFaultClassifier.SqliteError));

        Assert.False(ok);
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.Salute);
        Assert.True(coordinatore.IsUsable);
        Assert.Equal(0, coordinatore.RicostruzioniEseguite);
    }

    [Fact]
    public void ErroreSqlNellaScrittura_DegradaPerNonServireUnReadModelObsoleto()
    {
        // Una scrittura non applicata lascia il read model indietro: va degradato, altrimenti sarebbe servito come fresco.
        var store = PreparaIndiceConUnTest("TC-0005");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () => LocalIndexRebuildOutcome.Riuscita(0));

        var ok = coordinatore.TryWrite("scrittura con SQL errato", () => throw new SqliteException("near \"x\": syntax error", LocalIndexFaultClassifier.SqliteError));

        Assert.False(ok);
        Assert.Equal(LocalIndexHealth.Unavailable, coordinatore.Salute);
        Assert.Equal(0, coordinatore.RicostruzioniEseguite);
    }

    [Fact]
    public void ErroreSqlDiProgramma_EDistintoDagliAltriGuastiSqlite()
    {
        Assert.True(LocalIndexFaultClassifier.IsErroreSqlProgramma(new SqliteException("near \"x\": syntax error", LocalIndexFaultClassifier.SqliteError)));
        Assert.True(LocalIndexFaultClassifier.IsErroreSqlProgramma(new InvalidOperationException("wrap", new SqliteException("x", LocalIndexFaultClassifier.SqliteError))));
        Assert.False(LocalIndexFaultClassifier.IsErroreSqlProgramma(new SqliteException("busy", LocalIndexFaultClassifier.SqliteBusy)));
        Assert.False(LocalIndexFaultClassifier.IsErroreSqlProgramma(new SqliteException("corrotto", LocalIndexFaultClassifier.SqliteCorrupt)));
        Assert.False(LocalIndexFaultClassifier.IsErroreSqlProgramma(new IOException("io")));
    }

    // -------------------------------------------------- raggruppamento richieste

    [Fact]
    public void DieciQueryConcorrenti_ProduconoUnaSolaRicostruzione()
    {
        var store = PreparaIndiceConUnTest("TC-0010");
        var ricostruzioni = 0;
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () =>
        {
            Interlocked.Increment(ref ricostruzioni);
            Thread.Sleep(50);
            return LocalIndexRebuildOutcome.Riuscita(1);
        });

        const int concorrenti = 10;
        using var tutteDentro = new CountdownEvent(concorrenti);
        var thread = new Thread[concorrenti];
        for (var i = 0; i < concorrenti; i++)
        {
            thread[i] = new Thread(() => coordinatore.TryRead("query concorrente", () =>
            {
                // Tutte e dieci entrano mentre l'indice e' ancora sano: e' il caso peggiore, quello che
                // produceva dieci rebuild indipendenti.
                tutteDentro.Signal();
                tutteDentro.Wait(TimeSpan.FromSeconds(5));
                throw new SqliteException("corrotto", LocalIndexFaultClassifier.SqliteCorrupt);
            }));
            thread[i].Start();
        }
        foreach (var t in thread) Assert.True(t.Join(TimeSpan.FromSeconds(30)));

        // Dieci richieste, una sola ricostruzione: e' il raggruppamento che si voleva dimostrare.
        Assert.Equal(concorrenti, coordinatore.RichiesteDiRecupero);
        Assert.Equal(1, ricostruzioni);
        Assert.Equal(1, coordinatore.RicostruzioniEseguite);
    }

    [Fact]
    public void LetturaCorrotta_RientraDaSolaSullaNuovaGenerazioneConUnSoloRetry()
    {
        var store = PreparaIndiceConUnTest("TC-0011");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () => LocalIndexRebuildOutcome.Riuscita(1));
        var generazioneIniziale = coordinatore.Generazione;

        var tentativi = 0;
        var ok = coordinatore.TryRead("lettura con retry", () =>
        {
            tentativi++;
            if (tentativi == 1) throw new SqliteException("corrotto", LocalIndexFaultClassifier.SqliteCorrupt);
            return 42;
        }, out var valore);

        Assert.True(ok);
        Assert.Equal(42, valore);
        // Esattamente due esecuzioni: nessun retry ricorsivo, nessun ciclo di ricostruzioni.
        Assert.Equal(2, tentativi);
        Assert.Equal(1, coordinatore.RicostruzioniEseguite);
        Assert.True(coordinatore.Generazione > generazioneIniziale);
    }

    [Fact]
    public void SecondoGuastoSullaNuovaGenerazione_NonInnescaUnaSecondaRicostruzione()
    {
        var store = PreparaIndiceConUnTest("TC-0012");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath, () => LocalIndexRebuildOutcome.Riuscita(1));

        var tentativi = 0;
        var ok = coordinatore.TryRead("lettura sempre in errore", () =>
        {
            tentativi++;
            throw new SqliteException("corrotto", LocalIndexFaultClassifier.SqliteCorrupt);
        });

        Assert.False(ok);
        Assert.Equal(2, tentativi);
        Assert.Equal(1, coordinatore.RicostruzioniEseguite);
        Assert.Equal(1, coordinatore.RichiesteDiRecupero);
        Assert.Equal(LocalIndexHealth.Unavailable, coordinatore.Salute);
    }

    // ---------------------------------------------------------- quarantena

    [Fact]
    public void Quarantena_FallitaNonVieneDichiarataRiuscitaENonCancellaNulla()
    {
        var store = PreparaIndiceConUnTest("TC-0020");
        using var presidio = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        presidio.Open();

        var esito = LocalIndexQuarantine.TrySequester(store.DatabasePath, out var motivo);

        Assert.False(esito);
        Assert.False(string.IsNullOrWhiteSpace(motivo));
        // Il file NON e' stato cancellato di nascosto: si potra' ritentare quando l'handle sara' chiuso.
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public void VersioneNonCompatibile_EliminaDbWalShmEJournalSenzaCopieCorrotte()
    {
        var database = Path.Combine(_local, "index.db");
        File.WriteAllText(database, "database di un'altra versione");
        File.WriteAllText(database + "-wal", "wal");
        File.WriteAllText(database + "-shm", "shm");
        File.WriteAllText(database + "-journal", "journal");

        var esito = LocalIndexQuarantine.TryRecuperaAllAvvio(database, LocalIndexFaultKind.SchemaIncompatible, out var motivo);

        Assert.True(esito, motivo);
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + "-wal"));
        Assert.False(File.Exists(database + "-shm"));
        Assert.False(File.Exists(database + "-journal"));
        // Non e' corruzione: nessuna copia diagnostica.
        Assert.Empty(Directory.GetFiles(_local, "*.corrupt-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corruzione_RestaInQuarantenaConDbWalEShmInsieme()
    {
        var database = Path.Combine(_local, "index.db");
        File.WriteAllText(database, "non e' un database sqlite");
        File.WriteAllText(database + "-wal", "wal");
        File.WriteAllText(database + "-shm", "shm");

        var esito = LocalIndexQuarantine.TryRecuperaAllAvvio(database, LocalIndexFaultKind.Corruption, out var motivo);

        Assert.True(esito, motivo);
        Assert.False(File.Exists(database));
        Assert.Single(Directory.GetFiles(_local, "index.db.corrupt-*"));
        Assert.Single(Directory.GetFiles(_local, "index.db-wal.corrupt-*"));
        Assert.Single(Directory.GetFiles(_local, "index.db-shm.corrupt-*"));
    }

    [Fact]
    public void EliminazioneBloccata_NonCancellaNullaENonLanciaEccezioni()
    {
        var database = Path.Combine(_local, "index.db");
        File.WriteAllText(database, "database di un'altra versione");
        File.WriteAllText(database + "-wal", "wal");
        using var presidio = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);

        var eliminato = LocalIndexQuarantine.TryEliminaIncompatibile(database, out var motivoEliminazione);
        var recuperato = LocalIndexQuarantine.TryRecuperaAllAvvio(database, LocalIndexFaultKind.SchemaIncompatible, out var motivoRecupero);

        Assert.False(eliminato);
        Assert.False(string.IsNullOrWhiteSpace(motivoEliminazione));
        Assert.False(recuperato);
        Assert.False(string.IsNullOrWhiteSpace(motivoRecupero));
        // Controllo preliminare fallito prima di qualunque cancellazione: DB e WAL restano al loro posto.
        Assert.True(File.Exists(database));
        Assert.True(File.Exists(database + "-wal"));
        Assert.Empty(Directory.GetFiles(_local, "*.corrupt-*"));
    }

    [Fact]
    public void EliminazioneNonPossibile_RicadeSullaQuarantenaConIlSetIntero()
    {
        var database = Path.Combine(_local, "index.db");
        File.WriteAllText(database, "database di un'altra versione");
        File.WriteAllText(database + "-wal", "wal");
        // Sola lettura: la cancellazione fallisce mentre lo spostamento riesce, quindi il ripiego e' la quarantena.
        File.SetAttributes(database, FileAttributes.ReadOnly);

        var esito = LocalIndexQuarantine.TryRecuperaAllAvvio(database, LocalIndexFaultKind.SchemaIncompatible, out var motivo);

        Assert.True(esito, motivo);
        Assert.False(File.Exists(database));
        Assert.Single(Directory.GetFiles(_local, "index.db.corrupt-*"));
        Assert.Single(Directory.GetFiles(_local, "index.db-wal.corrupt-*"));
    }

    [Fact]
    public void IndiceTest_VersioneFutura_VieneEliminatoERicreatoSenzaQuarantena()
    {
        var store = PreparaIndiceConUnTest("TC-0030");
        var database = store.DatabasePath;
        ImpostaVersioneSchema(database, "99");
        SqliteConnection.ClearAllPools();

        var riaperto = new LocalTestIndexStore(_shared, _local);

        Assert.Equal(database, riaperto.DatabasePath);
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), LeggiVersioneSchema(database));
        Assert.False(riaperto.HasCompletedFullScan); // indice vuoto: la ricostruzione dai JSON deve ripartire
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(database)!, "index.db.corrupt-*"));
    }

    [Fact]
    public void IndiceTest_CorruzioneAllAvvio_RestaInQuarantena()
    {
        var database = new LocalTestIndexStore(_shared, _local).DatabasePath;
        DanneggiaDatabase(database);

        var riaperto = new LocalTestIndexStore(_shared, _local);

        Assert.Equal(database, riaperto.DatabasePath);
        Assert.True(File.Exists(database));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(database)!, "index.db.corrupt-*"));
    }

    [Fact]
    public void MasterPhaseD_VersioneNonCompatibile_VieneEliminataSenzaCopiaCorrotta()
    {
        var database = new PhaseDMasterMaterializer(_shared, _local).DatabasePath;
        ImpostaVersioneSchemaPhaseD(database, "1");

        var riaperto = new PhaseDMasterMaterializer(_shared, _local);

        Assert.Equal(database, riaperto.DatabasePath);
        Assert.Equal("2", LeggiVersioneSchemaPhaseD(database));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(database)!, "*.corrupt-*"));
    }

    [Fact]
    public void Quarantena_NonTocaJournalDbNeGliAltriDatabaseDelWorkspace()
    {
        var store = PreparaIndiceConUnTest("TC-0021");
        // journal.db contiene outbox ed eventi di recovery registrati PRIMA della scrittura JSON: estendere
        // il reset a quel file significherebbe perdere lavoro non ancora pubblicato.
        var journal = Path.Combine(store.WorkspaceDirectory, "journal.db");
        var ncIndex = Path.Combine(store.WorkspaceDirectory, "nc-index.db");
        File.WriteAllText(journal, "outbox");
        File.WriteAllText(ncIndex, "nc");

        Assert.True(LocalIndexQuarantine.TrySequester(store.DatabasePath, out var motivo), motivo);

        Assert.False(File.Exists(store.DatabasePath));
        Assert.True(File.Exists(journal));
        Assert.Equal("outbox", File.ReadAllText(journal));
        Assert.True(File.Exists(ncIndex));
    }

    // ------------------------------------------------- end-to-end sul repository

    [Fact]
    public void CorruzioneSuUnaQueryDopoLoStartup_RicostruisceERitornaAlPercorsoIndicizzato()
    {
        var paths = new DataPaths(_shared);
        ScriviTestCondiviso(paths, "TC-0100");
        ScriviTestCondiviso(paths, "TC-0101");

        var store = new LocalTestIndexStore(_shared, _local);
        var repo = new TestCaseRepository(paths, store);
        repo.RefreshIndex();
        Assert.True(repo.HasPersistentSnapshot);
        var coordinatore = Assert.IsType<LocalIndexRecoveryCoordinator>(repo.IndexRecovery);
        var generazioneIniziale = coordinatore.Generazione;
        Assert.Equal(2, new LocalTestArchiveQueryService(store.DatabasePath).GetHomeSummary("chiunque").TotalTests);

        // Fault injection REALE: l'header del file viene sovrascritto, quindi SQLite risponde SQLITE_NOTADB
        // alla prima query successiva. Nessun mock: e' lo stesso errore di una corruzione su disco.
        DanneggiaDatabase(store.DatabasePath);

        // Una sola query in errore: il coordinatore ricostruisce e concede l'unico retry consentito, quindi
        // la stessa chiamata torna con il dato giusto. Nessun riavvio dell'applicazione.
        var esecuzioni = 0;
        var lettoDallIndice = coordinatore.TryRead("query dopo lo startup", () =>
        {
            esecuzioni++;
            return new LocalTestArchiveQueryService(store.DatabasePath).GetHomeSummary("chiunque").TotalTests;
        }, out var totaleDopoGuasto);

        Assert.True(lettoDallIndice);
        Assert.Equal(2, esecuzioni);
        Assert.Equal(2, totaleDopoGuasto);
        Assert.Equal(1, coordinatore.RicostruzioniEseguite);
        Assert.True(coordinatore.Generazione > generazioneIniziale);
        Assert.Equal(LocalIndexHealth.Healthy, coordinatore.Salute);
        Assert.True(repo.HasPersistentSnapshot);
        Assert.NotEmpty(Directory.GetFiles(store.WorkspaceDirectory, "index.db.corrupt-*"));

        // Ritorno automatico al percorso indicizzato, senza riavviare l'applicazione.
        var riletto = coordinatore.TryRead("query dopo il recupero",
            () => new LocalTestArchiveQueryService(store.DatabasePath).GetHomeSummary("chiunque").TotalTests,
            out var totale);
        Assert.True(riletto);
        Assert.Equal(2, totale);

        // I JSON condivisi non sono stati toccati in nessun momento.
        Assert.Equal(2, Directory.GetFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void ErroreIndiceDopoIlSave_JsonERevisioneRestanoCorrettiEIlReadModelVieneInvalidato()
    {
        var paths = new DataPaths(_shared);
        var store = new LocalTestIndexStore(_shared, _local);
        var repo = new TestCaseRepository(paths, store);
        var tc = NuovoTest("TC-0200");
        repo.Save(tc, isNew: true);
        repo.RefreshIndex();
        Assert.True(repo.HasPersistentSnapshot);
        var coordinatore = Assert.IsType<LocalIndexRecoveryCoordinator>(repo.IndexRecovery);

        // Accesso negato sull'indice (NON corruzione): il file e' sano, e' l'ambiente a non permettere la
        // scrittura. Il salvataggio JSON deve comunque restare valido e non essere mai ripetuto.
        SqliteConnection.ClearAllPools();
        File.SetAttributes(store.DatabasePath, FileAttributes.ReadOnly);
        try
        {
            var revisionePrima = tc.RevisioneDati;
            repo.Save(tc, isNew: false, revisioneAttesa: revisionePrima);

            var percorso = repo.PercorsoPer(tc);
            var suDisco = JsonIo.TryRead<TestCase>(percorso, out var errore);
            Assert.NotNull(suDisco);
            Assert.Null(errore);
            Assert.Equal(revisionePrima + 1, suDisco!.RevisioneDati);
            // Nessun doppio salvataggio: un solo file, una sola revisione in piu'.
            Assert.Single(Directory.GetFiles(paths.TestCasesDir, "testcase_*.json", SearchOption.AllDirectories));

            // Nessuna pagina puo' continuare a trattare l'indice vecchio come valido.
            Assert.False(repo.HasPersistentSnapshot);
            Assert.Equal(LocalIndexHealth.Unavailable, coordinatore.Salute);
            Assert.Equal(0, coordinatore.RicostruzioniEseguite);
            Assert.Empty(Directory.GetFiles(store.WorkspaceDirectory, "*.corrupt-*"));
        }
        finally
        {
            File.SetAttributes(store.DatabasePath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void SorgenteCondivisaIncompleta_NessunaRicostruzioneVuotaDichiarataCompleta()
    {
        var paths = new DataPaths(_shared);
        ScriviTestCondiviso(paths, "TC-0300");
        // Consegna OneDrive parziale: il file esiste, e' enumerabile, ma il contenuto non e' ancora arrivato.
        File.WriteAllText(Path.Combine(paths.TestCasesDir, "testcase_TC-0301.json"), "{ \"parzial");

        var store = new LocalTestIndexStore(_shared, _local);
        var repo = new TestCaseRepository(paths, store);
        repo.RefreshIndex();

        Assert.False(repo.HasPersistentSnapshot);
        Assert.Equal(LocalIndexHealth.Unavailable, repo.IndexRecovery!.Salute);
        // La fotografia non e' marcata pronta: al riavvio non verra' usata come archivio completo.
        SqliteConnection.ClearAllPools();
        Assert.False(new LocalTestIndexStore(_shared, _local).HasCompletedFullScan);
    }

    [Fact]
    public void SorgenteCondivisaCompleta_MarcaLaFotografiaPronta()
    {
        var paths = new DataPaths(_shared);
        ScriviTestCondiviso(paths, "TC-0310");

        var store = new LocalTestIndexStore(_shared, _local);
        var repo = new TestCaseRepository(paths, store);
        repo.RefreshIndex();

        Assert.True(repo.HasPersistentSnapshot);
        SqliteConnection.ClearAllPools();
        Assert.True(new LocalTestIndexStore(_shared, _local).HasCompletedFullScan);
    }

    [Fact]
    public void RicostruzioneIncompleta_NonRiabilitaILettori()
    {
        var store = PreparaIndiceConUnTest("TC-0400");
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath,
            () => LocalIndexRebuildOutcome.Incompleta("Consegna OneDrive ancora in corso."));

        Assert.False(coordinatore.RecuperaAdesso("corruzione simulata"));

        Assert.Equal(LocalIndexHealth.Unavailable, coordinatore.Salute);
        Assert.False(coordinatore.IsUsable);
        Assert.Equal(0, coordinatore.RicostruzioniEseguite);
        Assert.Contains("OneDrive", coordinatore.Motivo);
        // Nessun lettore viene riammesso su un indice ricostruito a meta'.
        Assert.False(coordinatore.TryRead("lettura", () => { }));
    }

    [Fact]
    public void AvvisoStato_NotificaRicostruzioneInCorsoCompletataEFallita()
    {
        var store = PreparaIndiceConUnTest("TC-0500");
        var completa = true;
        var coordinatore = new LocalIndexRecoveryCoordinator(store.DatabasePath,
            () => completa ? LocalIndexRebuildOutcome.Riuscita(1) : LocalIndexRebuildOutcome.Incompleta("sorgente incompleta"));
        var stati = new List<LocalIndexHealth>();
        coordinatore.StatoCambiato += s => { lock (stati) stati.Add(s.Salute); };

        Assert.True(coordinatore.RecuperaAdesso("primo recupero"));
        completa = false;
        Assert.False(coordinatore.RecuperaAdesso("secondo recupero"));

        lock (stati)
        {
            Assert.Equal(
                new[]
                {
                    LocalIndexHealth.Recovering, LocalIndexHealth.Healthy,
                    LocalIndexHealth.Recovering, LocalIndexHealth.Unavailable
                },
                stati);
        }
    }

    // --------------------------------------------------------------- helper

    [Theory]
    [InlineData("nc")]
    [InlineData("master")]
    [InlineData("entity")]
    [Trait("Category", "Lento")]
    public void AltriIndici_CorruzioneAllAvvio_ConservaIlFileEReinizializza(string tipo)
    {
        var database = tipo switch
        {
            "nc" => new LocalNonConformitaIndexStore(_shared, _local).DatabasePath,
            "master" => new LocalMasterIndexStore(_shared, _local).DatabasePath,
            _ => new LocalEntityIndexStore(_shared, _local).DatabasePath
        };
        DanneggiaDatabase(database);

        var riaperto = tipo switch
        {
            "nc" => new LocalNonConformitaIndexStore(_shared, _local).DatabasePath,
            "master" => new LocalMasterIndexStore(_shared, _local).DatabasePath,
            _ => new LocalEntityIndexStore(_shared, _local).DatabasePath
        };

        Assert.Equal(database, riaperto);
        Assert.True(File.Exists(riaperto));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database) + ".corrupt-*"));
    }

    private static void ImpostaVersioneSchema(string databasePath, string versione)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE meta SET value = $versione WHERE key = 'schema_version';";
        command.Parameters.AddWithValue("$versione", versione);
        command.ExecuteNonQuery();
    }

    private static string? LeggiVersioneSchema(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return command.ExecuteScalar() as string;
    }

    /// <summary>Phase D usa la tabella phase_d_meta (colonne meta_key / meta_value), non meta.</summary>
    private static void ImpostaVersioneSchemaPhaseD(string databasePath, string versione)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE phase_d_meta SET meta_value = $versione WHERE meta_key = 'schema_version';";
        command.Parameters.AddWithValue("$versione", versione);
        command.ExecuteNonQuery();
    }

    private static string? LeggiVersioneSchemaPhaseD(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT meta_value FROM phase_d_meta WHERE meta_key = 'schema_version';";
        return command.ExecuteScalar() as string;
    }

    private LocalTestIndexStore PreparaIndiceConUnTest(string id)
    {
        var store = new LocalTestIndexStore(_shared, _local);
        var path = Path.Combine(new DataPaths(_shared).TestCasesDir, $"testcase_{id}.json");
        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 128, NuovoTest(id))], DateTime.UtcNow);
        return store;
    }

    private static void ScriviTestCondiviso(DataPaths paths, string id) =>
        JsonIo.WriteAtomic(Path.Combine(paths.TestCasesDir, $"testcase_{id}.json"), NuovoTest(id));

    private static TestCase NuovoTest(string id) => new()
    {
        Uid = Guid.NewGuid(),
        Id = id,
        RevisioneDati = 1,
        Componente = new EcuSelection { "Componente-" + id },
        Veicoli = ["Daily"],
        Veicolo = "Daily",
        DescrizioneBreve = "test " + id,
        Descrizione = "descrizione " + id
    };

    /// <summary>Header SQLite distrutto: la prima query successiva risponde SQLITE_NOTADB (26).</summary>
    private static void DanneggiaDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var companion in new[] { databasePath + "-wal", databasePath + "-shm" })
            if (File.Exists(companion)) File.Delete(companion);

        using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("QUESTO NON E' UN DATABASE SQLITE - fault injection"));
        stream.Flush(true);
    }

    private static void RimuoviSolaLettura(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }
}
