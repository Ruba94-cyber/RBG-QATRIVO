using System.Diagnostics;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class LocalMasterIndexStoreTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LocalMasterIndex_" + Guid.NewGuid().ToString("N"));

    public LocalMasterIndexStoreTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, true); }
        catch { }
    }

    [Fact]
    public void ReadModel_RestaFuoriDalWorkspaceCondiviso_EFiltraArchiviati()
    {
        var shared = Path.Combine(_root, "shared");
        var local = Path.Combine(_root, "local");
        Directory.CreateDirectory(shared);
        var store = new LocalMasterIndexStore(shared, local);
        var items = new List<Componente>
        {
            new() { Nome = "BCM", VeicoliAssociati = new() { "Daily" }, ModificatoDa = "Seed" },
            new() { Nome = "OLD", Archiviata = true, VeicoliAssociati = new() { "Legacy" }, ModificatoDa = "Seed" }
        };

        var result = store.ReconcileCatalog("componente", items, e => e.Nome, e => e.Archiviata,
            e => string.Join(' ', e.VeicoliAssociati), sourceFingerprint: "v1");

        Assert.Equal(2, result.Added);
        Assert.False(result.SkippedAsCurrent);
        Assert.False(Path.GetFullPath(store.DatabasePath).StartsWith(Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, store.Count("componente"));
        Assert.Equal(2, store.Count("componente", includeArchived: true));
        Assert.Equal("BCM", Assert.Single(store.Query<Componente>("componente", "Daily", false, 0, 20)).Key);
        Assert.Empty(store.Query<Componente>("componente", "Legacy", false, 0, 20));
        Assert.Equal("OLD", Assert.Single(store.Query<Componente>("componente", "Legacy", true, 0, 20)).Key);
    }

    [Fact]
    public void Reconcile_StessaVersioneVieneSaltata_EVersioneNuovaScriveSoloDelta()
    {
        var shared = Path.Combine(_root, "shared-delta");
        var store = new LocalMasterIndexStore(shared, Path.Combine(_root, "local-delta"));
        var first = new List<Requisito>
        {
            new() { Id = "R-1", Testo = "Uno", ModificatoDa = "Seed" },
            new() { Id = "R-2", Testo = "Due", ModificatoDa = "Seed" },
            new() { Id = "R-3", Testo = "Tre", ModificatoDa = "Seed" }
        };
        var initial = store.ReconcileCatalog("requisiti", first, r => r.Id, r => r.Archiviata, r => r.Testo, "source-A");
        Assert.Equal(3, initial.Added);

        var skipped = store.ReconcileCatalog("requisiti", first, r => r.Id, r => r.Archiviata, r => r.Testo, "source-A");
        Assert.True(skipped.SkippedAsCurrent);
        Assert.Equal(0, skipped.Added + skipped.Updated + skipped.Removed);

        var second = new List<Requisito>
        {
            new() { Id = "R-1", Testo = "Uno", ModificatoDa = "Seed" },
            new() { Id = "R-2", Testo = "Due modificato", ModificatoDa = "PC-A" },
            new() { Id = "R-4", Testo = "Quattro", ModificatoDa = "PC-A" }
        };
        var delta = store.ReconcileCatalog("requisiti", second, r => r.Id, r => r.Archiviata, r => r.Testo, "source-B");

        Assert.False(delta.SkippedAsCurrent);
        Assert.Equal(1, delta.Added);
        Assert.Equal(1, delta.Updated);
        Assert.Equal(1, delta.Removed);
        Assert.Equal(3, store.Count("requisiti", true));
        Assert.Empty(store.Query<Requisito>("requisiti", "Tre", true, 0, 20));
        Assert.Equal("R-2", Assert.Single(store.Query<Requisito>("requisiti", "modificato", true, 0, 20)).Key);
    }

    [Fact]
    public void DatabaseLocaleCorrotto_VieneQuarantinatoERicreatoVuoto()
    {
        var shared = Path.Combine(_root, "shared-recovery");
        var local = Path.Combine(_root, "local-recovery");
        var first = new LocalMasterIndexStore(shared, local);
        first.ReconcileCatalog("componente",
            new List<Componente> { new() { Nome = "Componente-1", ModificatoDa = "Seed" } },
            e => e.Nome, e => e.Archiviata, e => e.Nome, "v1");
        var db = first.DatabasePath;
        Assert.Equal(1, first.Count("componente", true));

        SqliteConnection.ClearAllPools();
        File.WriteAllText(db, "questo non e un database sqlite");

        var recovered = new LocalMasterIndexStore(shared, local);
        Assert.Equal(0, recovered.Count("componente", true));
        Assert.True(Directory.GetFiles(Path.GetDirectoryName(db)!, "master-index.db.corrupt-*").Length >= 1);
    }

    [Fact]
    public void Ricerca_PareggiDiRilevanzaRestanoStabiliDopoAggiornamenti()
    {
        var store = new LocalMasterIndexStore(Path.Combine(_root, "shared-pareggi"), Path.Combine(_root, "local-pareggi"));
        // Inserimento in ordine ST01, ST1, ST02, ST2: il rowid non deve decidere i pareggi. Stesso testo (stessa
        // rilevanza) e stessa chiave naturale (ST0000000001 e ST0000000002): lo spareggio e' l'uid, poi la chiave.
        var iniziali = new List<Requisito>
        {
            Req("ST01", "termine comune", Uid(0xb1)),
            Req("ST1", "termine comune", Uid(0xa1)),
            Req("ST02", "termine comune", Uid(0xd2)),
            Req("ST2", "termine comune", Uid(0xc2)),
        };
        Assert.Equal(4, Riconcilia(store, iniziali, "v1").Added);

        var attesi = new[] { "ST1", "ST01", "ST2", "ST02" };
        Assert.Equal(attesi, Chiavi(store.Query<Requisito>("requisiti", "termine", false, 0, 20)));
        Assert.Equal(attesi, Chiavi(store.Query<Requisito>("requisiti", null, false, 0, 20)));

        // Aggiornamento che non tocca il testo cercato: la riga cambia (hash) e la FTS viene riscritta.
        var aggiornati = new List<Requisito>
        {
            Req("ST01", "termine comune", Uid(0xb1)),
            Req("ST1", "termine comune", Uid(0xa1), modificatoDa: "PC-B"),
            Req("ST02", "termine comune", Uid(0xd2)),
            Req("ST2", "termine comune", Uid(0xc2)),
        };
        Assert.Equal(1, Riconcilia(store, aggiornati, "v2").Updated);
        Assert.Equal(attesi, Chiavi(store.Query<Requisito>("requisiti", "termine", false, 0, 20)));
        Assert.Equal(attesi, Chiavi(store.Query<Requisito>("requisiti", null, false, 0, 20)));
        AsseriFtsAllineata(store);

        // Rimozione e reinserimento di ST01 con lo stesso uid: il nuovo rowid non cambia l'ordine.
        Riconcilia(store, aggiornati.Where(r => r.Id != "ST01").ToList(), "v3");
        Assert.Equal(new[] { "ST1", "ST2", "ST02" }, Chiavi(store.Query<Requisito>("requisiti", "termine", false, 0, 20)));
        Riconcilia(store, aggiornati, "v4");
        Assert.Equal(attesi, Chiavi(store.Query<Requisito>("requisiti", "termine", false, 0, 20)));
        Assert.Equal(attesi, Chiavi(store.Query<Requisito>("requisiti", null, false, 0, 20)));
        AsseriFtsAllineata(store);
    }

    [Fact]
    public void Ricerca_MatchEOrdineRestanoUgualiAllaBase_ConAggiornamentiEPaginazione()
    {
        var store = new LocalMasterIndexStore(Path.Combine(_root, "shared-parita"), Path.Combine(_root, "local-parita"));
        var rnd = new Random(20261011);
        var parole = new[] { "alfa", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa" };
        string TestoCasuale() => string.Join(' ', Enumerable.Range(0, 2 + rnd.Next(5)).Select(_ => parole[rnd.Next(parole.Length)]));
        var uidPer = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        Guid UidPer(string chiave)
        {
            if (!uidPer.TryGetValue(chiave, out var uid))
            {
                var bytes = new byte[16];
                rnd.NextBytes(bytes);
                uid = new Guid(bytes);
                uidPer[chiave] = uid;
            }
            return uid;
        }

        // Fotografia autorevole come chiave -> (testo, archiviata): ogni passo la rigenera per intero.
        var voci = new Dictionary<string, (string Testo, bool Archiviata)>(StringComparer.OrdinalIgnoreCase);
        List<Requisito> Fotografia() => voci.Select(v => Req(v.Key, v.Value.Testo, UidPer(v.Key), v.Value.Archiviata)).ToList();

        // Fotografia 1: 60 voci con chiavi miste e testi ripetuti, alcune archiviate, piu' una coppia a pari chiave
        // naturale e pari testo (RQ1 / RQ01): pareggio totale di rilevanza e di key_sort.
        for (var i = 0; i < 60; i++)
            voci[i % 10 == 0 ? $"RQ{i}" : $"REQ-{i:D3}"] = (TestoCasuale(), i % 9 == 4);
        voci["RQ1"] = ("alfa beta", false);
        voci["RQ01"] = ("alfa beta", false);
        Riconcilia(store, Fotografia(), "p1");

        // Fotografia 2: testi rigenerati per 1 voce su 7, rimozione di 1 su 11, nuove voci con una seconda coppia a pari chiave.
        // La coppia a pari testo (RQ1 / RQ01) resta invariata: serve al controllo sui pareggi del passo di query.
        var coppia = new HashSet<string>(new[] { "RQ1", "RQ01" }, StringComparer.OrdinalIgnoreCase);
        var chiavi = voci.Keys.Where(k => !coppia.Contains(k)).ToList();
        for (var i = 0; i < chiavi.Count; i++)
            if (i % 7 == 3) voci[chiavi[i]] = (TestoCasuale(), voci[chiavi[i]].Archiviata);
        foreach (var chiave in chiavi.Where((_, i) => i % 11 == 5).ToList())
            voci.Remove(chiave);
        for (var n = 900; n < 905; n++)
            voci[$"REQ-{n}"] = (TestoCasuale(), false);
        voci["RQ900"] = ("gamma gamma delta", false);
        voci["RQ0900"] = ("gamma gamma delta", false);
        Riconcilia(store, Fotografia(), "p2");
        AsseriFtsAllineata(store);

        // Riferimento: le stesse voci finali nel layout dello schema 2 (FTS con catalog/item_key), query identiche alla base.
        var rifDb = Path.Combine(_root, "riferimento-v2", "master-index.db");
        CreaIndiceV2(rifDb, voci.Select(v => ("requisiti", v.Key, v.Value.Archiviata, NormalizzaTesto(v.Key + " " + v.Value.Testo))));
        using var rif = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = rifDb }.ToString());
        rif.Open();

        foreach (var query in new[] { "alfa", "alf", "alfa beta", "e", "gamma delta", "theta", "rq", "req" })
        {
            var riferimento = QueryBase(rif, query);
            var bm25 = riferimento.ToDictionary(x => x.Key, x => x.Bm25, StringComparer.OrdinalIgnoreCase);
            var nuovo = Chiavi(store.Query<Requisito>("requisiti", query, false, 0, 1000));
            var navigazione = Chiavi(store.Query<Requisito>("requisiti", null, false, 0, 1000));
            // Il confronto non deve essere vacuo: risultati presenti e, per la query con la coppia a pari testo,
            // almeno un gruppo di rilevanza con piu' di una voce (il pareggio viene davvero esercitato).
            Assert.NotEmpty(nuovo);
            if (query == "alfa beta")
                Assert.Contains(nuovo.GroupBy(k => bm25[k]), g => g.Count() > 1);

            // Stesso insieme di risultati della base.
            Assert.Equal(
                riferimento.Select(x => x.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
                nuovo.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray());
            // Stessa sequenza di rilevanza della base: stessi valori bm25, in ordine crescente.
            Assert.Equal(riferimento.Select(x => x.Bm25).OrderBy(x => x).ToArray(), nuovo.Select(k => bm25[k]).ToArray());
            // Nei pareggi di rilevanza l'ordine e' quello della navigazione (key_sort, uid, chiave).
            foreach (var gruppo in nuovo.GroupBy(k => bm25[k]))
            {
                var insieme = new HashSet<string>(gruppo, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(navigazione.Where(insieme.Contains).ToArray(), gruppo.ToArray());
            }
            // Paginazione: pagine da 4 concatenate danno lo stesso elenco, senza salti o duplicati sui pareggi.
            var pagine = new List<string>();
            for (long offset = 0; ; offset += 4)
            {
                var pagina = store.Query<Requisito>("requisiti", query, false, offset, 4);
                if (pagina.Count == 0) break;
                pagine.AddRange(Chiavi(pagina));
            }
            Assert.Equal(nuovo, pagine.ToArray());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IndiceV2_VieneEliminatoERicostruitoSenzaErrori(bool conSchemaVersion)
    {
        var nome = conSchemaVersion ? "con-versione" : "senza-versione";
        var shared = Path.Combine(_root, "shared-v2-" + nome);
        var local = Path.Combine(_root, "local-v2-" + nome);
        var db = new LocalMasterIndexStore(shared, local).DatabasePath;
        SqliteConnection.ClearAllPools();
        EliminaDatabase(db);
        // Indice v2 gia' riconciliato con la stessa fonte: senza il controllo di versione/layout il reconcile sarebbe saltato.
        CreaIndiceV2(db,
            new[] { ("requisiti", "R-1", false, "R-1 termine uno"), ("requisiti", "R-2", false, "R-2 termine due") },
            conSchemaVersion: conSchemaVersion,
            catalogiConSource: new[] { "requisiti" });

        var ricostruito = new LocalMasterIndexStore(shared, local);
        Assert.Equal(0, ricostruito.Count("requisiti", true));
        Assert.False(ricostruito.IsCatalogCurrent("requisiti", "source-A"));
        // Versione o layout non compatibili non sono corruzione: nessuna copia .corrupt-* (solo eliminazione).
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(db)!, "master-index.db.corrupt-*"));

        var risultato = Riconcilia(ricostruito, new List<Requisito>
        {
            Req("R-1", "termine uno", Uid(1)),
            Req("R-3", "termine tre", Uid(3)),
        }, "source-A");
        Assert.Equal(2, risultato.Added);
        Assert.False(risultato.SkippedAsCurrent);
        Assert.Equal(new[] { "R-1", "R-3" }, Chiavi(ricostruito.Query<Requisito>("requisiti", "termine", false, 0, 20)));
        Assert.Equal(new[] { "R-1" }, Chiavi(ricostruito.Query<Requisito>("requisiti", "uno", false, 0, 20)));
        Assert.Empty(ricostruito.Query<Requisito>("requisiti", "due", false, 0, 20));
        AsseriFtsAllineata(ricostruito);
        Assert.Equal("3", LeggiMeta(db, "schema_version"));
    }

    [Fact]
    public void RimozioneCatalogo_NonLasciaRigheFtsOrfane_EAltriCataloghiRestanoRicercabili()
    {
        var store = new LocalMasterIndexStore(Path.Combine(_root, "shared-rimozione"), Path.Combine(_root, "local-rimozione"));
        Riconcilia(store, new List<Requisito> { Req("R-1", "termine uno", Uid(1)), Req("R-2", "termine due", Uid(2)) }, "A");
        store.ReconcileCatalog("altro", new List<Requisito> { Req("ALTRO-1", "termine altro", Uid(3)) },
            r => r.Id, r => r.Archiviata, r => r.Testo, "A");

        store.RemoveCatalog("requisiti");
        Assert.Equal(0, store.Count("requisiti", true));
        Assert.Empty(store.Query<Requisito>("requisiti", "termine", true, 0, 20));
        Assert.Equal(new[] { "ALTRO-1" }, Chiavi(store.Query<Requisito>("altro", "termine", false, 0, 20)));
        AsseriFtsAllineata(store);

        // Riconciliazione dopo la rimozione: nessuna riga FTS residua puo' sopravvivere o collidere sui rid.
        Assert.Equal(1, Riconcilia(store, new List<Requisito> { Req("R-9", "nuovo testo", Uid(9)) }, "B").Added);
        Assert.Empty(store.Query<Requisito>("requisiti", "termine", true, 0, 20));
        Assert.Equal(new[] { "R-9" }, Chiavi(store.Query<Requisito>("requisiti", "nuovo", false, 0, 20)));
        Assert.Equal(new[] { "ALTRO-1" }, Chiavi(store.Query<Requisito>("altro", "termine", false, 0, 20)));
        AsseriFtsAllineata(store);
    }

    private static readonly DateTime FissaData = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Guid Uid(int n) => new($"{n:x8}-0000-0000-0000-000000000000");

    private static Requisito Req(string id, string testo, Guid uid, bool archiviata = false, string modificatoDa = "Seed")
    {
        var item = new Requisito
        {
            Id = id,
            Testo = testo,
            Archiviata = archiviata,
            ModificatoDa = modificatoDa,
            UltimaModifica = FissaData
        };
        item.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        item.ExtensionData[MasterEntityIdentityService.ExtensionKey] = JsonSerializer.SerializeToElement(uid.ToString("N"));
        return item;
    }

    private static LocalMasterReconcileResult Riconcilia(LocalMasterIndexStore store, IEnumerable<Requisito> items, string fingerprint) =>
        store.ReconcileCatalog("requisiti", items, r => r.Id, r => r.Archiviata, r => r.Testo, fingerprint);

    private static string[] Chiavi<T>(IEnumerable<LocalMasterIndexHit<T>> hits) where T : MasterEntity =>
        hits.Select(h => h.Key).ToArray();

    private static string NormalizzaTesto(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Stessa costruzione della query FTS di LocalMasterIndexStore (BuildFtsQuery).</summary>
    private static string BuildFts(string query) => string.Join(" AND ", query
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.Replace("\"", "\"\""))
        .Where(t => t.Length > 0)
        .Select(t => $"\"{t}\"*"));

    /// <summary>Query di ricerca della versione precedente (schema 2), copiata come riferimento: chiave e bm25.</summary>
    private static List<(string Key, double Bm25)> QueryBase(SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.item_key, bm25(master_search)
            FROM master_search
            JOIN master_item m ON m.catalog = master_search.catalog AND m.item_key = master_search.item_key
            WHERE master_search MATCH $fts AND m.catalog = $catalog AND m.archived = 0
            ORDER BY bm25(master_search), m.key_sort COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$fts", BuildFts(query));
        command.Parameters.AddWithValue("$catalog", "requisiti");
        var result = new List<(string Key, double Bm25)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetDouble(1)));
        return result;
    }

    /// <summary>Crea un indice nel layout dello schema 2 (prima della modifica) con le righe date: simula un indice
    /// locale gia' esistente sul PC. Le FTS usano le stesse colonne della versione precedente.</summary>
    private static void CreaIndiceV2(string db,
        IEnumerable<(string Catalogo, string Chiave, bool Archiviata, string Ricerca)> righe,
        bool conSchemaVersion = true,
        IEnumerable<string>? catalogiConSource = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();
        Esegui(connection, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE master_item(
                catalog TEXT NOT NULL,
                item_key TEXT COLLATE NOCASE NOT NULL,
                key_sort TEXT NOT NULL,
                archived INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                search_text TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY(catalog, item_key)
            );
            CREATE INDEX ix_master_catalog_archived_key ON master_item(catalog, archived, key_sort COLLATE NOCASE);
            CREATE VIRTUAL TABLE master_search USING fts5(
                catalog UNINDEXED,
                item_key UNINDEXED,
                search_text,
                tokenize='unicode61 remove_diacritics 2'
            );
            INSERT INTO meta(key, value) VALUES('fts_enabled', '1');
            """);
        if (conSchemaVersion)
            Esegui(connection, "INSERT INTO meta(key, value) VALUES('schema_version', '2');");
        foreach (var catalogo in catalogiConSource ?? Enumerable.Empty<string>())
            Esegui(connection, $"INSERT INTO meta(key, value) VALUES('catalog:{catalogo}:source', 'source-A');");
        foreach (var (catalogo, chiave, archiviata, ricerca) in righe)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO master_item(catalog,item_key,key_sort,archived,modified_ticks,content_hash,search_text,json)
                VALUES($catalogo,$chiave,$chiave,$archiviata,0,'v2',$ricerca,'{}');
                INSERT INTO master_search(catalog,item_key,search_text) VALUES($catalogo,$chiave,$ricerca);
                """;
            insert.Parameters.AddWithValue("$catalogo", catalogo);
            insert.Parameters.AddWithValue("$chiave", chiave);
            insert.Parameters.AddWithValue("$archiviata", archiviata ? 1 : 0);
            insert.Parameters.AddWithValue("$ricerca", ricerca);
            insert.ExecuteNonQuery();
        }
    }

    private static void Esegui(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? LeggiMeta(string db, string chiave)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", chiave);
        return command.ExecuteScalar() as string;
    }

    private static void EliminaDatabase(string db)
    {
        foreach (var file in new[] { db, db + "-wal", db + "-shm" })
            if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Invariante: FTS e tabella dati hanno le stesse righe e nessuna riga FTS orfana (rid non presente).</summary>
    private static void AsseriFtsAllineata(LocalMasterIndexStore store)
    {
        Assert.True(store.FtsEnabled);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath }.ToString());
        connection.Open();
        Assert.Equal(Scalare(connection, "SELECT COUNT(*) FROM master_item"), Scalare(connection, "SELECT COUNT(*) FROM master_search"));
        Assert.Equal(0L, Scalare(connection, "SELECT COUNT(*) FROM master_search WHERE rowid NOT IN (SELECT rid FROM master_item)"));
    }

    private static long Scalare(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}

[Collection("Scale performance")]
public sealed class LocalMasterIndexScaleTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LocalMasterIndexScale_" + Guid.NewGuid().ToString("N"));

    public LocalMasterIndexScaleTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, true); }
        catch { }
    }

    [Fact]
    [Trait("Category", "Scale")]
    public void Funzionalita100k_ReconcileRicercaEPagingRestanoOperativi()
    {
        const int count = 100_000;
        var store = new LocalMasterIndexStore(Path.Combine(_root, "shared"), Path.Combine(_root, "local"));
        var items = Enumerable.Range(0, count).Select(i => new Funzionalita
        {
            Nome = i == count - 1 ? "FUN-TARGET-099999" : $"FUN-{i:D6}",
            Categorie = new() { $"Categoria-{i % 100:D3}" },
            ApplicabilePerVeicolo = new() { $"Veicolo-{i % 500:D3}" },
            ModificatoDa = "Seed"
        }).ToList();

        var watch = Stopwatch.StartNew();
        var initial = store.ReconcileCatalog("funzionalita", items, f => f.Nome, f => f.Archiviata,
            f => $"{string.Join(' ', f.Categorie)} {string.Join(' ', f.ApplicabilePerVeicolo)}", "catalog-v1");
        var reconcile = watch.Elapsed;
        Assert.Equal(count, initial.Added);
        Assert.Equal(count, store.Count("funzionalita"));

        watch.Restart();
        var target = store.Query<Funzionalita>("funzionalita", "FUN-TARGET-099999", false, 0, 50);
        var search = watch.Elapsed;
        Assert.Equal("FUN-TARGET-099999", Assert.Single(target).Key);

        watch.Restart();
        var page = store.Query<Funzionalita>("funzionalita", null, false, 50_000, 100);
        var paging = watch.Elapsed;
        Assert.Equal(100, page.Count);

        items[50_000].Categorie = new() { "Categoria-Modificata" };
        items.RemoveAt(10);
        items.Add(new Funzionalita { Nome = "FUN-NEW", Categorie = new() { "Nuova" }, ModificatoDa = "PC-A" });
        watch.Restart();
        var delta = store.ReconcileCatalog("funzionalita", items, f => f.Nome, f => f.Archiviata,
            f => $"{string.Join(' ', f.Categorie)} {string.Join(' ', f.ApplicabilePerVeicolo)}", "catalog-v2");
        var incremental = watch.Elapsed;

        Assert.Equal(1, delta.Added);
        Assert.Equal(1, delta.Updated);
        Assert.Equal(1, delta.Removed);
        Assert.Equal(count, store.Count("funzionalita"));
        Assert.Equal("FUN-050000", Assert.Single(store.Query<Funzionalita>("funzionalita", "Categoria-Modificata", false, 0, 20)).Key);

        watch.Restart();
        var skipped = store.ReconcileCatalog("funzionalita", items, f => f.Nome, f => f.Archiviata,
            f => f.Nome, "catalog-v2");
        var noOp = watch.Elapsed;
        Assert.True(skipped.SkippedAsCurrent);

        var ramMiB = Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024.0;
        _output.WriteLine($"MASTER_INDEX|count={count}|reconcile_ms={reconcile.TotalMilliseconds:F0}|search_ms={search.TotalMilliseconds:F1}|page_ms={paging.TotalMilliseconds:F1}|incremental_ms={incremental.TotalMilliseconds:F0}|noop_ms={noOp.TotalMilliseconds:F1}|fts={store.FtsEnabled}|db_bytes={new FileInfo(store.DatabasePath).Length}|ram_mib={ramMiB:F1}");

        Assert.True(reconcile < TimeSpan.FromSeconds(90), $"Reconcile locale 100k troppo lento: {reconcile}");
        Assert.True(search < TimeSpan.FromSeconds(2), $"Ricerca locale 100k troppo lenta: {search}");
        Assert.True(paging < TimeSpan.FromSeconds(2), $"Paging locale 100k troppo lento: {paging}");
        Assert.True(incremental < TimeSpan.FromSeconds(30), $"Reconcile incrementale 100k troppo lento: {incremental}");
        Assert.True(noOp < TimeSpan.FromSeconds(2), $"No-op per fingerprint troppo lento: {noOp}");
    }
}
