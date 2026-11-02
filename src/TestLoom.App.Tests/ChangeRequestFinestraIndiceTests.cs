using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Finestra tra la ricreazione dell'indice CR e la prima riconciliazione completa. Contratto: finche' la fotografia del tipo
/// non e' completa (database nuovo, ricreato dalla quarantena o ricostruzione in corso) nessuna lettura dell'indice viene
/// usata: le pagine, la ricerca, il KPI e i riferimenti ricadono sui JSON autorevoli, quindi non mostrano mai zero o
/// liste parziali come se fossero reali. Tutto su cartelle temporanee.
/// </summary>
public sealed class ChangeRequestFinestraIndiceTests : IDisposable
{
    private static readonly StatoChangeRequest[] StatiCiclo =
    {
        StatoChangeRequest.DaValidare, StatoChangeRequest.Validata, StatoChangeRequest.Implementata,
        StatoChangeRequest.Chiusa, StatoChangeRequest.Annullata, StatoChangeRequest.Aperta,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CrFinestra_" + Guid.NewGuid().ToString("N"));
    private readonly string _indici = Path.Combine(Path.GetTempPath(), "TestLoom_CrFinestraIdx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _indici })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void IndiceV3_AperturaV4_LettureMaiZeroDuranteELaRicostruzione()
    {
        var paths = Percorsi();
        var repo0 = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        for (var n = 1; n <= 60; n++)
            repo0.Save(Cr(n));
        var tutte = repo0.GetAll(includeArchiviate: true);
        var attese = new Attese(AspettateAperte(tutte), tutte.Count(c => !c.Archiviata), tutte.Count);
        var db = repo0.LocalIndexDatabasePath;
        SqliteConnection.ClearAllPools();
        RiportaAlloSchemaV3(db);
        SqliteConnection.ClearAllPools();
        Assert.Equal("3", (string?)Scalare(db, "SELECT value FROM meta WHERE key = 'schema_version';"));

        // Apertura con il build v4: lo schema v3 si migra in place con le righe conservate. Nessuna lettura deve vedere zero.
        var nuovo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        Assert.False(nuovo.HasPersistentSnapshot);
        VerificaLetture(nuovo, attese);

        // Durante la ricostruzione: il refresh gira su un altro thread e le letture continuano. Ogni risposta deve restare quella del condiviso.
        var ricostruzione = Task.Run(() => nuovo.RefreshIndex());
        while (!ricostruzione.IsCompleted)
            VerificaLetture(nuovo, attese);
        ricostruzione.Wait();

        // Subito dopo: indice completo, stesse risposte, schema v4 e fotografia marcata come completa.
        Assert.True(nuovo.HasPersistentSnapshot);
        VerificaLetture(nuovo, attese);
        Assert.Equal("5", (string?)Scalare(db, "SELECT value FROM meta WHERE key = 'schema_version';"));
        Assert.Equal("1", (string?)Scalare(db, "SELECT value FROM meta WHERE key = 'snapshot_completo:change-request';"));
    }

    [Fact]
    public void DopoIlRefresh_LeLettureTornanoSullIndice()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        for (var n = 1; n <= 12; n++)
            repo.Save(Cr(n));
        repo.RefreshIndex();
        var aperte = AspettateAperte(repo.GetAll(includeArchiviate: true));

        // Il condiviso cambia senza refresh: con fotografia completa la lettura resta sull'indice, come prima della correzione.
        File.Delete(paths.ChangeRequestFile(Id(2)));
        Assert.Equal(aperte, repo.CountAperte());
    }

    [Fact]
    public void NuovoPcSenzaIndice_LeLettureDelPrimoRefreshDannoLeCrDelCondiviso()
    {
        var paths = Percorsi();
        for (var n = 1; n <= 25; n++)
            JsonIo.WriteAtomic(paths.ChangeRequestFile(Id(n)), Cr(n));
        var tutte = Enumerable.Range(1, 25).Select(n => Cr(n)).ToList();
        var attese = new Attese(AspettateAperte(tutte), tutte.Count(c => !c.Archiviata), tutte.Count);

        var nuovo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        Assert.False(nuovo.HasPersistentSnapshot);
        VerificaLetture(nuovo, attese);

        nuovo.RefreshIndex();
        Assert.True(nuovo.HasPersistentSnapshot);
        VerificaLetture(nuovo, attese);
    }

    [Fact]
    public void IndiceSenzaMarcatore_NonSiLegge_LeLettureUsanoIlCondiviso()
    {
        // Indice con righe da un'attivita' interrotta prima della prima riconciliazione: nessun marcatore di fotografia completa.
        var paths = Percorsi();
        var repo0 = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        for (var n = 1; n <= 10; n++)
            repo0.Save(Cr(n, stato: StatoChangeRequest.Aperta));

        // Il condiviso cambia senza passare da questo indice (altra postazione): le CR 1..5 risultano chiuse.
        for (var n = 1; n <= 5; n++)
            JsonIo.WriteAtomic(paths.ChangeRequestFile(Id(n)), Cr(n, stato: StatoChangeRequest.Chiusa), overwrite: true);

        var condiviso = Enumerable.Range(1, 10)
            .Select(n => Cr(n, stato: n <= 5 ? StatoChangeRequest.Chiusa : StatoChangeRequest.Aperta)).ToList();
        var attese = new Attese(AspettateAperte(condiviso), condiviso.Count(c => !c.Archiviata), condiviso.Count);

        var nuovo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        Assert.False(nuovo.HasPersistentSnapshot);
        VerificaLetture(nuovo, attese);
    }

    [Fact]
    public void CondivisoIrraggiungibileInPendenza_LeLettureFalliscono_MaiZero()
    {
        var paths = Percorsi();
        var nuovo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        Assert.False(nuovo.HasPersistentSnapshot);
        Directory.Delete(_root, recursive: true);

        Assert.Throws<IOException>(() => nuovo.CountAperte());
        Assert.Throws<IOException>(() => nuovo.Count());
        Assert.Throws<IOException>(() => nuovo.PeekAllReadOnly());
    }

    // ------------------------------------------------------------------ helper

    private sealed record Attese(long Aperte, long NonArchiviate, long Tutte);

    private DataPaths Percorsi()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    private static string Id(int n) => $"CR-2026-{n:D4}";

    /// <summary>Stato e archiviazione deterministici: n%6 sceglie lo stato (quattro aperti su sei), n%7 l'archiviazione.</summary>
    private static ChangeRequest Cr(int n, StatoChangeRequest? stato = null) => new()
    {
        Id = Id(n),
        Titolo = $"Change request {Id(n)}",
        Stato = stato ?? StatiCiclo[n % StatiCiclo.Length],
        Archiviata = n % 7 == 0,
        CreatoDa = "Operatore Prova A",
        DataCreazione = new DateTime(2026, 1, 1),
        UltimaModifica = new DateTime(2026, 2, 1),
    };

    private static long AspettateAperte(IEnumerable<ChangeRequest> crs) =>
        crs.LongCount(c => !c.Archiviata && c.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata));

    /// <summary>Ogni lettura che la UI e la ricerca usano: nessuna deve restituire un valore diverso dal condiviso.</summary>
    private static void VerificaLetture(ChangeRequestRepository repo, Attese attese)
    {
        Assert.Equal(attese.Aperte, repo.CountAperte());
        Assert.Equal(attese.NonArchiviate, repo.Count());
        Assert.Equal(attese.NonArchiviate, (long)repo.PeekAllReadOnly().Count);
        Assert.Equal(attese.NonArchiviate, (long)repo.GetAll().Count);
        Assert.Equal(attese.NonArchiviate, repo.QueryActiveSlice(0, 1000).TotalCount);
        Assert.Equal(attese.Tutte - attese.NonArchiviate, repo.QueryArchivedSlice(0, 1000).TotalCount);
    }

    /// <summary>Riporta il database allo schema v3: colonna generata memorizzata (STORED), nessun marcatore di fotografia.</summary>
    private static void RiportaAlloSchemaV3(string dbPath)
    {
        using var connection = Apri(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE entity_item_copia AS
                SELECT kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json FROM entity_item;
            DROP TABLE entity_item;
            CREATE TABLE entity_item(
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                uid TEXT NOT NULL,
                item_key TEXT NOT NULL COLLATE NOCASE,
                archived INTEGER NOT NULL DEFAULT 0,
                deleted INTEGER NOT NULL DEFAULT 0,
                modified_ticks INTEGER NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                file_length INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                json TEXT NOT NULL,
                stato_testo TEXT GENERATED ALWAYS AS (json_extract(json, '$.Stato')) STORED,
                PRIMARY KEY(kind,path)
            ) WITHOUT ROWID;
            CREATE INDEX ix_entity_kind_key ON entity_item(kind,item_key COLLATE NOCASE);
            CREATE INDEX ix_entity_kind_uid ON entity_item(kind,uid);
            CREATE INDEX ix_entity_kind_archived_modified ON entity_item(kind,archived,modified_ticks DESC);
            CREATE INDEX ix_entity_kind_deleted_modified ON entity_item(kind,deleted,modified_ticks DESC);
            CREATE INDEX ix_entity_kind_archived_stato ON entity_item(kind,archived,stato_testo);
            INSERT INTO entity_item(kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json)
                SELECT kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json FROM entity_item_copia;
            DROP TABLE entity_item_copia;
            DELETE FROM meta WHERE key LIKE 'snapshot_completo:%';
            UPDATE meta SET value = '3' WHERE key = 'schema_version';
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Apri(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalare(string dbPath, string sql)
    {
        using var connection = Apri(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
