using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Indice locale idx_phase_d_head_dup_nocase per la verifica di chiavi business duplicate senza distinzione di maiuscole
/// (EnsureCatalogQueryable). Garanzie provate: (1) un duplicato che differisce solo per maiuscole/minuscole continua a
/// lanciare la stessa eccezione; (2) il risultato della query e' identico con e senza indice; (3) con l'indice il piano non
/// usa piu' l'ordinamento temporaneo; (4) un DB locale preesistente senza indice si apre, riceve l'indice e non viene
/// ricostruito. Le teste sono inserite direttamente nel read-model locale, l'unica tabella letta dalla verifica: nessun
/// journal o sorgente condivisa viene toccata.
/// </summary>
public sealed class PhaseDMasterIndiceDuplicatiNocaseTests : IDisposable
{
    private const string Catalogo = "componente";
    private static readonly string SqlSenzaLimite = PhaseDMasterScalableReader.SqlDuplicatiAttivi.Replace("LIMIT 1;", ";");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_IdxDup_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_IdxDupLocal_" + Guid.NewGuid().ToString("N"));

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PhaseDMasterIndiceDuplicatiNocaseTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    /// <summary>Percorso del read-model locale. Costruire il materializzatore inizializza lo schema (e l'indice).</summary>
    private string DatabasePath() => new PhaseDMasterMaterializer(_root, _local).DatabasePath;

    private SqliteConnection Apri()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath()}");
        connection.Open();
        return connection;
    }

    private static void Inserisci(SqliteConnection connection, string chiave,
        PhaseDMasterMutationOperation operazione = PhaseDMasterMutationOperation.Upsert, bool inConflitto = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO phase_d_head(catalog,entity_uid,operation,business_key,payload_json,payload_sha256,vector_json,conflict,concurrent_heads_json,head_fingerprint)
            VALUES($catalog,$uid,$operazione,$chiave,'{}','','{}',$conflitto,'[]','');
            """;
        command.Parameters.AddWithValue("$catalog", Catalogo);
        command.Parameters.AddWithValue("$uid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$operazione", (int)operazione);
        command.Parameters.AddWithValue("$chiave", chiave);
        command.Parameters.AddWithValue("$conflitto", inConflitto ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>Righe della query di verifica (chiave in minuscolo, MIN e MAX dei UID), nell'ordine restituito da SQLite.</summary>
    private static List<string> Esegui(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$catalog", Catalogo);
        command.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        var righe = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            righe.Add($"{reader.GetString(0).ToLowerInvariant()}|{reader.GetString(1)}|{reader.GetString(2)}");
        return righe;
    }

    private static string Piano(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$catalog", Catalogo);
        if (sql.Contains("$upsert"))
            command.Parameters.AddWithValue("$upsert", (int)PhaseDMasterMutationOperation.Upsert);
        var righe = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) righe.Add(reader.GetString(reader.GetOrdinal("detail")));
        return string.Join(Environment.NewLine, righe);
    }

    private static bool IndiceEsiste(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=$nome;";
        command.Parameters.AddWithValue("$nome", PhaseDMasterMaterializer.IndiceDuplicatiNocase);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    [Fact]
    public void DuplicatoSoloPerMaiuscole_LanciaLaStessaEccezioneDiPrima()
    {
        using (var connection = Apri())
        {
            Inserisci(connection, "Componente-A");
            Inserisci(connection, "componente-a");
        }

        var reader = new PhaseDMasterScalableReader(_root, _local);
        Assert.Throws<PhaseDMasterBusinessKeyConflictException>(() => reader.GetCatalogState(Catalogo));
    }

    [Fact]
    public void DuplicatoStessaChiave_LanciaLaStessaEccezione()
    {
        using (var connection = Apri())
        {
            Inserisci(connection, "Componente-B");
            Inserisci(connection, "Componente-B");
        }

        var reader = new PhaseDMasterScalableReader(_root, _local);
        Assert.Throws<PhaseDMasterBusinessKeyConflictException>(() => reader.GetCatalogState(Catalogo));
    }

    [Fact]
    public void VariantiNonAttive_NonContanoComeDuplicati()
    {
        using (var connection = Apri())
        {
            Inserisci(connection, "Componente-C");
            Inserisci(connection, "componente-c", inConflitto: true);                              // in conflitto: esclusa
            Inserisci(connection, "Componente-D");
            Inserisci(connection, "componente-d", PhaseDMasterMutationOperation.Delete);           // cancellata: esclusa
            Inserisci(connection, "");                                                      // chiave vuota: esclusa
            Inserisci(connection, "");
        }

        var reader = new PhaseDMasterScalableReader(_root, _local);
        var stato = reader.GetCatalogState(Catalogo);
        // Il conteggio include tutte le upsert, conflitti compresi (componente-C, componente-c, componente-D, due chiavi vuote); la cancellazione no.
        Assert.Equal(5, stato.ActiveCount);
    }

    [Fact]
    public void RisultatoConEsenzaIndice_UgualeEPianoSenzaOrdinamentoTemporaneo()
    {
        using (var connection = Apri())
        {
            for (var i = 0; i < 400; i++)
            {
                var chiaveBase = $"Componente-{i % 120:D4}";
                var variante = (i % 3) switch
                {
                    0 => chiaveBase.ToLowerInvariant(),
                    1 => chiaveBase.ToUpperInvariant(),
                    _ => chiaveBase,
                };
                Inserisci(connection, variante,
                    operazione: i % 23 == 0 ? PhaseDMasterMutationOperation.Delete : PhaseDMasterMutationOperation.Upsert,
                    inConflitto: i % 17 == 0);
            }
            // Confronto ASCII: sotto NOCASE SQLite non ripiega lettere accentate, quindi queste due NON sono duplicate.
            Inserisci(connection, "città");
            Inserisci(connection, "CITTÀ");
        }

        List<string> conIndice, tuttiConIndice;
        using (var connection = Apri())
        {
            Assert.True(IndiceEsiste(connection));
            var piano = Piano(connection, PhaseDMasterScalableReader.SqlDuplicatiAttivi);
            _output.WriteLine("PIANO CON INDICE:" + Environment.NewLine + piano);
            Assert.Contains(PhaseDMasterMaterializer.IndiceDuplicatiNocase, piano);
            Assert.DoesNotContain("TEMP B-TREE", piano);
            conIndice = Esegui(connection, PhaseDMasterScalableReader.SqlDuplicatiAttivi);
            tuttiConIndice = Esegui(connection, SqlSenzaLimite);
        }

        List<string> senzaIndice, tuttiSenzaIndice;
        using (var connection = Apri())
        {
            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = $"DROP INDEX IF EXISTS {PhaseDMasterMaterializer.IndiceDuplicatiNocase};";
                drop.ExecuteNonQuery();
            }
            var pianoSenza = Piano(connection, PhaseDMasterScalableReader.SqlDuplicatiAttivi);
            _output.WriteLine("PIANO SENZA INDICE:" + Environment.NewLine + pianoSenza);
            Assert.Contains("TEMP B-TREE", pianoSenza);
            senzaIndice = Esegui(connection, PhaseDMasterScalableReader.SqlDuplicatiAttivi);
            tuttiSenzaIndice = Esegui(connection, SqlSenzaLimite);
        }

        Assert.NotEmpty(tuttiConIndice); // il dataset contiene davvero duplicati (per maiuscole/minuscole e stessa chiave)
        Assert.Equal(senzaIndice, conIndice);
        Assert.Equal(tuttiSenzaIndice, tuttiConIndice);
    }

    [Fact]
    public void DbLocalePreesistenteSenzaIndice_SiApreRiceveIndiceSenzaRicostruzione()
    {
        var percorsoDb = DatabasePath();
        using (var connection = new SqliteConnection($"Data Source={percorsoDb}"))
        {
            connection.Open();
            Inserisci(connection, "Componente-E");
            Inserisci(connection, "Componente-F");
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP INDEX IF EXISTS {PhaseDMasterMaterializer.IndiceDuplicatiNocase};";
            drop.ExecuteNonQuery();
        }

        // Il lettore costruisce un materializzatore che inizializza lo schema: l'indice manca e va creato, senza quarantena.
        var reader = new PhaseDMasterScalableReader(_root, _local);
        var stato = reader.GetCatalogState(Catalogo);

        Assert.Equal(2, stato.ActiveCount);
        using (var connection = Apri())
            Assert.True(IndiceEsiste(connection));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(percorsoDb)!, "*.corrupt-*"));
    }

    /// <summary>Le letture delle teste di un catalogo (LoadCatalog) non devono cambiare piano: con l'indice nuovo resta
    /// la chiave primaria (catalog, entity_uid) a fornire l'ordine, senza ordinamento temporaneo, e i risultati coincidono.</summary>
    [Fact]
    public void LetturaTesteDelCatalogo_PianoEdOrdineInvariatiConIndice()
    {
        using (var connection = Apri())
        {
            for (var i = 0; i < 300; i++)
                Inserisci(connection, $"Componente-{i:D4}", inConflitto: i % 11 == 0);
        }

        List<string> conIndice;
        using (var connection = Apri())
        {
            var piano = Piano(connection, PhaseDMasterMaterializer.SqlTesteCatalogo);
            _output.WriteLine("PIANO TESTE CON INDICE:" + Environment.NewLine + piano);
            Assert.DoesNotContain("TEMP B-TREE", piano);
            conIndice = EseguiTeste(connection);
        }

        List<string> senzaIndice;
        using (var connection = Apri())
        {
            using (var drop = connection.CreateCommand())
            {
                drop.CommandText = $"DROP INDEX IF EXISTS {PhaseDMasterMaterializer.IndiceDuplicatiNocase};";
                drop.ExecuteNonQuery();
            }
            _output.WriteLine("PIANO TESTE SENZA INDICE:" + Environment.NewLine + Piano(connection, PhaseDMasterMaterializer.SqlTesteCatalogo));
            senzaIndice = EseguiTeste(connection);
        }

        Assert.Equal(300, conIndice.Count);
        Assert.Equal(senzaIndice, conIndice);
        Assert.Equal(conIndice.OrderBy(uid => uid, StringComparer.Ordinal).ToList(), conIndice);
    }

    /// <summary>UID delle teste del catalogo nell'ordine restituito da SQLite.</summary>
    private static List<string> EseguiTeste(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PhaseDMasterMaterializer.SqlTesteCatalogo;
        command.Parameters.AddWithValue("$catalog", Catalogo);
        var uid = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) uid.Add(reader.GetString(0));
        return uid;
    }
}
