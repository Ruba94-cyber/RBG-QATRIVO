using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TestLoom.App.Tests;

/// <summary>
/// Migrazione in place dell'indice CR alla versione 5 (colonna ricerca_testo e indice di ordinamento). Le righe restano:
/// nessun indice viene svuotato, messo in quarantena o eliminato; gli indici della v5 sono creati dalla migrazione; l'ordine
/// di <see cref="LocalEntityIndexStore.EnumerateOrdinatoPerLettura{T}"/> resta quello di <see cref="LocalEntityIndexStore.Enumerate{T}"/>.
/// </summary>
public sealed class LocalEntityIndexMigrazioneV5Tests : IDisposable
{
    private const string Kind = "change-request";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_EntMig_" + Guid.NewGuid().ToString("N"));

    private string Shared => Path.Combine(_root, "shared");
    private string Local => Path.Combine(_root, "local");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Migrazione4Verso5_ConservaLeRigheCreaGliIndiciEMantieneLOrdine()
    {
        var db = CreaIndiceCon(3);
        RiportaAVersione4(db);
        Assert.Equal(3L, ContaRighe(db));

        var riaperto = new LocalEntityIndexStore(Shared, Local);

        Assert.Null(riaperto.IndisponibileMotivo);
        Assert.Equal("5", LeggiVersione(db));
        Assert.Equal(3L, ContaRighe(db));                       // nessuno svuotamento
        Assert.True(IndiceEsiste(db, "ix_entity_kind_archived_ordine"));
        Assert.True(IndiceEsiste(db, "ix_entity_kind_archived_ricerca"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db)!, "*.corrupt-*"));  // nessuna quarantena
        AssertOrdineUguale(riaperto, 3);
    }

    [Fact]
    public void Migrazione1Verso5_ConservaLeRigheCreaGliIndiciEMantieneLOrdine()
    {
        var db = CreaIndiceCon(3);
        RiportaAVersione1(db);
        Assert.Equal(3L, ContaRighe(db));

        var riaperto = new LocalEntityIndexStore(Shared, Local);

        Assert.Null(riaperto.IndisponibileMotivo);
        Assert.Equal("5", LeggiVersione(db));
        Assert.Equal(3L, ContaRighe(db));
        Assert.True(IndiceEsiste(db, "ix_entity_kind_archived_ordine"));
        Assert.True(IndiceEsiste(db, "ix_entity_kind_archived_ricerca"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db)!, "*.corrupt-*"));
        AssertOrdineUguale(riaperto, 3);
    }

    private string CreaIndiceCon(int quante)
    {
        var paths = new DataPaths(Shared);
        paths.EnsureStructure();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: Local);
        for (var i = 1; i <= quante; i++)
            repo.Save(Cr($"CR-2026-{i:D4}", i % 2 == 0 ? StatoChangeRequest.Validata : StatoChangeRequest.Aperta));
        repo.RefreshIndex();
        var db = repo.LocalIndexDatabasePath;
        SqliteConnection.ClearAllPools();
        return db;
    }

    /// <summary>Riporta l'indice allo schema 4: senza la colonna ricerca_testo e senza i due indici della v5.</summary>
    private static void RiportaAVersione4(string db)
    {
        using var connection = Apri(db);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX IF EXISTS ix_entity_kind_archived_ricerca;
            DROP INDEX IF EXISTS ix_entity_kind_archived_ordine;
            ALTER TABLE entity_item DROP COLUMN ricerca_testo;
            UPDATE meta SET value = '4' WHERE key = 'schema_version';
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Riporta l'indice allo schema 1: tabella senza stato_testo e ricerca_testo, indici minimi.</summary>
    private static void RiportaAVersione1(string db)
    {
        using var connection = Apri(db);
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
        SqliteConnection.ClearAllPools();
    }

    private static void AssertOrdineUguale(LocalEntityIndexStore store, int attese)
    {
        var perEnumerate = store.Enumerate<ChangeRequest>(Kind, archived: false).Select(r => r.Path).ToList();
        var perLettura = store.EnumerateOrdinatoPerLettura<ChangeRequest>(Kind, archived: false).Select(r => r.Path).ToList();
        Assert.Equal(attese, perLettura.Count);
        Assert.Equal(perEnumerate, perLettura);
    }

    private static long ContaRighe(string db)
    {
        using var connection = Apri(db);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entity_item WHERE kind = 'change-request';";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string? LeggiVersione(string db)
    {
        using var connection = Apri(db);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return command.ExecuteScalar() as string;
    }

    private static bool IndiceEsiste(string db, string nome)
    {
        using var connection = Apri(db);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $nome;";
        command.Parameters.AddWithValue("$nome", nome);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static SqliteConnection Apri(string db)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static ChangeRequest Cr(string id, StatoChangeRequest stato) => new()
    {
        Id = id,
        Titolo = $"Change request {id}",
        Stato = stato,
        CreatoDa = "Operatore Prova A",
        DataCreazione = new DateTime(2026, 1, 1),
        UltimaModifica = new DateTime(2026, 2, 1),
    };
}
