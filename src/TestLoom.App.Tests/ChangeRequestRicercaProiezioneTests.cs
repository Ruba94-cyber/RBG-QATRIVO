using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Ricerca testuale delle CR sulla proiezione dell'indice. Contratto: stessi risultati e stesso ordine del percorso JSON
/// (confronto per campo su <see cref="ChangeRequestRepository.PeekAllReadOnly"/>), con proiezione completa, prima della prima
/// riconciliazione, dopo la migrazione dalla versione 4, e dopo salvataggi o modifiche esterne. Tutto su cartelle temporanee.
/// </summary>
public sealed class ChangeRequestRicercaProiezioneTests : IDisposable
{
    private static readonly StatoChangeRequest[] StatiCiclo =
    {
        StatoChangeRequest.DaValidare, StatoChangeRequest.Validata, StatoChangeRequest.Implementata,
        StatoChangeRequest.Chiusa, StatoChangeRequest.Annullata, StatoChangeRequest.Aperta,
    };

    private static readonly string[] Query =
    {
        "freno", "FRENO", "ùò", "ı", "İ", "ſ", "ß", "riga", "SECONDA", "potenza", "rossi", "LUCA", "daily", "alpha",
        "my24", "componente-1", "éta", "ÉTA", "cr-2026-00", "cr-2026-0042", "zzz-assente", "a", "ri", "",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_CrRicerca_" + Guid.NewGuid().ToString("N"));
    private readonly string _indici = Path.Combine(Path.GetTempPath(), "TestLoom_CrRicercaIdx_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _indici })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ProiezioneCompleta_UgualeAlPercorsoJson_PrimiNEOrdine()
    {
        var repo = Apri();
        for (var n = 1; n <= 240; n++)
            repo.Save(Cr(n));
        repo.RefreshIndex();

        Assert.Equal("1", Scalare(repo.LocalIndexDatabasePath, "SELECT value FROM meta WHERE key = 'ricerca_completa:change-request';"));
        VerificaParita(repo);
    }

    [Fact]
    public void PrimaDellaRiconciliazione_RicadeSulJson_StessiRisultati()
    {
        var repo = Apri();
        for (var n = 1; n <= 60; n++)
            repo.Save(Cr(n));

        Assert.Null(Scalare(repo.LocalIndexDatabasePath, "SELECT value FROM meta WHERE key = 'ricerca_completa:change-request';"));
        VerificaParita(repo);
    }

    [Fact]
    public void MigrazioneDallaVersione4_LaProiezioneManca_FinoAlPrimoRefresh()
    {
        var repo = Apri();
        for (var n = 1; n <= 120; n++)
            repo.Save(Cr(n));
        repo.RefreshIndex();
        var db = repo.LocalIndexDatabasePath;
        SqliteConnection.ClearAllPools();
        RiportaAlloSchemaV4(db);
        SqliteConnection.ClearAllPools();
        Assert.Equal("4", Scalare(db, "SELECT value FROM meta WHERE key = 'schema_version';"));

        // Apertura con il build nuovo: la colonna ricerca_testo appare, vuota; la ricerca resta sul JSON fino al refresh.
        var migrato = new ChangeRequestRepository(new DataPaths(_root), localBaseDirectory: _indici);
        Assert.Equal("5", Scalare(db, "SELECT value FROM meta WHERE key = 'schema_version';"));
        Assert.Null(Scalare(db, "SELECT value FROM meta WHERE key = 'ricerca_completa:change-request';"));
        VerificaParita(migrato);

        migrato.RefreshIndex();
        Assert.Equal("1", Scalare(db, "SELECT value FROM meta WHERE key = 'ricerca_completa:change-request';"));
        VerificaParita(migrato);
    }

    [Fact]
    public void SalvataggioEModificaEsterna_AggiornanoLaProiezione()
    {
        var paths = Percorsi();
        var repo = new ChangeRequestRepository(paths, localBaseDirectory: _indici);
        for (var n = 1; n <= 40; n++)
            repo.Save(Cr(n));
        repo.RefreshIndex();
        Assert.Empty(repo.CercaTesto("xqzunico", 8));

        // Salvataggio da questa postazione: il nuovo titolo deve trovarsi subito.
        var daModificare = repo.GetById(Id(3))!;
        daModificare.Titolo = "Titolo salvato XQZUNICO";
        repo.Save(daModificare, daModificare.RevisioneDati);
        Assert.Equal(new[] { Id(3) }, repo.CercaTesto("xqzunico", 8).Select(c => c.Id));

        // Modifica da un'altra postazione: il refresh riallinea la proiezione (titolo vecchio fuori, nuovo dentro).
        var esterna = Cr(5);
        esterna.Titolo = "Titolo esterno ESTERNOUNICO";
        JsonIo.WriteAtomic(paths.ChangeRequestFile(Id(5)), esterna, overwrite: true);
        repo.RefreshIndex();
        Assert.Equal(new[] { Id(5) }, repo.CercaTesto("esternounico", 8).Select(c => c.Id));
        Assert.DoesNotContain(Id(5), repo.CercaTesto("più righe", 1000).Select(c => c.Id)); // il vecchio titolo non deve piu' trovarla
        VerificaParita(repo);
    }

    [Fact]
    public void QueryConACapo_RestaSulJson_RisultatoUguale()
    {
        var repo = Apri();
        for (var n = 1; n <= 60; n++)
            repo.Save(Cr(n));
        repo.RefreshIndex();

        var attesi = repo.PeekAllReadOnly().Where(c => Corrisponde("prima riga\nseconda", c)).Select(c => c.Id).Take(8).ToList();
        Assert.Equal(attesi, repo.CercaTesto("prima riga\nseconda", 8).Select(c => c.Id));
    }

    // ------------------------------------------------------------------ helper

    private ChangeRequestRepository Apri() => new(Percorsi(), localBaseDirectory: _indici);

    private DataPaths Percorsi()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        return paths;
    }

    private static string Id(int n) => $"CR-2026-{n:D4}";

    /// <summary>Campi variati di proposito: maiuscole, lettere accentate e casi speciali del confronto, a capo nella
    /// descrizione, campi vuoti, liste vuote, archiviate e date uguali (ordine per Id a parita' di data).</summary>
    private static ChangeRequest Cr(int n)
    {
        string[] titoli =
        {
            "Freno di stazionamento", "Attuatore àèìòù", "Ruota ß test", "Ispezione İstanbul ı i",
            "S long s", "Titolo su più righe", "Centralina componente", "Diagnosi rapida",
        };
        string[] veicoli = { "Daily 24", "Alpha 24", "MY24" };
        string[] componente = { "Componente-1", "BCM", "componente-ÉTA" };
        return new ChangeRequest
        {
            Id = Id(n),
            Titolo = titoli[n % titoli.Length] + (n % 5 == 0 ? " potenza" : string.Empty),
            Descrizione = n % 4 == 0 ? string.Empty : $"prima riga {n}\nSeconda riga con Potenza e Freno {n % 3}",
            Responsabile = n % 3 == 0 ? string.Empty : (n % 2 == 0 ? "Mario Rossi" : "luca bianchi"),
            Stato = StatiCiclo[n % StatiCiclo.Length],
            Archiviata = n % 9 == 0,
            CreatoDa = "Operatore Prova A",
            DataCreazione = new DateTime(2026, 1, 1),
            UltimaModifica = new DateTime(2026, 2, 1).AddHours(n % 17),
            VeicoliImpattati = Enumerable.Range(0, n % 3).Select(i => veicoli[(n + i) % veicoli.Length]).ToList(),
            EcuImpattate = Enumerable.Range(0, (n + 1) % 3).Select(i => componente[(n + i) % componente.Length]).ToList(),
        };
    }

    /// <summary>Stesso criterio della ricerca globale: un campo contiene la query senza distinguere maiuscole.</summary>
    private static bool Corrisponde(string q, ChangeRequest c) =>
        new[] { c.Id, c.Titolo, c.Descrizione, c.Responsabile }.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
        || c.VeicoliImpattati.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
        || c.EcuImpattate.Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

    private static void VerificaParita(ChangeRequestRepository repo)
    {
        foreach (var q in Query)
        {
            var attesi = repo.PeekAllReadOnly().Where(c => Corrisponde(q, c)).Select(c => c.Id).ToList();
            foreach (var limite in new[] { 8, 1000 })
                Assert.Equal(attesi.Take(limite), repo.CercaTesto(q, limite).Select(c => c.Id));
        }
    }

    /// <summary>Riporta il database allo schema v4: senza colonna ricerca_testo, senza marcatore della proiezione.</summary>
    private static void RiportaAlloSchemaV4(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE entity_item_copia AS
                SELECT kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,stato_testo FROM entity_item;
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
                stato_testo TEXT,
                PRIMARY KEY(kind,path)
            ) WITHOUT ROWID;
            CREATE INDEX ix_entity_kind_key ON entity_item(kind,item_key COLLATE NOCASE);
            CREATE INDEX ix_entity_kind_uid ON entity_item(kind,uid);
            CREATE INDEX ix_entity_kind_archived_modified ON entity_item(kind,archived,modified_ticks DESC);
            CREATE INDEX ix_entity_kind_deleted_modified ON entity_item(kind,deleted,modified_ticks DESC);
            CREATE INDEX ix_entity_kind_archived_stato ON entity_item(kind,archived,stato_testo);
            INSERT INTO entity_item(kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,stato_testo)
                SELECT kind,path,uid,item_key,archived,deleted,modified_ticks,mtime_ticks,file_length,content_hash,json,stato_testo FROM entity_item_copia;
            DROP TABLE entity_item_copia;
            DELETE FROM meta WHERE key LIKE 'ricerca_completa:%';
            UPDATE meta SET value = '4' WHERE key = 'schema_version';
            """;
        command.ExecuteNonQuery();
    }

    private static object? Scalare(string dbPath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
