using System.Diagnostics;
using System.Globalization;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>
/// Banco della query di ricerca per tasto del read-model master (LocalMasterIndexStore.Query, ramo FTS5 + bm25) su un
/// catalogo componente sintetico a scala N (default 50k). Gating: TestLoom_PERF_MASTER_QUERY=1; senza variabile il test non misura.
/// TestLoom_PERF_MASTER_SCALE=N per la scala, TestLoom_PERF_MASTER_LABEL per l'etichetta dei file in .ai/artifacts/perf.
/// Confronto (stessa base dati, costruite nello stesso processo):
///  - riferimento: indice schema 2 (master_search con catalog/item_key UNINDEXED, join per catalog+item_key), costruito
///    copiando le righe dello store nuovo; la query e' quella della versione precedente;
///  - store (Query attuale): LocalMasterIndexStore schema 3 (rid allineato, join per rid, spareggio entity_uid/item_key).
/// Per ogni bersaglio (1, N/4, N/2, 3N/4, N) il nome completo viene digitato carattere per carattere: ogni prefisso e' una query.
/// Le varianti sono misurate alternate nello stesso processo; ogni variante deve restituire le stesse chiavi, nello stesso
/// ordine, del riferimento. I costi di costruzione sono misurati a parte: seme da zero, riferimento schema 2, aggiornamento
/// da schema 2 (apertura con quarantena e ricreazione, poi riconciliazione completa).
/// </summary>
[Collection("Scale performance")]
[Trait("Category", "Benchmark")]
public sealed class PerfMasterRicercaQueryBenchmarkTests
{
    private const int Giri = 3;
    private const int FinestraPagina = 201; // come ReadPage(pageSize: 200): una riga in piu' per sapere se c'e' una pagina successiva.
    private static readonly DateTime DataBanco = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Query del read-model schema 2 (riferimento): copia del ramo FTS di LocalMasterIndexStore.Query prima della modifica.</summary>
    private const string SqlRiferimento = """
        SELECT m.item_key, m.archived, m.modified_ticks, m.json
        FROM master_search
        JOIN master_item m ON m.catalog = master_search.catalog AND m.item_key = master_search.item_key
        WHERE master_search MATCH $fts AND m.catalog = $catalog AND m.archived = 0
        ORDER BY bm25(master_search), m.key_sort COLLATE NOCASE
        LIMIT $limit OFFSET $offset;
        """;

    /// <summary>Query del read-model schema 3: copia del ramo FTS di LocalMasterIndexStore.Query (solo per EXPLAIN QUERY PLAN).</summary>
    private const string SqlNuova = """
        SELECT m.item_key, m.archived, m.modified_ticks, m.json
        FROM master_search
        JOIN master_item m ON m.rid = master_search.rowid
        WHERE master_search MATCH $fts AND m.catalog = $catalog AND m.archived = 0
        ORDER BY bm25(master_search), m.key_sort COLLATE NOCASE, m.entity_uid, m.item_key COLLATE NOCASE
        LIMIT $limit OFFSET $offset;
        """;

    private readonly ITestOutputHelper _output;
    public PerfMasterRicercaQueryBenchmarkTests(ITestOutputHelper output) => _output = output;

    private sealed record Campione(string Variante, int Bersaglio, int Lunghezza, int Giro, double Ms, int Righe, string Firma);

    [Fact]
    public void MisuraQueryRicercaPerTasto()
    {
        if (Environment.GetEnvironmentVariable("TestLoom_PERF_MASTER_QUERY") != "1")
        {
            _output.WriteLine("TestLoom_PERF_MASTER_QUERY non impostata a 1: benchmark escluso (non eseguito).");
            return;
        }
        var scala = int.TryParse(Environment.GetEnvironmentVariable("TestLoom_PERF_MASTER_SCALE"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var letta) && letta > 0 ? letta : 50_000;
        var etichetta = Environment.GetEnvironmentVariable("TestLoom_PERF_MASTER_LABEL");
        if (string.IsNullOrWhiteSpace(etichetta)) etichetta = "corrente";

        var radice = TrovaRadiceRepo()
            ?? throw new InvalidOperationException("Radice del repository non trovata (manca TestLoom.slnx).");
        var temporanea = Path.Combine(radice, ".ai", "tmp", "perf-query-" + Guid.NewGuid().ToString("N")[..8]);
        var campioni = new List<Campione>();
        var note = new List<string>();
        try
        {
            var dati = Path.Combine(temporanea, "dati");
            var locale = Path.Combine(temporanea, "locale");
            Directory.CreateDirectory(dati);
            Directory.CreateDirectory(locale);
            var fonte = "perf-query-" + scala.ToString(CultureInfo.InvariantCulture);

            var seme = Stopwatch.StartNew();
            var store = new LocalMasterIndexStore(dati, locale);
            store.ReconcileCatalog<Componente>("componente", Catalogo(scala), e => e.Nome, e => e.Archiviata, e => e.Nome, fonte);
            note.Add($"Costo di costruzione, indice schema 3 da zero ({scala:N0} componente, FTS e chiavi): {seme.Elapsed.TotalMilliseconds:F0} ms.");

            var riferimentoDb = Path.Combine(temporanea, "riferimento", "master-index.db");
            Directory.CreateDirectory(Path.GetDirectoryName(riferimentoDb)!);
            var costoRiferimento = Stopwatch.StartNew();
            CostruisciRiferimento(store.DatabasePath, riferimentoDb);
            note.Add($"Costo di costruzione del riferimento schema 2 (copia delle righe, FTS con catalog/item_key): {costoRiferimento.Elapsed.TotalMilliseconds:F0} ms.");

            // Aggiornamento da un indice schema 2 gia' presente sul PC: apertura (quarantena + ricreazione) e
            // riconciliazione completa dalla fonte autorevole. E' il costo reale pagato una volta per PC.
            var datiVecchio = Path.Combine(temporanea, "dati-v2");
            var localeVecchio = Path.Combine(temporanea, "locale-v2");
            Directory.CreateDirectory(datiVecchio);
            Directory.CreateDirectory(localeVecchio);
            var segnaposto = new LocalMasterIndexStore(datiVecchio, localeVecchio);
            SostituisciDatabase(segnaposto.DatabasePath, riferimentoDb);
            var apertura = Stopwatch.StartNew();
            var aggiornato = new LocalMasterIndexStore(datiVecchio, localeVecchio);
            var msApertura = apertura.Elapsed.TotalMilliseconds;
            var riconciliazione = Stopwatch.StartNew();
            var risultato = aggiornato.ReconcileCatalog<Componente>("componente", Catalogo(scala), e => e.Nome, e => e.Archiviata, e => e.Nome, fonte);
            var msRiconciliazione = riconciliazione.Elapsed.TotalMilliseconds;
            note.Add($"Aggiornamento da indice schema 2 ({scala:N0} righe): apertura con quarantena e ricreazione {msApertura:F0} ms; " +
                     $"riconciliazione completa {msRiconciliazione:F0} ms (aggiunte {risultato.Added:N0}).");

            foreach (var n in new[] { 1, scala / 4, scala / 2, 3 * scala / 4, scala })
            {
                var nome = $"Componente-BENCH-{n:D6}";
                foreach (var lunghezza in new[] { 1, 4, 8, nome.Length })
                {
                    var prefisso = nome[..lunghezza];
                    var attesi = Firma(store.Query<Componente>("componente", prefisso, false, 0, FinestraPagina).Select(h => h.Key));
                    var ottenuti = Firma(aggiornato.Query<Componente>("componente", prefisso, false, 0, FinestraPagina).Select(h => h.Key));
                    if (attesi != ottenuti)
                        throw new InvalidOperationException($"Indice aggiornato da schema 2 diverso dallo store nuovo per '{prefisso}'.");
                }
            }
            note.Add("Verifica: l'indice aggiornato da schema 2 restituisce le stesse chiavi, nello stesso ordine, dello store nuovo sui prefissi campionati.");

            using var connessioneNuova = Apri(store.DatabasePath);
            using var connessioneRiferimento = Apri(riferimentoDb);
            Esegui(connessioneNuova, "PRAGMA wal_checkpoint(TRUNCATE);");
            note.Add($"Dimensione file: schema 3 {DimensioneMiB(store.DatabasePath):F1} MiB; schema 2 {DimensioneMiB(riferimentoDb):F1} MiB (dopo checkpoint WAL).");

            foreach (var (nome, connessione, sql) in new[]
                     {
                         ("riferimento", connessioneRiferimento, SqlRiferimento),
                         ("store", connessioneNuova, SqlNuova),
                     })
            {
                using var piano = connessione.CreateCommand();
                piano.CommandText = "EXPLAIN QUERY PLAN " + sql;
                piano.Parameters.AddWithValue("$fts", BuildFtsQuery("E"));
                piano.Parameters.AddWithValue("$catalog", "componente");
                piano.Parameters.AddWithValue("$limit", FinestraPagina);
                piano.Parameters.AddWithValue("$offset", 0L);
                using var lettore = piano.ExecuteReader();
                while (lettore.Read()) note.Add($"Piano {nome}: {lettore.GetString(3)}");
            }

            var varianti = new (string Nome, Func<string, IReadOnlyList<string>> Esegui)[]
            {
                ("store (Query attuale)", prefisso => store.Query<Componente>("componente", prefisso, false, 0, FinestraPagina).Select(h => h.Key).ToList()),
                ("riferimento", prefisso => ChiaviSql(connessioneRiferimento, SqlRiferimento, BuildFtsQuery(prefisso))),
            };

            var bersagli = new[] { 1, scala / 4, scala / 2, 3 * scala / 4, scala };
            // Giro 0: riscaldamento, non registrato. Giri 1..Giri: misure registrate, ordine delle varianti ruotato per giro.
            for (var giro = 0; giro <= Giri; giro++)
            {
                foreach (var n in bersagli)
                {
                    var nome = $"Componente-BENCH-{n:D6}";
                    for (var lunghezza = 1; lunghezza <= nome.Length; lunghezza++)
                    {
                        var prefisso = nome[..lunghezza];
                        var firme = new string[varianti.Length];
                        var righe = new int[varianti.Length];
                        for (var k = 0; k < varianti.Length; k++)
                        {
                            var indice = (k + giro) % varianti.Length;
                            var sw = Stopwatch.StartNew();
                            var chiavi = varianti[indice].Esegui(prefisso);
                            sw.Stop();
                            firme[indice] = Firma(chiavi);
                            righe[indice] = chiavi.Count;
                            if (giro > 0)
                                campioni.Add(new Campione(varianti[indice].Nome, n, lunghezza, giro, sw.Elapsed.TotalMilliseconds,
                                    chiavi.Count, firme[indice]));
                        }
                        for (var k = 1; k < varianti.Length; k++)
                            if (firme[k] != firme[0])
                                throw new InvalidOperationException(
                                    $"Risultati diversi per '{prefisso}': {varianti[0].Nome}={righe[0]} righe, {varianti[k].Nome}={righe[k]} righe (firma o ordine).");
                    }
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(temporanea, recursive: true); } catch { }
        }

        ScriviRisultati(radice, etichetta, scala, campioni, note);
    }

    private static IEnumerable<Componente> Catalogo(int scala) =>
        Enumerable.Range(1, scala).Select(n => new Componente { Nome = $"Componente-BENCH-{n:D6}", UltimaModifica = DataBanco });

    private static SqliteConnection Apri(string db)
    {
        var connessione = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());
        connessione.Open();
        return connessione;
    }

    /// <summary>Riferimento schema 2: le stesse righe dello store nuovo, con master_search a colonne UNINDEXED.</summary>
    private static void CostruisciRiferimento(string sorgente, string destinazione)
    {
        using var connessione = Apri(destinazione);
        Esegui(connessione, $"ATTACH DATABASE '{sorgente.Replace("'", "''")}' AS nuovo;");
        Esegui(connessione, """
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta(key, value) VALUES('schema_version', '2'), ('fts_enabled', '1');
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
            INSERT INTO master_item(catalog,item_key,key_sort,archived,modified_ticks,content_hash,search_text,json)
                SELECT catalog,item_key,key_sort,archived,modified_ticks,content_hash,search_text,json FROM nuovo.master_item;
            INSERT INTO master_search(catalog,item_key,search_text) SELECT catalog,item_key,search_text FROM master_item;
            DETACH DATABASE nuovo;
            """);
    }

    /// <summary>Sostituisce l'indice con una copia del riferimento schema 2, senza file di WAL residui.</summary>
    private static void SostituisciDatabase(string destinazione, string sorgente)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { destinazione + "-wal", destinazione + "-shm" })
            if (File.Exists(file)) File.Delete(file);
        File.Copy(sorgente, destinazione, overwrite: true);
    }

    private static double DimensioneMiB(string path) => new FileInfo(path).Length / 1024.0 / 1024.0;

    private static void Esegui(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ChiaviSql(SqliteConnection c, string sql, string fts)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$fts", fts);
        cmd.Parameters.AddWithValue("$catalog", "componente");
        cmd.Parameters.AddWithValue("$limit", FinestraPagina);
        cmd.Parameters.AddWithValue("$offset", 0L);
        var chiavi = new List<string>(FinestraPagina);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) chiavi.Add(reader.GetString(0));
        return chiavi;
    }

    /// <summary>Stessa costruzione della query di LocalMasterIndexStore (BuildFtsQuery).</summary>
    private static string BuildFtsQuery(string query)
    {
        var termini = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Replace("\"", "\"\""))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t}\"*");
        return string.Join(" AND ", termini);
    }

    /// <summary>FNV-1a sulle chiavi nell'ordine restituito: stesse righe e stesso ordine danno la stessa firma.</summary>
    private static string Firma(IEnumerable<string> chiavi)
    {
        ulong h = 14695981039346656037UL;
        foreach (var chiave in chiavi)
        {
            foreach (var c in chiave) h = (h ^ c) * 1099511628211UL;
            h = (h ^ 0x0A) * 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static double Percentile(IEnumerable<double> valori, double p)
    {
        var ordinati = valori.OrderBy(x => x).ToArray();
        var rango = (int)Math.Ceiling(p / 100.0 * ordinati.Length);
        return ordinati[Math.Clamp(rango - 1, 0, ordinati.Length - 1)];
    }

    private void ScriviRisultati(string radice, string etichetta, int scala, List<Campione> campioni, List<string> note)
    {
        var cartella = Path.Combine(radice, ".ai", "artifacts", "perf");
        Directory.CreateDirectory(cartella);
        var radiceFile = Path.Combine(cartella, $"master-query-{etichetta}-{scala / 1000}k");

        var tsv = new StringBuilder();
        tsv.AppendLine("variante\tbersaglio\tlunghezza\tgiro\tms\trighe\tfirma");
        foreach (var c in campioni)
            tsv.AppendLine(string.Join('\t', c.Variante, c.Bersaglio, c.Lunghezza, c.Giro,
                c.Ms.ToString("F3", CultureInfo.InvariantCulture), c.Righe, c.Firma));
        File.WriteAllText(radiceFile + ".tsv", tsv.ToString());

        var md = new StringBuilder();
        md.AppendLine($"# Query di ricerca master: {etichetta}, {scala} componente, {Giri} giri intercalati");
        md.AppendLine();
        md.AppendLine("| Variante | Campioni | p50 ms | p95 ms | max ms | somma ms | p50 rispetto al riferimento |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        var riferimento = campioni.Where(c => c.Variante == "riferimento").Select(c => c.Ms).ToList();
        var mediaRiferimento = riferimento.Count == 0 ? double.NaN : Percentile(riferimento, 50);
        foreach (var gruppo in campioni.GroupBy(c => c.Variante))
        {
            var ms = gruppo.Select(c => c.Ms).ToList();
            var p50 = Percentile(ms, 50);
            var rapporto = (p50 / mediaRiferimento - 1) * 100;
            md.AppendLine($"| {gruppo.Key} | {ms.Count} | {p50:F1} | {Percentile(ms, 95):F1} | {ms.Max():F1} | {ms.Sum():F0} | {rapporto:+0.0;-0.0} % |");
        }
        md.AppendLine();
        foreach (var riga in note) md.AppendLine("- " + riga);
        File.WriteAllText(radiceFile + ".md", md.ToString());

        foreach (var gruppo in campioni.GroupBy(c => c.Variante))
        {
            var ms = gruppo.Select(c => c.Ms).ToList();
            _output.WriteLine($"{gruppo.Key}: n={ms.Count} p50={Percentile(ms, 50):F1} ms p95={Percentile(ms, 95):F1} ms somma={ms.Sum():F0} ms");
        }
        foreach (var riga in note) _output.WriteLine(riga);
    }

    private static string? TrovaRadiceRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "TestLoom.slnx"))) return dir.FullName;
        return null;
    }
}
