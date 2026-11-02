using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>Review 6.4.0, punto 1: prima di questi test la migrazione "Pianificato legacy → DaEseguire" viveva
/// solo dentro LocalTestIndexStore (proiezione SQLite), mentre TestCaseRepository (editor/liste in memoria)
/// non la conosceva affatto — stesso file, due interpretazioni diverse dello stesso Esito. Questi test
/// blindano il punto unico condiviso <see cref="TestCaseLegacyMigrations"/> e il fatto che editor e
/// statistiche ora concordano sempre sullo stesso Test.</summary>
public sealed class TestCaseLegacyMigrationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_LegacyMig_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_LegacyMigLocal_" + Guid.NewGuid().ToString("N"));

    public TestCaseLegacyMigrationsTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
        new DataPaths(_root).EnsureStructure();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    [Fact]
    public void EditorEStatistiche_ConcordanoSulloStessoTestLegacyPianificato()
    {
        var paths = new DataPaths(_root);
        var path = Path.Combine(paths.TestCasesDir, "testcase_TC-LEG-1.json");
        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-LEG-1",
            SchemaVersion = 5, // simula un file scritto da un client pre-6.4.0
            StatoLavoro = StatoLavoroTest.Pianificato,
            Componente = new EcuSelection { "Componente-A" },
            Veicoli = ["Daily"],
            Veicolo = "Daily",
            Descrizione = "legacy",
            Esecuzioni =
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Data = DateTime.Now,
                    Operatore = "Paolo",
                    Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.Conforme } }
                }
            }
        };
        JsonIo.WriteAtomic(path, tc);

        var store = new LocalTestIndexStore(_root, _local);
        var repo = new TestCaseRepository(paths, store);
        repo.RefreshIndex();

        // Editor/liste in memoria: l'oggetto caricato deve già mostrare "Da eseguire", non più Conforme.
        var dallEditor = Assert.Single(repo.GetAll());
        Assert.Equal(Esito.DaEseguire, dallEditor.Esecuzioni[0].Condizioni[0].Esito);

        // Statistiche/proiezione SQLite: stessa interpretazione, l'operazione non conta.
        var stats = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Equal(0, stats.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void UnConformeRealeInseritoDaClient640_NonVieneMaiToccato()
    {
        // SchemaVersion di default (CurrentSchemaVersion): un file già scritto da un client 6.4.0+ non entra
        // mai nel ramo di migrazione, anche se ancora Pianificato con Esito=Conforme e nessuna nota — in un
        // client 6.4.0+ questo può accadere solo per una scelta reale dell'operatore.
        var paths = new DataPaths(_root);
        var path = Path.Combine(paths.TestCasesDir, "testcase_TC-LEG-2.json");
        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-LEG-2",
            StatoLavoro = StatoLavoroTest.Pianificato,
            Componente = new EcuSelection { "Componente-A" },
            Veicoli = ["Daily"],
            Veicolo = "Daily",
            Descrizione = "reale",
            Esecuzioni =
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Data = DateTime.Now,
                    Operatore = "Paolo",
                    Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.Conforme } }
                }
            }
        };
        JsonIo.WriteAtomic(path, tc);

        var store = new LocalTestIndexStore(_root, _local);
        var repo = new TestCaseRepository(paths, store);
        repo.RefreshIndex();

        var dallEditor = Assert.Single(repo.GetAll());
        Assert.Equal(Esito.Conforme, dallEditor.Esecuzioni[0].Condizioni[0].Esito);

        var stats = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Equal(1, stats.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void MigraInLetturaDaTesto_EsitoAssenteDalJson_TornaAlVecchioDefaultConforme()
    {
        // Gap teorico (review 6.4.0, punto 3): un JSON legacy la cui Condizione OMETTE del tutto la proprietà
        // "Esito" (impossibile per un file scritto da TestLoom stesso, che grazie a
        // JsonIo.Options.DefaultIgnoreCondition=Never scrive sempre ogni proprietà — solo un file esterno o
        // manomesso può arrivare così) deve tornare al vecchio default implicito Conforme, non al nuovo
        // default CLR DaEseguire.
        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-LEG-3",
            SchemaVersion = 5,
            StatoLavoro = StatoLavoroTest.Concluso, // Concluso: nessuna ulteriore migrazione Pianificato sopra
            Esecuzioni =
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" } } }
                }
            }
        };
        var json = $$"""
            {
              "SchemaVersion": 5,
              "Uid": "{{tc.Uid:N}}",
              "Id": "TC-LEG-3",
              "StatoLavoro": "Concluso",
              "Esecuzioni": [
                {
                  "Iterazione": 1,
                  "Condizioni": [
                    { "Componente": ["Componente-A"] }
                  ]
                }
              ]
            }
            """;

        TestCaseLegacyMigrations.MigraInLetturaDaTesto(tc, json);

        Assert.Equal(Esito.Conforme, tc.Esecuzioni[0].Condizioni[0].Esito);
    }

    [Fact]
    public void MigraInLetturaDaTesto_EsitoAssenteSuPianificato_VieneUlteriormenteMigratoADaEseguire()
    {
        // Le due regole si compongono: assente => Conforme (vecchio default), poi Conforme senza altra
        // evidenza su un Test ancora Pianificato => Da eseguire (regola esistente).
        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-LEG-4",
            SchemaVersion = 5,
            StatoLavoro = StatoLavoroTest.Pianificato,
            Esecuzioni =
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" } } }
                }
            }
        };
        var json = $$"""
            {
              "SchemaVersion": 5,
              "Uid": "{{tc.Uid:N}}",
              "Id": "TC-LEG-4",
              "StatoLavoro": "Pianificato",
              "Esecuzioni": [
                { "Iterazione": 1, "Condizioni": [ { "Componente": ["Componente-A"] } ] }
              ]
            }
            """;

        TestCaseLegacyMigrations.MigraInLetturaDaTesto(tc, json);

        Assert.Equal(Esito.DaEseguire, tc.Esecuzioni[0].Condizioni[0].Esito);
    }

    [Fact]
    public void MigraInLetturaDaTesto_EsitoScrittoEsplicitamente_NonVieneToccatoDalRamoAssenza()
    {
        // Un "Conforme" scritto esplicitamente nel JSON (chiave presente) non è mai un artefatto: solo
        // l'assenza totale della chiave attiva il fallback.
        var tc = new TestCase
        {
            Uid = Guid.NewGuid(),
            Id = "TC-LEG-5",
            SchemaVersion = 5,
            StatoLavoro = StatoLavoroTest.Concluso,
            Esecuzioni =
            {
                new Esecuzione
                {
                    Iterazione = 1,
                    Condizioni = { new Condizione { Componente = new EcuSelection { "Componente-A" }, Esito = Esito.NonConforme } }
                }
            }
        };
        var json = $$"""
            {
              "SchemaVersion": 5,
              "Uid": "{{tc.Uid:N}}",
              "Id": "TC-LEG-5",
              "StatoLavoro": "Concluso",
              "Esecuzioni": [
                { "Iterazione": 1, "Condizioni": [ { "Componente": ["Componente-A"], "Esito": "NonConforme" } ] }
              ]
            }
            """;

        TestCaseLegacyMigrations.MigraInLetturaDaTesto(tc, json);

        Assert.Equal(Esito.NonConforme, tc.Esecuzioni[0].Condizioni[0].Esito);
    }
}
