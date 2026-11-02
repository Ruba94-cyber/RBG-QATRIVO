using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Conteggio delle Change Request aperte sulla colonna stato_testo dell'indice locale (schema v4, colonna normale coperta dall'indice).
///
/// Contratti da proteggere: il conteggio SQLite coincide con quello calcolato sui JSON (GetAll) e con la query
/// json_extract della versione precedente; un indice di schema diverso (v1) viene isolato e ricostruito dai JSON
/// all'apertura, senza righe fantasma e senza cancellare il file vecchio; un indice gia' alla versione corrente non
/// viene isolato; una CR cambiata da un'altra postazione si riflette dopo il refresh. Tutto su cartelle temporanee.
/// </summary>
public sealed class ConteggioCrAperteIndiceTests : IDisposable
{
    private static readonly string[] StatiEsclusi = { nameof(StatoChangeRequest.Chiusa), nameof(StatoChangeRequest.Annullata) };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CrAperte_" + Guid.NewGuid().ToString("N"));
    private readonly string _indici = Path.Combine(Path.GetTempPath(), "TestLoom_CrAperteIdx_" + Guid.NewGuid().ToString("N"));
    private readonly string _indiciAltra = Path.Combine(Path.GetTempPath(), "TestLoom_CrAperteIdxB_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _indici, _indiciAltra })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CountAperte_CoincideConIlConteggioSuiJsonEConLaQueryDiretta()
    {
        var repo = new ChangeRequestRepository(Percorsi(), localBaseDirectory: _indici);
        var stati = new[]
        {
            StatoChangeRequest.Aperta, StatoChangeRequest.DaValidare, StatoChangeRequest.Validata,
            StatoChangeRequest.Implementata, StatoChangeRequest.Chiusa, StatoChangeRequest.Annullata,
        };
        for (var n = 1; n <= 36; n++)
            repo.Save(Cr($"CR-2026-{n:D4}", stati[n % stati.Length], archiviata: n % 5 == 0));

        var attese = AspettateAperte(repo.GetAll(includeArchiviate: true));
        Assert.True(attese > 0 && attese < 36, "il dataset deve contenere sia CR aperte sia CR escluse");
        Assert.Equal(attese, repo.CountAperte());

        // La stessa domanda posta direttamente all'indice con l'espressione json_extract: deve dare lo stesso numero.
        var indice = new LocalEntityIndexStore(_root, _indici);
        Assert.Equal(attese, indice.CountJsonTextNotIn(ChangeRequestRepository.LocalEntityKind, nameof(ChangeRequest.Stato), StatiEsclusi, archived: false));
    }

    [Fact]
    public void ColonnaStatoTesto_ValorizzataInScritturaCoincideConJsonExtract()
    {
        var repo = new ChangeRequestRepository(Percorsi(), localBaseDirectory: _indici);
        repo.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        repo.Save(Cr("CR-2026-0002", StatoChangeRequest.Chiusa));
        repo.Save(Cr("CR-2026-0003", StatoChangeRequest.Validata, archiviata: true));
        var db = repo.LocalIndexDatabasePath;

        Assert.Equal(3L, Convert.ToInt64(Scalare(db, "SELECT COUNT(*) FROM entity_item WHERE kind = 'change-request';")));
        Assert.Equal(0L, Convert.ToInt64(Scalare(db,
            "SELECT COUNT(*) FROM entity_item WHERE COALESCE(stato_testo, '') IS NOT COALESCE(json_extract(json, '$.Stato'), '');")));
        Assert.Equal(1L, Convert.ToInt64(Scalare(db,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_entity_kind_archived_stato';")));
        // Colonna normale (hidden = 0, non generata): il valore viene scritto insieme alla riga. Il conteggio deve poter
        // essere soddisfatto dal solo indice: una colonna generata non e' coperta e SQLite rileggerebbe ogni riga.
        Assert.Equal(0L, Convert.ToInt64(Scalare(db,
            "SELECT hidden FROM pragma_table_xinfo('entity_item') WHERE name = 'stato_testo';")));
        Assert.Contains(Piano(db, "SELECT COUNT(*) FROM entity_item WHERE kind = 'change-request' AND archived = 0 " +
            "AND COALESCE(stato_testo, '') NOT IN ('Chiusa', 'Annullata');"), riga => riga.Contains("COVERING INDEX ix_entity_kind_archived_stato", StringComparison.Ordinal));
    }

    [Fact]
    public void IndiceSchemaV1_MigraInPlaceConservaLeRigheERiallineaAlRefresh()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        repo.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        repo.Save(Cr("CR-2026-0002", StatoChangeRequest.Validata));
        repo.Save(Cr("CR-2026-0003", StatoChangeRequest.Chiusa));
        var db = repo.LocalIndexDatabasePath;
        var jsonPrima = File.ReadAllBytes(paths.ChangeRequestFile("CR-2026-0001"));
        SqliteConnection.ClearAllPools();
        RiportaAlloSchemaV1(db, Path.Combine(_root, "fantasma", "cr_CR-FANTASMA.json"));
        SqliteConnection.ClearAllPools();
        Assert.Equal("1", (string?)Scalare(db, "SELECT value FROM meta WHERE key = 'schema_version';"));

        // Apertura con il build nuovo: lo schema v1 si migra in place. Le righe restano, anche la fantasma, come ultima
        // fotografia nota; la riconciliazione del refresh le riallinea ai file.
        var nuova = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        Assert.False(nuova.HasPersistentSnapshot);
        Assert.Equal(3L, nuova.CountAperte()); // CR 1 e 2 aperte, piu' la fantasma ancora nella fotografia
        Assert.Equal(4L, nuova.Count());
        nuova.RefreshIndex();

        Assert.True(nuova.HasPersistentSnapshot);
        Assert.Equal(2L, nuova.CountAperte());
        Assert.Equal(3L, nuova.Count()); // la riga fantasma sparisce alla riconciliazione
        Assert.Equal(new[] { "CR-2026-0001", "CR-2026-0002", "CR-2026-0003" },
            nuova.PeekAllReadOnly().Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("5", (string?)Scalare(db,"SELECT value FROM meta WHERE key = 'schema_version';"));
        Assert.Equal(1L, Convert.ToInt64(Scalare(db,
            "SELECT COUNT(*) FROM pragma_table_xinfo('entity_item') WHERE name = 'stato_testo';")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db)!, "entity-index.db.corrupt-*")); // migrazione in place: nessuna quarantena
        Assert.Equal(jsonPrima, File.ReadAllBytes(paths.ChangeRequestFile("CR-2026-0001")));
    }

    [Fact]
    public void IndiceGiaAllaVersioneCorrente_RiapertoSenzaQuarantena()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        repo.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        repo.Save(Cr("CR-2026-0002", StatoChangeRequest.Chiusa));
        SqliteConnection.ClearAllPools();

        var riaperto = new ChangeRequestRepository(paths, localBaseDirectory: _indici);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(repo.LocalIndexDatabasePath)!, "entity-index.db.corrupt-*"));
        Assert.Equal("5", (string?)Scalare(repo.LocalIndexDatabasePath,"SELECT value FROM meta WHERE key = 'schema_version';"));
        Assert.Equal(1L, riaperto.CountAperte());
    }

    [Fact]
    public void CrChiusaDaUnaltraPostazione_LoStatoSiAggiornaAlRefresh()
    {
        var paths = Percorsi();
        var postazioneA = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        var postazioneB = new ChangeRequestRepository(paths, localBaseDirectory: _indiciAltra);
        postazioneA.Save(Cr("CR-2026-0001", StatoChangeRequest.Aperta));
        postazioneA.Save(Cr("CR-2026-0002", StatoChangeRequest.Validata));
        postazioneB.RefreshIndex();
        Assert.Equal(2L, postazioneB.CountAperte());

        // A chiude la CR 1 con un titolo piu' lungo: la dimensione del file cambia, quindi lo stamp di B non resta valido.
        var daChiudere = postazioneA.GetById("CR-2026-0001")!;
        daChiudere.Stato = StatoChangeRequest.Chiusa;
        daChiudere.Titolo = "Change request chiusa sull'altra postazione";
        postazioneA.Save(daChiudere, daChiudere.RevisioneDati);

        Assert.Equal(2L, postazioneB.CountAperte()); // fotografia locale: prima del refresh resta il valore precedente
        postazioneB.RefreshIndex();
        Assert.Equal(1L, postazioneB.CountAperte());
        Assert.Equal(AspettateAperte(postazioneB.GetAll(includeArchiviate: true)), postazioneB.CountAperte());
    }

    // ------------------------------------------------------------------ helper

    private DataPaths Percorsi()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    private static ChangeRequest Cr(string id, StatoChangeRequest stato, bool archiviata = false) => new()
    {
        Id = id,
        Titolo = $"Change request {id}",
        Stato = stato,
        Archiviata = archiviata,
        CreatoDa = "Operatore Prova A",
        DataCreazione = new DateTime(2026, 1, 1),
        UltimaModifica = new DateTime(2026, 2, 1),
    };

    /// <summary>Stessa regola del ramo JSON di ChangeRequestRepository.CountAperte: non archiviate e non chiuse/annullate.</summary>
    private static long AspettateAperte(IEnumerable<ChangeRequest> crs) =>
        crs.LongCount(c => !c.Archiviata && c.Stato is not (StatoChangeRequest.Chiusa or StatoChangeRequest.Annullata));

    private static SqliteConnection Apri(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        return connection;
    }

    private static List<string> Piano(string dbPath, string sql)
    {
        var righe = new List<string>();
        using var connection = Apri(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) righe.Add(reader.GetString(reader.FieldCount - 1));
        return righe;
    }

    private static object? Scalare(string dbPath, string sql)
    {
        using var connection = Apri(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Riporta il database allo schema v1 (senza colonna stato_testo ne' il suo indice, versione 1 in meta) e vi
    /// aggiunge una riga fantasma: una CR che nei JSON non esiste piu'. Le righe reali restano, come nell'indice
    /// lasciato dalla versione precedente.
    /// </summary>
    private static void RiportaAlloSchemaV1(string dbPath, string fantasma)
    {
        using (var connection = Apri(dbPath))
        using (var command = connection.CreateCommand())
        {
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
                    PRIMARY KEY(kind,path)
                ) WITHOUT ROWID;
                CREATE INDEX ix_entity_kind_key ON entity_item(kind,item_key COLLATE NOCASE);
                CREATE INDEX ix_entity_kind_uid ON entity_item(kind,uid);
                CREATE INDEX ix_entity_kind_archived_modified ON entity_item(kind,archived,modified_ticks DESC);
                CREATE INDEX ix_entity_kind_deleted_modified ON entity_item(kind,deleted,modified_ticks DESC);
                INSERT INTO entity_item(kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json)
                    SELECT kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json FROM entity_item_copia;
                DROP TABLE entity_item_copia;
                UPDATE meta SET value = '1' WHERE key = 'schema_version';
                """;
            command.ExecuteNonQuery();
        }

        using var connessioneRiga = Apri(dbPath);
        using var riga = connessioneRiga.CreateCommand();
        riga.CommandText = """
            INSERT INTO entity_item(kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json)
            VALUES('change-request',$path,'uid-fantasma','CR-FANTASMA',0,0,1,1,1,'fantasma','{"Id":"CR-FANTASMA","Stato":"Aperta"}');
            """;
        riga.Parameters.AddWithValue("$path", fantasma);
        riga.ExecuteNonQuery();
    }
}
