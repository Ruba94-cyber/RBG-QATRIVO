using System.Collections.Concurrent;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Oracolo delle fonti Home: la query originale (prima del modello persistito v16), eseguita sulle proiezioni dei Test.</summary>
internal static class OracoloSorgentiHome
{
    public const string Sql = """
        WITH attivi AS (
        SELECT uid, id FROM test_summary WHERE eliminato = 0
        ),
        veicoli AS (
        SELECT s.uid, COALESCE(v.value, '') AS value
        FROM attivi s LEFT JOIN test_vehicle v ON v.test_uid = s.uid
        ),
        funzionalita AS (
        SELECT s.uid, COALESCE(f.value, '') AS value
        FROM attivi s LEFT JOIN test_functionality f ON f.test_uid = s.uid
        ),
        componenti AS (
        SELECT s.uid, COALESCE(e.value, '') AS value
        FROM attivi s LEFT JOIN test_ecu e ON e.test_uid = s.uid
        ),
        dichiarate AS (
        SELECT s.uid, s.id, f.value AS funzionalita, e.value AS componente, v.value AS veicolo
        FROM attivi s
        JOIN funzionalita f ON f.uid = s.uid
        JOIN componenti e ON e.uid = s.uid
        JOIN veicoli v ON v.uid = s.uid
        ),
        tentativi AS (
        SELECT a.test_uid, a.revision_index, a.attempt_index, a.funzionalita, a.outcome,
        a.data_ticks, s.id, r.data_ticks AS data_revisione
        FROM test_operation_attempt a
        JOIN attivi s ON s.uid = a.test_uid
        JOIN test_revision r ON r.test_uid = a.test_uid AND r.revision_index = a.revision_index
        WHERE a.is_effective = 1
        AND NOT EXISTS (
        SELECT 1 FROM test_dtc_operation d
        WHERE d.test_uid = a.test_uid AND d.revision_index = a.revision_index
        AND d.attempt_index = a.attempt_index
        )
        ),
        tentativo_ecu AS (
        SELECT t.test_uid, t.revision_index, t.attempt_index, e.componente
        FROM tentativi t
        JOIN test_operation_ecu e ON e.test_uid = t.test_uid
        AND e.revision_index = t.revision_index AND e.attempt_index = t.attempt_index
        UNION ALL
        SELECT t.test_uid, t.revision_index, t.attempt_index,
        CASE WHEN COUNT(e.value) = 1 THEN MIN(e.value) ELSE '' END AS componente
        FROM tentativi t
        LEFT JOIN test_ecu e ON e.test_uid = t.test_uid
        WHERE NOT EXISTS (
        SELECT 1 FROM test_operation_ecu oe WHERE oe.test_uid = t.test_uid
        AND oe.revision_index = t.revision_index AND oe.attempt_index = t.attempt_index
        )
        GROUP BY t.test_uid, t.revision_index, t.attempt_index
        ),
        tentativo_funzionalita AS (
        SELECT test_uid, revision_index, attempt_index, funzionalita
        FROM tentativi WHERE TRIM(funzionalita) <> ''
        UNION ALL
        SELECT t.test_uid, t.revision_index, t.attempt_index, f.value
        FROM tentativi t JOIN funzionalita f ON f.uid = t.test_uid
        WHERE TRIM(t.funzionalita) = ''
        ),
        tentativo_candidati AS (
        SELECT t.test_uid, t.id,
        f.funzionalita,
        e.componente, v.value AS veicolo,
        t.outcome AS outcome,
        t.data_revisione AS data_ticks, t.revision_index, t.attempt_index
        FROM tentativi t
        JOIN tentativo_ecu e ON e.test_uid = t.test_uid AND e.revision_index = t.revision_index
        AND e.attempt_index = t.attempt_index
        JOIN tentativo_funzionalita f ON f.test_uid = t.test_uid AND f.revision_index = t.revision_index
        AND f.attempt_index = t.attempt_index
        JOIN veicoli v ON v.uid = t.test_uid
        ),
        revision_fallback AS (
        SELECT d.uid AS test_uid, d.id, d.funzionalita, d.componente, d.veicolo,
        CASE WHEN EXISTS (
        SELECT 1 FROM test_operation_attempt a
        WHERE a.test_uid = r.test_uid AND a.revision_index = r.revision_index AND a.is_effective = 1
        ) THEN NULL ELSE r.outcome END AS outcome,
        r.data_ticks, r.revision_index, 0 AS attempt_index
        FROM test_revision r JOIN dichiarate d ON d.uid = r.test_uid
        WHERE NOT EXISTS (
        SELECT 1 FROM test_operation_attempt a
        WHERE a.test_uid = r.test_uid AND a.revision_index = r.revision_index AND a.is_effective = 1
        AND NOT EXISTS (
        SELECT 1 FROM test_dtc_operation guasto WHERE guasto.test_uid = a.test_uid
        AND guasto.revision_index = a.revision_index AND guasto.attempt_index = a.attempt_index
        )
        )
        ),
        nessuna_revisione AS (
        SELECT d.uid AS test_uid, d.id, d.funzionalita, d.componente, d.veicolo,
        NULL AS outcome, 0 AS data_ticks, -1 AS revision_index, 0 AS attempt_index
        FROM dichiarate d
        WHERE NOT EXISTS (SELECT 1 FROM test_revision r WHERE r.test_uid = d.uid)
        ),
        candidati AS (
        SELECT test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index
        FROM tentativo_candidati
        UNION ALL
        SELECT test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index
        FROM revision_fallback
        UNION ALL
        SELECT test_uid, id, funzionalita, componente, veicolo, outcome, data_ticks, revision_index, attempt_index
        FROM nessuna_revisione
        ),
        classificati AS (
        SELECT *,
        CASE WHEN outcome IN ($conforme, $nonConforme, $deviazioni) THEN 1 ELSE 0 END AS eseguito,
        CASE WHEN outcome IS NULL OR outcome = $daEseguire THEN 1 ELSE 0 END AS previsto
        FROM candidati
        ),
        ordinati AS (
        SELECT *, ROW_NUMBER() OVER (
        PARTITION BY funzionalita COLLATE NOCASE, componente COLLATE NOCASE, veicolo COLLATE NOCASE
        ORDER BY eseguito DESC, previsto DESC, data_ticks DESC, id COLLATE BINARY DESC,
        revision_index DESC, attempt_index DESC
        ) AS posizione
        FROM classificati
        )
        SELECT funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index
        FROM ordinati WHERE posizione = 1;
        """;

    public static List<string> Oracolo(SqliteConnection connection, SqliteTransaction? transaction = null) =>
        Leggi(connection, transaction, Sql, withParams: true);

    /// <summary>Righe del modello persistito (home_source_winner), in forma canonica.</summary>
    public static List<string> Persistito(SqliteConnection connection, SqliteTransaction? transaction = null) =>
        Leggi(connection, transaction,
            "SELECT funzionalita, componente, veicolo, outcome, data_ticks, id, revision_index, attempt_index FROM home_source_winner;",
            withParams: false);

    /// <summary>Righe prodotte dal servizio pubblico GetHomeFunctionalitySources, in forma canonica.</summary>
    public static List<string> Servizio(string databasePath)
    {
        var sorgenti = new LocalTestArchiveQueryService(databasePath).GetHomeFunctionalitySources();
        return sorgenti
            .Select(s => Canonica(s.Funzionalita, s.Componente, s.Veicolo, (int?)(s.Outcome is { } e ? (int)e : null),
                s.DataTicks, s.TestId, s.Revisione, s.Tentativo))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static string Canonica(string funzionalita, string componente, string veicolo, int? outcome, long dataTicks, string id,
        int revisione, int tentativo) =>
        string.Join("|", funzionalita, componente, veicolo, outcome?.ToString() ?? "", dataTicks.ToString(), id,
            revisione.ToString(), tentativo.ToString());

    private static List<string> Leggi(SqliteConnection connection, SqliteTransaction? transaction, string sql, bool withParams)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (withParams)
        {
            command.Parameters.AddWithValue("$conforme", (int)Esito.Conforme);
            command.Parameters.AddWithValue("$nonConforme", (int)Esito.NonConforme);
            command.Parameters.AddWithValue("$deviazioni", (int)Esito.ConformeConDeviazioni);
            command.Parameters.AddWithValue("$daEseguire", (int)Esito.DaEseguire);
        }
        var righe = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            righe.Add(Canonica(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt64(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetInt32(7)));
        righe.Sort(StringComparer.Ordinal);
        return righe;
    }
}

/// <summary>Fonti della Home (v16): il modello persistito deve dare esattamente l'oracolo, dopo migrazione, inserimenti,
/// correzioni, cancellazioni, eliminazioni logiche e batch, con revisioni, tentativi, operazioni guasto, pareggi e varianti di maiuscole.</summary>
public sealed class LocalHomeSorgentiV16Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_SorgV16_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Base = new(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Esito[] Esiti =
    {
        Esito.DaEseguire, Esito.Conforme, Esito.NonConforme, Esito.ConformeConDeviazioni, Esito.NonApplicabile,
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private LocalTestIndexStore Apri() => new(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

    private string PathTest(string id) => Path.Combine(_root, "data", id + ".json");

    private static Guid GuidCasuale(Random rnd)
    {
        var bytes = new byte[16];
        rnd.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static List<string> Scegli(Random rnd, string[] valori, int min, int max)
    {
        var mescolati = valori.OrderBy(_ => rnd.Next()).ToList();
        return mescolati.Take(rnd.Next(min, max + 1)).ToList();
    }

    /// <summary>Test casuale ma deterministico per seme: dichiarazioni con varianti di maiuscole, revisioni, condizioni, guasto.</summary>
    private static TestCase CasoCasuale(Random rnd, int indice, Guid? uid = null)
    {
        string[] funzionalita = { "F1", "f1", "F2", "F3", "Ff" };
        string[] componente = { "E1", "e1", "E2", "E3" };
        string[] veicoli = { "V1", "v1", "V2", "" };
        var tc = new TestCase
        {
            Id = "TC-" + indice.ToString("D3"),
            Uid = uid ?? GuidCasuale(rnd),
            DataCreazione = Base,
            UltimaModifica = Base.AddHours(rnd.Next(0, 3)),
            CreatoDa = "Op",
            ModificatoDa = "Op",
            DescrizioneBreve = "Caso " + indice,
            Descrizione = "Caso " + indice,
            StatoLavoro = StatoLavoroTest.Concluso,
            Eliminato = rnd.Next(12) == 0,
            Funzionalita = Scegli(rnd, funzionalita, 0, 2),
            Componente = new EcuSelection(Scegli(rnd, componente, 0, 2)),
            Veicoli = Scegli(rnd, veicoli, 0, 2),
        };
        var revisioni = rnd.Next(0, 4);
        for (var r = 0; r < revisioni; r++)
        {
            var condizioni = new List<Condizione>();
            var n = rnd.Next(0, 4);
            for (var c = 0; c < n; c++)
            {
                var condizione = new Condizione
                {
                    Funzionalita = rnd.Next(6) == 0 ? "" : funzionalita[rnd.Next(funzionalita.Length)],
                    Componente = new EcuSelection(Scegli(rnd, componente, 0, 2)),
                    Esito = Esiti[rnd.Next(Esiti.Length)],
                };
                if (rnd.Next(5) == 0)
                {
                    condizione.Guasto = "P0" + rnd.Next(100, 999);
                    condizione.DtcRequisitoId = "REQ-" + rnd.Next(3);
                }
                condizioni.Add(condizione);
            }
            tc.Esecuzioni.Add(new Esecuzione
            {
                Iterazione = r + 1,
                Data = Base.AddDays(rnd.Next(0, 3)),
                Operatore = "Op",
                Esito = Esiti[rnd.Next(Esiti.Length)],
                Condizioni = condizioni,
            });
        }
        return tc;
    }

    private static void EseguiScript(SqliteConnection connection, string script)
    {
        using var command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    private static string VersioneSchema(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return (string)command.ExecuteScalar()!;
    }

    private static long Conteggio(string databasePath, string tabella)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tabella};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>Riporta l'indice alla forma v15: stesse proiezioni dei Test, senza tabelle delle fonti e versione 15.</summary>
    private static void RiportaAVersione15(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        EseguiScript(connection,
            "DROP TABLE IF EXISTS home_source_winner; DROP TABLE IF EXISTS home_source_candidate; " +
            "DROP TABLE IF EXISTS home_source_dirty; UPDATE meta SET value = '15' WHERE key = 'schema_version';");
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Verifica completa: oracolo, modello persistito e servizio pubblico devono coincidere.</summary>
    private static void VerificaUguale(LocalTestIndexStore store, string passo)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        var oracolo = OracoloSorgentiHome.Oracolo(connection);
        var persistito = OracoloSorgentiHome.Persistito(connection);
        var servizio = OracoloSorgentiHome.Servizio(store.DatabasePath);
        Assert.True(oracolo.SequenceEqual(persistito), $"{passo}: il modello persistito diverge dall'oracolo ({oracolo.Count} contro {persistito.Count} righe)");
        Assert.True(oracolo.SequenceEqual(servizio), $"{passo}: il servizio diverge dall'oracolo ({oracolo.Count} contro {servizio.Count} righe)");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void FontiHome_UgualiAllOracolo_ConInserimentiCorrezioniCancellazioniEBatch(int seme)
    {
        var rnd = new Random(seme);
        var store = Apri();
        var uid = new Dictionary<int, Guid>();
        for (var i = 0; i < 30; i++)
        {
            var tc = CasoCasuale(rnd, i);
            uid[i] = tc.Uid;
            store.Upsert(PathTest(tc.Id), Base, 100, tc);
        }
        VerificaUguale(store, "archivio iniziale");

        for (var passo = 0; passo < 25; passo++)
        {
            var i = rnd.Next(30);
            var tc = CasoCasuale(rnd, i, uid[i]);
            switch (rnd.Next(4))
            {
                case 0:
                    store.Remove(PathTest(tc.Id));
                    break;
                default:
                    store.Upsert(PathTest(tc.Id), Base, 100, tc);
                    break;
            }
            VerificaUguale(store, "correzione " + passo);
        }

        var piccolo = Enumerable.Range(30, 12).Select(i =>
        {
            var tc = CasoCasuale(rnd, i);
            uid[i] = tc.Uid;
            return LocalIndexMutation.Upsert(PathTest(tc.Id), Base, 100, tc);
        }).ToList();
        store.ApplyBatch(piccolo);
        VerificaUguale(store, "batch sotto soglia");

        var grande = Enumerable.Range(100, 70).Select(i =>
        {
            var tc = CasoCasuale(rnd, i);
            return LocalIndexMutation.Upsert(PathTest(tc.Id), Base, 100, tc);
        }).ToList();
        store.ApplyBatch(grande);
        VerificaUguale(store, "batch sopra soglia");
    }

    [Fact]
    public void Migrazione15a16_RicostruisceLeFontiDallaProiezioneEsistente()
    {
        var store = Apri();
        for (var i = 0; i < 40; i++)
        {
            var tc = CasoCasuale(new Random(1000 + i), i);
            store.Upsert(PathTest(tc.Id), Base, 100, tc);
        }
        var db = store.DatabasePath;
        RiportaAVersione15(db);

        var riaperto = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.True(Conteggio(db, "home_source_winner") > 0);
        VerificaUguale(riaperto, "dopo migrazione 15->16");
    }

    [Fact]
    public void Migrazione15a16_InterruzioneFaRollbackERitentoCompleta()
    {
        var store = Apri();
        for (var i = 0; i < 40; i++)
        {
            var tc = CasoCasuale(new Random(2000 + i), i);
            store.Upsert(PathTest(tc.Id), Base, 100, tc);
        }
        var db = store.DatabasePath;
        RiportaAVersione15(db);

        LocalTestIndexStore.PuntoInterruzioneMigrazione = _ => throw new InvalidOperationException("interruzione di prova");
        try
        {
            Assert.Throws<InvalidOperationException>(() => Apri());
        }
        finally
        {
            LocalTestIndexStore.PuntoInterruzioneMigrazione = null;
        }

        // Interruzione prima del commit: la versione resta 15 e nessuna fonte e' stata scritta.
        Assert.Equal("15", VersioneSchema(db));
        Assert.Equal(0, Conteggio(db, "home_source_winner"));

        // Ritento senza interruzione: la migrazione si completa e produce l'oracolo.
        var riprovato = Apri();
        Assert.Equal("16", VersioneSchema(db));
        VerificaUguale(riprovato, "dopo ritento della migrazione");
    }

    [Fact]
    public void FontiHome_LettoreEScrittoreSuDueConnessioni_SnapshotSempreCoerente()
    {
        var store = Apri();
        for (var i = 0; i < 20; i++)
        {
            var tc = CasoCasuale(new Random(7 + i), i);
            store.Upsert(PathTest(tc.Id), Base, 100, tc);
        }
        var db = store.DatabasePath;
        var errori = new ConcurrentBag<Exception>();

        var scrittore = Task.Run(() =>
        {
            try
            {
                var rnd = new Random(99);
                for (var k = 0; k < 120; k++)
                {
                    var i = rnd.Next(20);
                    var tc = CasoCasuale(rnd, i);
                    if (k % 7 == 0) store.Remove(PathTest(tc.Id));
                    else store.Upsert(PathTest(tc.Id), Base, 100, tc);
                }
            }
            catch (Exception ex)
            {
                errori.Add(ex);
            }
        });

        var lettore = Task.Run(() =>
        {
            try
            {
                for (var k = 0; k < 120; k++)
                {
                    using var connection = new SqliteConnection($"Data Source={db}");
                    connection.Open();
                    using var transaction = connection.BeginTransaction();
                    var oracolo = OracoloSorgentiHome.Oracolo(connection, transaction);
                    var persistito = OracoloSorgentiHome.Persistito(connection, transaction);
                    transaction.Commit();
                    if (!oracolo.SequenceEqual(persistito))
                        throw new InvalidOperationException("snapshot incoerente al passo " + k);
                }
            }
            catch (Exception ex)
            {
                errori.Add(ex);
            }
        });

        Task.WaitAll(scrittore, lettore);
        Assert.Empty(errori);
        VerificaUguale(store, "dopo scrittura concorrente");
    }

    [Fact]
    public void FontiHome_ConteggiDellaHomeUgualiAllOracolo()
    {
        var rnd = new Random(42);
        var store = Apri();
        for (var i = 0; i < 40; i++)
        {
            var tc = CasoCasuale(rnd, i);
            store.Upsert(PathTest(tc.Id), Base, 100, tc);
        }
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        IReadOnlyList<LocalHomeFunctionalitySource> daOracolo = OracoloSorgentiHome.Oracolo(connection)
            .Select(riga => riga.Split('|'))
            .Select(c => new LocalHomeFunctionalitySource(c[0], c[1], c[2],
                c[3].Length == 0 ? null : (Esito)int.Parse(c[3]), long.Parse(c[4]), c[5], int.Parse(c[6]), int.Parse(c[7])))
            .ToList();
        var daServizio = new LocalTestArchiveQueryService(store.DatabasePath).GetHomeFunctionalitySources();

        var statoOracolo = HomeConformitySummaryService.CalcolaStatoCorrente(daOracolo, [], [], [], []);
        var statoServizio = HomeConformitySummaryService.CalcolaStatoCorrente(daServizio, [], [], [], []);
        Assert.Equal(statoOracolo, statoServizio);
    }

    [Theory]
    [InlineData(0L, 64)]
    [InlineData(3_600L, 72)]
    [InlineData(100_000L, 2_000)]
    [InlineData(1_000_000L, 20_000)]
    public void SogliaRicostruzione_ProporzionaleConMinimo64(long totale, int attesa) =>
        Assert.Equal(attesa, LocalTestIndexStore.SogliaRicostruzioneSorgentiHome(totale));

    [Fact]
    public void FontiHome_SogliaProporzionale_SottoESopraSogliaUgualiAllOracolo()
    {
        // Review (MEDIUM perf): con archivio grande la soglia sale a 1/50 dei Test. Lo stesso archivio deve dare lo stesso risultato
        // sia con il ricalcolo per Test (sotto soglia) sia con la ricostruzione globale (sopra soglia): entrambi confrontati con l'oracolo.
        var rnd = new Random(42);
        var store = Apri();
        var primo = Enumerable.Range(0, 3_600).Select(i =>
        {
            var tc = CasoCasuale(rnd, i);
            return LocalIndexMutation.Upsert(PathTest(tc.Id), Base, 100, tc);
        }).ToList();
        store.ApplyBatch(primo);   // archivio di 3600 Test: 3600 >= soglia -> ricostruzione globale
        VerificaUguale(store, "archivio 3600 Test");

        var sotto = Enumerable.Range(3_600, 70).Select(i =>
        {
            var tc = CasoCasuale(rnd, i);
            return LocalIndexMutation.Upsert(PathTest(tc.Id), Base, 100, tc);
        }).ToList();
        store.ApplyBatch(sotto);   // 70 < soglia (3670/50 = 73): ricalcolo per Test
        VerificaUguale(store, "batch sotto soglia proporzionale");

        var sopra = Enumerable.Range(4_000, 80).Select(i =>
        {
            var tc = CasoCasuale(rnd, i);
            return LocalIndexMutation.Upsert(PathTest(tc.Id), Base, 100, tc);
        }).ToList();
        store.ApplyBatch(sopra);   // 80 >= soglia (3750/50 = 75): ricostruzione globale
        VerificaUguale(store, "batch sopra soglia proporzionale");
    }

    [Fact]
    public void IndiceVincitoreDiPartizione_ReCreatoAdOgniAperturaEUsatoSenzaOrdinamentoTemporaneo()
    {
        // Review (MEDIUM perf): l'indice di copertura deve esistere sia in un DB nuovo sia dopo la migrazione 15->16, e deve
        // essere ricreato da un DB v16 scritto dalla build precedente che non lo aveva (CREATE INDEX IF NOT EXISTS a ogni apertura).
        var store = Apri();
        for (var i = 0; i < 5; i++)
        {
            var tc = CasoCasuale(new Random(500 + i), i);
            store.Upsert(PathTest(tc.Id), Base, 100, tc);
        }
        var db = store.DatabasePath;
        const string indice = "ix_home_source_candidate_winner";

        Assert.Equal(1, ConteggioIndice(db, indice));
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            var piano = PianoVincitore(connection);
            Assert.Contains(indice, piano);
            Assert.DoesNotContain("TEMP B-TREE", piano);
        }

        // DB v16 della build precedente: stesso schema senza l'indice.
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            EseguiScript(connection, $"DROP INDEX {indice};");
        }
        SqliteConnection.ClearAllPools();
        Apri();
        Assert.Equal(1, ConteggioIndice(db, indice));

        // Migrazione 15->16: la tabella candidati viene ricreata con il suo indice.
        RiportaAVersione15(db);
        var riaperto = Apri();
        Assert.Equal("16", VersioneSchema(db));
        Assert.Equal(1, ConteggioIndice(db, indice));
        VerificaUguale(riaperto, "dopo migrazione 15->16 con indice");
    }

    private static long ConteggioIndice(string databasePath, string nome)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $n;";
        command.Parameters.AddWithValue("$n", nome);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string PianoVincitore(SqliteConnection connection)
    {
        // Il piano della query del vincitore (come eseguita da AllineaSorgentiHome): stessi parametri, valori di prova.
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + LocalTestIndexStore.SqlVincitoreDiPartizioneHome;
        command.Parameters.AddWithValue("$f", "F");
        command.Parameters.AddWithValue("$e", "E");
        command.Parameters.AddWithValue("$v", "V");
        using var reader = command.ExecuteReader();
        var righe = new List<string>();
        while (reader.Read()) righe.Add(reader.GetString(3));
        return string.Join("\n", righe);
    }
}
