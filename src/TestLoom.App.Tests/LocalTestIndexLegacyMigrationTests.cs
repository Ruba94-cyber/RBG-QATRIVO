using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): apertura dell'indice locale da forme reali v2..v6 e dalla versione intermedia 5 interrotta. Il DDL di
/// ogni forma e' quello del commit che la scriveva (Fixtures/indice-legacy-vN.sql, estratto da git); le righe di prova sono quelle
/// della proiezione corrente, copiate solo nelle colonne che quella forma possiede. Prima della correzione l'indice
/// ix_test_summary_origine (colonna v7) veniva creato prima della migrazione e l'apertura falliva su queste forme.</summary>
public sealed class LocalTestIndexLegacyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_IdxLegacy_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private LocalTestIndexStore Apri() => new(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

    private string PathTest(string id) => Path.Combine(_root, "data", id + ".json");

    private static TestCase TestConDurate(string id, Guid uid, params int?[] minuti)
    {
        var condizioni = minuti.Select((m, i) => new Condizione
        {
            Funzionalita = "F" + i,
            Componente = new EcuSelection(new[] { "Y" }),
            Esito = Esito.Conforme,
            DurataMinuti = m,
        }).ToList();
        var tc = new TestCase
        {
            Id = id, Uid = uid, DataCreazione = Day, UltimaModifica = Day, CreatoDa = "Plan", ModificatoDa = "Plan",
            Componente = new EcuSelection(new[] { "Y" }), Funzionalita = ["F0", "F1", "F2"], Veicoli = ["Daily"], Veicolo = "Daily",
        };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Day, Operatore = "Op", Esito = Esito.Conforme, Condizioni = condizioni });
        return tc;
    }

    private static string Fixture(string forma) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", $"indice-legacy-v{forma}.sql");

    /// <summary>Riscrive l'indice corrente nella forma indicata (DDL storico di <paramref name="forma"/>): stesse righe, versione
    /// <paramref name="versione"/> scritta per ultima.</summary>
    private string CreaIndiceLegacy(string versione, string forma, Guid uid)
    {
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));
        var db = store.DatabasePath;
        SqliteConnection.ClearAllPools();
        var sorgente = db + ".corrente";
        File.Copy(db, sorgente, true);
        EliminaDatabase(db);

        using (var legacy = new SqliteConnection($"Data Source={db}"))
        {
            legacy.Open();
            EseguiScript(legacy, File.ReadAllText(Fixture(forma)));
            EseguiScript(legacy, $"ATTACH DATABASE '{sorgente.Replace("'", "''")}' AS src; PRAGMA foreign_keys = OFF;");
            foreach (var tabella in TabelleDi(legacy, "main"))
            {
                var comuni = ColonneComuni(legacy, tabella);
                if (comuni.Count == 0) continue;
                var elenco = string.Join(", ", comuni);
                EseguiScript(legacy, $"INSERT INTO main.{tabella} ({elenco}) SELECT {elenco} FROM src.{tabella};");
            }
            EseguiScript(legacy,
                $"UPDATE main.meta SET value = '{versione}' WHERE key = 'schema_version'; DETACH DATABASE src;");
        }
        SqliteConnection.ClearAllPools();
        return db;
    }

    private static void EseguiScript(SqliteConnection connection, string script)
    {
        using var command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    private static void EliminaDatabase(string db)
    {
        foreach (var file in new[] { db, db + "-wal", db + "-shm", db + "-journal" })
            if (File.Exists(file)) File.Delete(file);
    }

    private static List<string> TabelleDi(SqliteConnection connection, string schema)
    {
        var tabelle = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM {schema}.sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND sql NOT LIKE '%VIRTUAL%';";
        using var reader = command.ExecuteReader();
        while (reader.Read()) tabelle.Add(reader.GetString(0));
        return tabelle;
    }

    private static List<string> ColonneDi(SqliteConnection connection, string schema, string tabella)
    {
        var colonne = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {schema}.table_info({tabella});";
        using var reader = command.ExecuteReader();
        while (reader.Read()) colonne.Add(reader.GetString(1));
        return colonne;
    }

    /// <summary>Colonne presenti sia nella forma legacy sia nella copia corrente, nell'ordine della forma legacy.</summary>
    private static List<string> ColonneComuni(SqliteConnection connection, string tabella)
    {
        var sorgente = new HashSet<string>(ColonneDi(connection, "src", tabella), StringComparer.OrdinalIgnoreCase);
        return ColonneDi(connection, "main", tabella).Where(sorgente.Contains).ToList();
    }

    private static string VersioneSchema(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return (string)command.ExecuteScalar()!;
    }

    private static bool ColonnaPresente(string databasePath, string tabella, string colonna)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        return ColonneDi(connection, "main", tabella).Any(c => string.Equals(c, colonna, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IndicePresente(string databasePath, string nome)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $nome);";
        command.Parameters.AddWithValue("$nome", nome);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static List<int?> DurateNelDb(LocalTestIndexStore store, Guid uid)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT duration_minutes FROM test_operation_attempt WHERE test_uid = $uid ORDER BY revision_index, attempt_index;";
        command.Parameters.AddWithValue("$uid", uid.ToString("N"));
        using var reader = command.ExecuteReader();
        var risultato = new List<int?>();
        while (reader.Read()) risultato.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        return risultato;
    }

    [Theory]
    [InlineData("2", "2")]
    [InlineData("3", "3")]
    [InlineData("4", "3")]   // v4: nella storia non esiste un DDL proprio (nessun commit ha scritto CurrentSchemaVersion = 4); forma v3, versione 4
    [InlineData("5", "5")]   // versione intermedia: interruzione dopo il passo v5 e prima della v7
    [InlineData("6", "6")]
    public void IndiceLegacy_SiApreSenzaEccezioniEArrivaAllaVersione16ConLeDurateELeFonti(string versione, string forma)
    {
        var uid = Guid.NewGuid();
        var db = CreaIndiceLegacy(versione, forma, uid);

        // Forma reale: la colonna origine_test arriva con la v7, quindi l'indice che la usa non puo' esistere prima della migrazione.
        Assert.Equal(versione, VersioneSchema(db));
        Assert.False(ColonnaPresente(db, "test_summary", "origine_test"));
        using (var sonda = new SqliteConnection($"Data Source={db}"))
        {
            sonda.Open();
            // Il vecchio ordine (indice creato prima della migrazione) falliva esattamente qui.
            Assert.ThrowsAny<SqliteException>(() => EseguiScript(sonda, "CREATE INDEX ix_sonda_origine ON test_summary(origine_test);"));
        }

        var riaperto = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.True(ColonnaPresente(db, "test_operation_attempt", "duration_minutes"));
        Assert.True(ColonnaPresente(db, "test_summary", "origine_test"));
        Assert.True(IndicePresente(db, "ix_test_summary_origine"));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(riaperto, uid));

        // v16: le fonti della Home ricostruite dalla migrazione coincidono con l'oracolo sulle proiezioni legacy.
        using var connessione = new SqliteConnection($"Data Source={db}");
        connessione.Open();
        var oracolo = OracoloSorgentiHome.Oracolo(connessione);
        Assert.NotEmpty(oracolo);
        Assert.Equal(oracolo, OracoloSorgentiHome.Persistito(connessione));
        Assert.Equal(oracolo, OracoloSorgentiHome.Servizio(db));
    }

    [Fact]
    public void IndiceLegacy_RiapertoDueVolte_NonCambiaLeDurateNeLaVersione()
    {
        var uid = Guid.NewGuid();
        var db = CreaIndiceLegacy("3", "3", uid);

        var primo = Apri();
        var secondo = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(primo, uid));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(secondo, uid));
    }
}
