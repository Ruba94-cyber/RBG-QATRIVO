using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1): indice locale schema 15 = colonna duration_minutes su test_operation_attempt. Proiettata a ogni
/// scrittura (upsert, correzione, eliminazione) e riempita dal JSON gia' in test_case_cache quando si apre un indice 14
/// (o 11..13). La migrazione e' transazionale: se fallisce rolla back (schema 14, colonna assente) e si ripete al successivo avvio.
/// Il fallimento e' simulato con un trigger SQLite: nessun dato reale viene toccato.</summary>
public sealed class LocalTestIndexDurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_IdxDurata_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private LocalTestIndexStore Apri() => new(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

    private string PathTest(string id) => Path.Combine(_root, "data", id + ".json");

    /// <summary>Test con una revisione e tre operazioni con durate date (null = non nota, 0 = zero noto).</summary>
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

    private static List<int?> DurateNelDb(LocalTestIndexStore store, Guid uid)
    {
        using var connection = new SqliteConnection($"Data Source={store.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT duration_minutes FROM test_operation_attempt WHERE test_uid = $uid ORDER BY revision_index, attempt_index;";
        command.Parameters.AddWithValue("$uid", uid.ToString("N"));
        using var reader = command.ExecuteReader();
        var result = new List<int?>();
        while (reader.Read()) result.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
        return result;
    }

    private static string VersioneSchema(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return (string)command.ExecuteScalar()!;
    }

    private static bool ColonnaPresente(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(test_operation_attempt);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), "duration_minutes", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Riporta l'indice al formato di una versione precedente: colonna della durata rimossa, versione scritta.</summary>
    private static void SimulaSchema(string databasePath, string versione)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "ALTER TABLE test_operation_attempt DROP COLUMN duration_minutes;";
            drop.ExecuteNonQuery();
        }
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "UPDATE meta SET value = $versione WHERE key = 'schema_version';";
            meta.Parameters.AddWithValue("$versione", versione);
            meta.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private static void EseguiSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void IndiceNuovo_EsponeLaVersione15ELeDurateSenzaPerdereNullOZero()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));

        Assert.Equal("16", VersioneSchema(store.DatabasePath));
        Assert.True(ColonnaPresente(store.DatabasePath));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(store, uid));
    }

    [Fact]
    public void Migrazione14A15_RiempieLeDurateDalJsonInCache()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));
        var db = store.DatabasePath;
        SimulaSchema(db, "14");
        Assert.False(ColonnaPresente(db));

        var riaperto = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.True(ColonnaPresente(db));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(riaperto, uid));
    }

    [Fact]
    public void Migrazione14A15_FallimentoRollbackENuovoTentativoAlSuccessivoAvvio()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));
        var db = store.DatabasePath;
        SimulaSchema(db, "14");
        EseguiSql(db, "CREATE TRIGGER fallisci_durata BEFORE UPDATE ON test_operation_attempt BEGIN SELECT RAISE(ABORT, 'simulato'); END;");

        // Il backfill fallisce dentro la transazione: ALTER e UPDATE rollback, la versione non viene mai scritta.
        Assert.ThrowsAny<SqliteException>(() => Apri());
        Assert.Equal("14", VersioneSchema(db));
        Assert.False(ColonnaPresente(db));

        EseguiSql(db, "DROP TRIGGER fallisci_durata;");
        var riprovato = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(riprovato, uid));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    public void MigrazioneDaV2A4_PassaDalPassoV5EArrivaAllaVersione15SenzaPerdereLeDurate(string versione)
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));
        var db = store.DatabasePath;
        SimulaSchema(db, versione);

        var riaperto = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.True(ColonnaPresente(db));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(riaperto, uid));
    }

    [Fact]
    public void MigrazioneRiprendeDallaVersioneIntermediaV5_SenzaPerdereLeDurate()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));
        var db = store.DatabasePath;
        SimulaSchema(db, "5");      // interruzione dopo il passo v5 e prima dei passi successivi

        var riaperto = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.True(ColonnaPresente(db));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(riaperto, uid));
    }

    [Fact]
    public void MigrazioneFallitaAlPassoFinale_NonPubblicaLa15ERiprovaAlRiavvio()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 20, 0, null));
        var db = store.DatabasePath;
        SimulaSchema(db, "4");
        EseguiSql(db, "CREATE TRIGGER fallisci_versione_finale BEFORE UPDATE ON meta WHEN NEW.key = 'schema_version' AND NEW.value = '16' BEGIN SELECT RAISE(ABORT, 'simulato'); END;");

        // Il passo finale (v8..v15) fallisce: la sua transazione rolla back, resta la versione intermedia 5.
        Assert.ThrowsAny<SqliteException>(() => Apri());
        Assert.Equal("5", VersioneSchema(db));
        Assert.False(ColonnaPresente(db));

        EseguiSql(db, "DROP TRIGGER fallisci_versione_finale;");
        var riprovato = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.True(ColonnaPresente(db));
        Assert.Equal(new int?[] { 20, 0, null }, DurateNelDb(riprovato, uid));
    }

    [Fact]
    public void MigrazioneFallitaAlPassoV5_LaVersioneRestaQuellaPrecedente()
    {
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", Guid.NewGuid(), 20));
        var db = store.DatabasePath;
        SimulaSchema(db, "4");
        EseguiSql(db, "CREATE TRIGGER fallisci_versione_v5 BEFORE UPDATE ON meta WHEN NEW.key = 'schema_version' AND NEW.value = '5' BEGIN SELECT RAISE(ABORT, 'simulato'); END;");

        Assert.ThrowsAny<SqliteException>(() => Apri());
        Assert.Equal("4", VersioneSchema(db));

        EseguiSql(db, "DROP TRIGGER fallisci_versione_v5;");
        Apri();
        Assert.Equal("16", VersioneSchema(db));
    }

    [Fact]
    public void Migrazione13A15_PercorsoGenericoAggiungeLaColonnaEReimpostaLeDurate()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 45, null, 0));
        var db = store.DatabasePath;
        SimulaSchema(db, "13");

        var riaperto = Apri();

        Assert.Equal("16", VersioneSchema(db));
        Assert.Equal(new int?[] { 45, null, 0 }, DurateNelDb(riaperto, uid));
    }

    [Fact]
    public void RiaperturaDiUnIndiceV15_NonCambiaLeDurate()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        store.Upsert(PathTest("TC-A"), Day, 100, TestConDurate("TC-A", uid, 7, null));

        var primo = Apri();
        var secondo = Apri();

        Assert.Equal("16", VersioneSchema(secondo.DatabasePath));
        Assert.Equal(new int?[] { 7, null }, DurateNelDb(primo, uid));
        Assert.Equal(new int?[] { 7, null }, DurateNelDb(secondo, uid));
    }

    [Fact]
    public void AggiornamentoEEliminazione_RiproiettanoLeDurateAtomicamente()
    {
        var uid = Guid.NewGuid();
        var store = Apri();
        var path = PathTest("TC-A");
        store.Upsert(path, Day, 100, TestConDurate("TC-A", uid, 20, 0, null));

        // Correzione: l'operatore cambia le durate; la proiezione segue la stessa transazione dell'upsert.
        store.Upsert(path, Day, 120, TestConDurate("TC-A", uid, 5, null, 30));
        Assert.Equal(new int?[] { 5, null, 30 }, DurateNelDb(store, uid));

        store.Remove(path);
        Assert.Empty(DurateNelDb(store, uid));
    }
}
