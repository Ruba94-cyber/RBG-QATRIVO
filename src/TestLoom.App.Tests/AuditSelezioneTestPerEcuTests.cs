using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Oracoli del percorso Audit. Per ogni insieme di componente il percorso in memoria (snapshot, sola lettura) deve dare gli
/// stessi Test, con lo stesso contenuto e lo stesso ordine di QueryAll su SQLite, qualunque sia l'ordine di partenza
/// dello snapshot. Ordine e uguaglianza NOCASE sono confrontati con SQLite stesso, non con un'implementazione di riferimento.
/// </summary>
[Collection("ArchivioSopraTetto")]
public sealed class AuditSelezioneTestPerEcuTests
{
    private readonly ArchivioSopraTettoFixture _archivio;

    public AuditSelezioneTestPerEcuTests(ArchivioSopraTettoFixture archivio) => _archivio = archivio;

    [Theory]
    [InlineData("Componente-A")]            // tutto l'archivio: percorso in memoria
    [InlineData("Componente-B")]            // meta' dell'archivio
    [InlineData("componente-b")]            // maiuscole diverse: stesso abbinamento dell'indice
    [InlineData(" componente-RARA ")]       // selettivo, con spazi intorno
    [InlineData("Componente-INESISTENTE")]  // nessun Test abbinato
    public void DaSnapshot_StessiTestStessoContenutoEStessoOrdineDiQueryAll(string componente)
    {
        var service = new LocalTestArchiveQueryService(_archivio.DatabasePath);
        var richiesta = new[] { componente };
        var snapshot = CaricaSnapshot(_archivio.DatabasePath, new Random(7));   // ordine diverso da quello dell'indice

        var daIndice = service.QueryAll(new LocalTestArchiveQuery { AllowedEcus = richiesta });
        var inMemoria = AuditSelezioneTestPerEcu.DaSnapshot(snapshot, richiesta);

        Assert.Equal(daIndice.Select(Impronta), inMemoria.Select(Impronta));
    }

    [Fact]
    public void Leggi_SceglieIlPercorsoDallIndice_NonDallaDimensioneDelloSnapshot()
    {
        var service = new LocalTestArchiveQueryService(_archivio.DatabasePath);
        var snapshotParziale = CaricaSnapshot(_archivio.DatabasePath, new Random(11)).Take(10).ToList();

        // Non selettivo (tutto l'archivio): usa lo snapshot, quindi solo i suoi 10 Test.
        Assert.Equal(10, AuditSelezioneTestPerEcu.Leggi(service, snapshotParziale, new[] { "Componente-A" }).Count);
        // Selettivo (300 su 20.500): va all'indice e ignora lo snapshot parziale.
        Assert.Equal(ArchivioSopraTettoFixture.TestConEcuRara,
            AuditSelezioneTestPerEcu.Leggi(service, snapshotParziale, new[] { "Componente-RARA" }).Count);
    }

    [Fact]
    public void Confronto_OrdineEUguaglianzaNoCase_UgualiAQuelliDiSqlite()
    {
        var casuale = new Random(20261010);
        var testi = new List<string>();
        for (var i = 0; i < 600; i++) testi.Add(TestoCasuale(casuale));
        testi.AddRange(new[] { "tc-1", "TC-1", "Tc-10", "tc-2", "é", "É", "e", "f", "İ", "i", "ß", "😀", "ж", "Ж" });

        using var connessione = new SqliteConnection("Data Source=:memory:");
        connessione.Open();
        Esegui(connessione, "CREATE TABLE t(id TEXT NOT NULL, uid TEXT NOT NULL PRIMARY KEY);");
        for (var i = 0; i < testi.Count; i++)
            Esegui(connessione, "INSERT INTO t(id, uid) VALUES($id, $uid);", ("$id", testi[i]), ("$uid", $"{i:D6}"));

        var ordineSqlite = new List<string>();
        using (var comando = connessione.CreateCommand())
        {
            comando.CommandText = "SELECT uid FROM t ORDER BY id COLLATE NOCASE, uid;";
            using var lettore = comando.ExecuteReader();
            while (lettore.Read()) ordineSqlite.Add(lettore.GetString(0));
        }
        var ordineCSharp = testi
            .Select((testo, i) => (Testo: testo, Uid: $"{i:D6}"))
            .OrderBy(x => x.Testo, Comparer<string>.Create(SqliteTesto.ConfrontaNoCase))
            .ThenBy(x => x.Uid, StringComparer.Ordinal)
            .Select(x => x.Uid)
            .ToList();
        Assert.Equal(ordineSqlite, ordineCSharp);

        for (var i = 0; i < 300; i++)
        {
            var a = testi[casuale.Next(testi.Count)];
            var b = casuale.Next(2) == 0 ? testi[casuale.Next(testi.Count)] : MaiuscoleCasuali(a, casuale);
            Assert.Equal(SqliteUguali(connessione, a, b), SqliteTesto.ConfrontoNoCase.Equals(a, b));
        }
    }

    /// <summary>Snapshot come lo caricherebbe il repository (JSON letto e deserializzato, normalizzato, con stamp), in un
    /// ordine casuale diverso da quello dell'indice.</summary>
    private static List<TestCase> CaricaSnapshot(string databasePath, Random casuale)
    {
        var tutti = new List<TestCase>();
        using var connessione = new SqliteConnection($"Data Source={databasePath}");
        connessione.Open();
        using var comando = connessione.CreateCommand();
        comando.CommandText = """
            SELECT c.json, c.content_hash,
                   (SELECT value FROM meta WHERE key = 'content_hash_algo') AS marcatore_algoritmo
            FROM test_summary s
            JOIN test_case_cache c ON c.path = s.path
            WHERE s.eliminato = 0;
            """;
        using var lettore = comando.ExecuteReader();
        while (lettore.Read())
        {
            var tc = JsonSerializer.Deserialize<TestCase>(lettore.GetString(0), JsonIo.Options)!;
            tc.NormalizzaVeicoli();
            TestCaseFingerprint.StampDaIndice(tc, lettore.GetString(1), lettore.IsDBNull(2) ? null : lettore.GetString(2));
            tutti.Add(tc);
        }
        for (var i = tutti.Count - 1; i > 0; i--)
        {
            var j = casuale.Next(i + 1);
            (tutti[i], tutti[j]) = (tutti[j], tutti[i]);
        }
        return tutti;
    }

    private static string Impronta(TestCase tc) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tc, JsonIo.CompactOptions))));

    private static string TestoCasuale(Random casuale)
    {
        string[] alfabeto = { "a", "A", "b", "B", "z", "Z", "0", "9", "-", "_", " ", "é", "É", "ß", "ж", "Ж", "İ", "i", "I", "😀" };
        var lunghezza = casuale.Next(0, 9);
        var sb = new StringBuilder();
        for (var i = 0; i < lunghezza; i++) sb.Append(alfabeto[casuale.Next(alfabeto.Length)]);
        return sb.ToString();
    }

    private static string MaiuscoleCasuali(string testo, Random casuale) =>
        string.Concat(testo.Select(c => casuale.Next(2) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)));

    private static void Esegui(SqliteConnection connessione, string sql, params (string Nome, string Valore)[] parametri)
    {
        using var comando = connessione.CreateCommand();
        comando.CommandText = sql;
        foreach (var (nome, valore) in parametri) comando.Parameters.AddWithValue(nome, valore);
        comando.ExecuteNonQuery();
    }

    private static bool SqliteUguali(SqliteConnection connessione, string a, string b)
    {
        using var comando = connessione.CreateCommand();
        comando.CommandText = "SELECT $a = $b COLLATE NOCASE;";
        comando.Parameters.AddWithValue("$a", a);
        comando.Parameters.AddWithValue("$b", b);
        return Convert.ToInt64(comando.ExecuteScalar()) == 1;
    }
}
