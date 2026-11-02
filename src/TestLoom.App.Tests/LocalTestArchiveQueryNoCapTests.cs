using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Fixture condivisa: un archivio con piu' di 20.000 Test attivi, tutti nel componente-A, meta' anche nel componente-B.
/// Il seeding avviene una sola volta per classe (ApplyBatch in un'unica transazione).
/// </summary>
[CollectionDefinition("ArchivioSopraTetto")]
public sealed class ArchivioSopraTettoCollection : ICollectionFixture<ArchivioSopraTettoFixture>
{
}

public sealed class ArchivioSopraTettoFixture : IDisposable
{
    public const int Quanti = 20_500;

    /// <summary>Primi Test con il componente-RARA in piu': filtro selettivo (circa l'1,5% dell'archivio).</summary>
    public const int TestConEcuRara = 300;

    public ArchivioSopraTettoFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "TestLoom_NoCap_" + Guid.NewGuid().ToString("N"));
        Local = Path.Combine(Path.GetTempPath(), "TestLoom_NoCapLocal_" + Guid.NewGuid().ToString("N"));
        var store = new LocalTestIndexStore(Root, Local);
        var base0 = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var mutazioni = new List<LocalIndexMutation>(Quanti);
        for (var i = 0; i < Quanti; i++)
        {
            var uidBytes = new byte[16];
            BitConverter.TryWriteBytes(uidBytes, i + 1);
            var tc = new TestCase
            {
                Uid = new Guid(uidBytes),
                Id = $"TC-{i:D5}",
                DataCreazione = base0.AddDays(-1),
                UltimaModifica = base0.AddMinutes(i % 997),       // istanti ripetuti: legami frequenti nell'ordine
                RevisioneDati = 1,
                CreatoDa = "Paolo",
                DescrizioneBreve = "Test " + i,
                Descrizione = "Test sopra il vecchio tetto " + i,
                StatoLavoro = StatoLavoroTest.Pianificato,
                Componente = new EcuSelection(i < TestConEcuRara
                    ? new[] { "Componente-A", i % 2 == 0 ? "Componente-B" : "Componente-C", "Componente-RARA" }
                    : new[] { "Componente-A", i % 2 == 0 ? "Componente-B" : "Componente-C" }),
            };
            mutazioni.Add(LocalIndexMutation.Upsert(Path.Combine(Root, $"sopra_{i:D6}.json"), tc.UltimaModifica, 100, tc));
        }
        store.ApplyBatch(mutazioni);
        DatabasePath = store.DatabasePath;
    }

    public string Root { get; }
    public string Local { get; }
    public string DatabasePath { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        try { if (Directory.Exists(Local)) Directory.Delete(Local, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>
/// QueryAll senza tetto implicito (Audit e Report): oltre 20.000 Test abbinati il risultato resta completo e identico
/// alla lettura a pagine OFFSET e alla deserializzazione da stringa del JSON. Il limite resta solo se e' esplicito.
/// </summary>
[Collection("ArchivioSopraTetto")]
public sealed class LocalTestArchiveQueryNoCapTests
{
    private readonly ArchivioSopraTettoFixture _archivio;

    public LocalTestArchiveQueryNoCapTests(ArchivioSopraTettoFixture archivio) => _archivio = archivio;

    [Fact]
    public void QueryAll_SenzaTetto_OltreVecchioLimite_RestituisceTuttiEIdenticiAPagineOffset()
    {
        var service = new LocalTestArchiveQueryService(_archivio.DatabasePath);

        Assert.Equal(ArchivioSopraTettoFixture.Quanti, service.QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount);
        var tutti = service.QueryAll(new LocalTestArchiveQuery());

        Assert.Equal(ArchivioSopraTettoFixture.Quanti, tutti.Count);
        Assert.Equal(tutti.Count, tutti.Select(x => x.Uid).Distinct().Count());
        Assert.Equal(PaginazioneAOffset(service, new LocalTestArchiveQuery()).Select(Firma), tutti.Select(Firma));
    }

    [Fact]
    public void QueryAll_FiltroEcuDelRiepilogoAudit_OltreVecchioLimite_RestituisceTuttiIMatch()
    {
        var service = new LocalTestArchiveQueryService(_archivio.DatabasePath);

        // Stesso scoping di Audit (AllowedEcus): tutti i Test hanno il componente-A, quindi il conteggio supera 20.000.
        var ecuA = service.QueryAll(new LocalTestArchiveQuery { AllowedEcus = new[] { "Componente-A" } });
        Assert.Equal(ArchivioSopraTettoFixture.Quanti, ecuA.Count);

        // componente-B su meta' dei Test, con confronto senza distinzione di maiuscole come nell'indice.
        var ecuB = service.QueryAll(new LocalTestArchiveQuery { AllowedEcus = new[] { "componente-b" } });
        Assert.Equal(ArchivioSopraTettoFixture.Quanti / 2, ecuB.Count);
        Assert.All(ecuB, tc => Assert.Contains(tc.Componente, e => e.Equals("Componente-B", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void QueryAll_MaterializzazioneDaByte_UgualeAllaDeserializzazioneDaStringa()
    {
        var letti = new LocalTestArchiveQueryService(_archivio.DatabasePath).QueryAll(new LocalTestArchiveQuery());
        var attesi = OracoloDaStringaJson(_archivio.DatabasePath);

        Assert.Equal(attesi.Count, letti.Count);
        for (var i = 0; i < attesi.Count; i++)
        {
            Assert.Equal(attesi[i].Json, JsonSerializer.Serialize(letti[i], JsonIo.CompactOptions));
            Assert.Equal(attesi[i].Stamp, TestCaseFingerprint.GetStored(letti[i]));
        }
    }

    [Fact]
    public void QueryAll_LimiteEsplicito_FallisceChiusoSenzaMaterializzare()
    {
        var service = new LocalTestArchiveQueryService(_archivio.DatabasePath);

        Assert.Throws<LocalTestArchiveResultLimitExceededException>(() =>
            service.QueryAll(new LocalTestArchiveQuery(), maximumRows: ArchivioSopraTettoFixture.Quanti - 1));
        Assert.Equal(ArchivioSopraTettoFixture.Quanti,
            service.QueryAll(new LocalTestArchiveQuery(), maximumRows: ArchivioSopraTettoFixture.Quanti).Count);
    }

    /// <summary>Oracolo indipendente dalla materializzazione in produzione: stesso ordine SQL, JSON letto come stringa
    /// e deserializzato con l'API da stringa, normalizzazione e stamp come nel percorso di lettura.</summary>
    private static List<(string Json, string? Stamp)> OracoloDaStringaJson(string databasePath)
    {
        var risultato = new List<(string Json, string? Stamp)>();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.json, c.content_hash,
                   (SELECT value FROM meta WHERE key = 'content_hash_algo') AS marcatore_algoritmo
            FROM test_summary s
            JOIN test_case_cache c ON c.path = s.path
            WHERE s.eliminato = 0
            ORDER BY s.ultima_modifica_ticks DESC, s.id COLLATE NOCASE, s.uid;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tc = JsonSerializer.Deserialize<TestCase>(reader.GetString(0), JsonIo.Options)!;
            tc.NormalizzaVeicoli();
            var stamp = TestCaseFingerprint.StampDaIndice(tc, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            risultato.Add((JsonSerializer.Serialize(tc, JsonIo.CompactOptions), stamp));
        }
        return risultato;
    }

    /// <summary>Riferimento: pagine a OFFSET da MaxPageSize sullo stesso ordine (percorso diverso dal keyset).</summary>
    private static List<TestCase> PaginazioneAOffset(LocalTestArchiveQueryService service, LocalTestArchiveQuery query)
    {
        var risultato = new List<TestCase>();
        var totale = service.QuerySlice(query, 0, 1).TotalCount;
        for (long offset = 0; offset < totale; offset += LocalTestArchiveQueryService.MaxPageSize)
            risultato.AddRange(service.QuerySlice(query, offset, LocalTestArchiveQueryService.MaxPageSize).Items);
        return risultato;
    }

    private static string Firma(TestCase tc) =>
        $"{tc.Uid:N}|{tc.Id}|{tc.UltimaModifica.Ticks}|{tc.Eliminato}|{tc.Assegnatario}|{tc.Origine}|{tc.DescrizioneBreve}";
}
