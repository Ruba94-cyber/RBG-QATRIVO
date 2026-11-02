using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Filtri lista a selezione multipla della query SQLite paginata (pagina Test, richiesta utente 6.4.2):
/// ogni lista e' un IN parametrizzato (OR fra i valori), lista vuota/null = nessun filtro, e le liste utente si
/// combinano in AND con gli scoping (AllowedEcus/AllowedVehicles) invece di sostituirli. Ogni caso confronta il
/// risultato SQL con un oracolo LINQ calcolato sugli stessi Test in memoria.</summary>
public sealed class LocalTestArchiveQueryMultiValueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_QueryMulti_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_QueryMultiLocal_" + Guid.NewGuid().ToString("N"));
    private readonly List<TestCase> _tests = new();
    private readonly LocalTestArchiveQueryService _service;

    public LocalTestArchiveQueryMultiValueTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var componente = new[] { "ECM", "BCM", "TCU" };
        var assegnatari = new[] { "Mario", "Luigi", "Anna", null };
        var severita = Enum.GetValues<SeveritaAnomalia>();
        var priorita = Enum.GetValues<PrioritaOperativa>();
        var stati = Enum.GetValues<StatoLavoroTest>();
        var esiti = Enum.GetValues<Esito>();
        for (var i = 0; i < 48; i++)
        {
            var tc = new TestCase
            {
                Uid = Guid.NewGuid(),
                Id = $"TC-{i:D4}",
                DataCreazione = now.AddDays(-1),
                UltimaModifica = now.AddMinutes(i),
                RevisioneDati = 1,
                CreatoDa = i % 3 == 0 ? "Paolo" : i % 3 == 1 ? "Sara" : "Elena",
                DescrizioneBreve = "Test " + i,
                Descrizione = "Multi " + i,
                StatoLavoro = stati[i % stati.Length],
                Priorita = priorita[i % priorita.Length],
                Severita = i % 4 == 3 ? null : severita[i % severita.Length],
                Assegnatario = assegnatari[i % assegnatari.Length],
                Componente = componente[i % 3],
                Veicoli = [i % 2 == 0 ? "Daily" : "Alpha"],
                RequisitoId = [$"REQ-{i % 5}"],
                Funzionalita = [i % 4 == 0 ? "Guasto" : i % 4 == 1 ? "Parameters" : "Flash"],
            };
            tc.Veicolo = tc.Veicoli[0];
            if (i % 5 != 4) tc.Esecuzioni.Add(new Esecuzione { Data = now, Esito = esiti[i % esiti.Length] });
            _tests.Add(tc);
            store.Upsert(Path.Combine(_root, $"testcase_{i:D4}.json"), now.AddMinutes(i), 100 + i, tc);
        }
        _service = new LocalTestArchiveQueryService(store.DatabasePath);
    }

    private HashSet<string> Ids(LocalTestArchiveQuery q) =>
        _service.QueryAll(q, maximumRows: 1000).Select(t => t.Id).ToHashSet();

    private static HashSet<string> Atteso(IEnumerable<TestCase> source) => source.Select(t => t.Id).ToHashSet();

    [Fact]
    public void Componenti_SonoUnInInsensibileAlCaso()
    {
        var atteso = Atteso(_tests.Where(t => t.Componente.Any(e => e is "ECM" or "TCU")));
        Assert.NotEmpty(atteso);
        Assert.Equal(atteso, Ids(new LocalTestArchiveQuery { Componenti = ["ecm", "TCU"] }));
    }

    [Fact]
    public void Vehicles_SonoUnIn()
    {
        Assert.Equal(Atteso(_tests.Where(t => t.Veicoli.Contains("Daily"))), Ids(new LocalTestArchiveQuery { Vehicles = ["Daily"] }));
        Assert.Equal(_tests.Count, Ids(new LocalTestArchiveQuery { Vehicles = ["daily", "Alpha"] }).Count);
    }

    [Fact]
    public void Outcomes_SonoUnIn_ETestSenzaEsecuzioneNonPassano()
    {
        var scelti = new[] { Esito.Conforme, Esito.NonConforme };
        var atteso = Atteso(_tests.Where(t => t.StatoCorrente.HasValue && scelti.Contains(t.StatoCorrente.Value)));
        Assert.NotEmpty(atteso);
        var risultato = Ids(new LocalTestArchiveQuery { Outcomes = scelti });
        Assert.Equal(atteso, risultato);
        Assert.True(risultato.Count < _tests.Count);
    }

    [Fact]
    public void Assignees_ECreators_SonoUnIn()
    {
        Assert.Equal(Atteso(_tests.Where(t => t.Assegnatario is "Mario" or "Anna")),
            Ids(new LocalTestArchiveQuery { Assignees = ["Mario", "Anna"] }));
        Assert.Equal(Atteso(_tests.Where(t => t.CreatoDa is "Paolo" or "Elena")),
            Ids(new LocalTestArchiveQuery { Creators = ["Paolo", "Elena"] }));
    }

    [Fact]
    public void Requirements_EFunctionalities_SonoUnInSuTabelleFiglie()
    {
        Assert.Equal(Atteso(_tests.Where(t => t.RequisitoId.Any(r => r is "REQ-1" or "REQ-3"))),
            Ids(new LocalTestArchiveQuery { Requirements = ["REQ-1", "REQ-3"] }));
        Assert.Equal(Atteso(_tests.Where(t => t.Funzionalita.Any(f => f is "Guasto" or "Flash"))),
            Ids(new LocalTestArchiveQuery { Functionalities = ["Guasto", "Flash"] }));
    }

    [Fact]
    public void SeveritiesPrioritiesEWorkStates_SonoUnIn()
    {
        var sev = Enum.GetValues<SeveritaAnomalia>().Take(2).ToArray();
        var pri = Enum.GetValues<PrioritaOperativa>().Take(2).ToArray();
        var sta = Enum.GetValues<StatoLavoroTest>().Take(2).ToArray();
        Assert.Equal(Atteso(_tests.Where(t => t.Severita.HasValue && sev.Contains(t.Severita.Value))),
            Ids(new LocalTestArchiveQuery { Severities = sev }));
        Assert.Equal(Atteso(_tests.Where(t => pri.Contains(t.Priorita))),
            Ids(new LocalTestArchiveQuery { Priorities = pri }));
        Assert.Equal(Atteso(_tests.Where(t => sta.Contains(t.StatoLavoro))),
            Ids(new LocalTestArchiveQuery { WorkStates = sta }));
    }

    [Fact]
    public void ListaVuotaONull_NonFiltra_UnSoloElementoEquivaleAlFiltroSingolo()
    {
        Assert.Equal(_tests.Count, Ids(new LocalTestArchiveQuery { Componenti = [], Vehicles = [], Outcomes = [], Assignees = [], Creators = [], Requirements = [], Functionalities = [], Severities = [], Priorities = [] }).Count);
        Assert.Equal(Ids(new LocalTestArchiveQuery { Componente = "BCM", Assignee = "Luigi", Requirement = "REQ-1" }),
            Ids(new LocalTestArchiveQuery { Componenti = ["BCM"], Assignees = ["Luigi"], Requirements = ["REQ-1"] }));
    }

    [Fact]
    public void FiltriDiversiSiCombinanoInAnd()
    {
        var atteso = Atteso(_tests.Where(t => t.Componente.Any(e => e is "ECM" or "BCM") && t.Assegnatario is "Mario" or "Luigi" && t.RequisitoId.Any(r => r is "REQ-0" or "REQ-1" or "REQ-2")));
        var risultato = Ids(new LocalTestArchiveQuery { Componenti = ["ECM", "BCM"], Assignees = ["Mario", "Luigi"], Requirements = ["REQ-0", "REQ-1", "REQ-2"] });
        Assert.Equal(atteso, risultato);
    }

    [Fact]
    public void FiltroUtenteEcu_SiIntersecaConLoScopingAllowedEcus_NonLoSostituisce()
    {
        var atteso = Atteso(_tests.Where(t => t.Componente.Any(e => e == "BCM")));
        var risultato = Ids(new LocalTestArchiveQuery { Componenti = ["ECM", "BCM"], AllowedEcus = ["BCM", "TCU"] });
        Assert.NotEmpty(atteso);
        Assert.Equal(atteso, risultato);
        // L'utente non puo' "uscire" dallo scoping scegliendo un componente non consentita.
        Assert.Empty(Ids(new LocalTestArchiveQuery { Componenti = ["ECM"], AllowedEcus = ["TCU"] }));
    }

    [Fact]
    public void FiltroUtenteVeicoli_SiIntersecaConLoScopingAllowedVehicles()
    {
        Assert.Empty(Ids(new LocalTestArchiveQuery { Vehicles = ["Alpha"], AllowedVehicles = ["Daily"] }));
        Assert.Equal(Atteso(_tests.Where(t => t.Veicoli.Contains("Daily"))),
            Ids(new LocalTestArchiveQuery { Vehicles = ["Daily", "Alpha"], AllowedVehicles = ["Daily"] }));
    }

    [Fact]
    public void ValoriMaliziosi_SonoSoloParametri_NessunaIniezioneSql()
    {
        var subdolo = "x') OR 1=1; DROP TABLE test_summary; --";
        Assert.Empty(Ids(new LocalTestArchiveQuery { Assignees = [subdolo] }));
        Assert.Empty(Ids(new LocalTestArchiveQuery { Componenti = [subdolo] }));
        Assert.Empty(Ids(new LocalTestArchiveQuery { Requirements = [subdolo], Creators = [subdolo] }));
        // La tabella esiste ancora e contiene tutti i Test.
        Assert.Equal(_tests.Count, Ids(new LocalTestArchiveQuery()).Count);
    }

    [Fact]
    public void ListaNonVuotaConSoliValoriVuoti_NonDiventaTutti_FailClosed()
    {
        Assert.Empty(Ids(new LocalTestArchiveQuery { Assignees = [""] }));
        Assert.Empty(Ids(new LocalTestArchiveQuery { Componenti = ["  "] }));
    }

    [Fact]
    public void ListeGrandi_SopraLaSogliaDelleUiRestanoParametrizzate()
    {
        // 700 valori distinti (ordine di grandezza delle opzioni dei filtri a 100k Test): sotto il limite di
        // variabili di SQLite e con lo stesso risultato dell'oracolo.
        var molti = Enumerable.Range(0, 700).Select(i => "REQ-X" + i).Append("REQ-2").ToList();
        Assert.Equal(Atteso(_tests.Where(t => t.RequisitoId.Contains("REQ-2"))),
            Ids(new LocalTestArchiveQuery { Requirements = molti }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }
}
