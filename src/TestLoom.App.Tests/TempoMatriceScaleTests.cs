using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>TIME (6.6.1), scala 50k e 100k Test sul PERCORSO DI PRODUZIONE: indice locale su disco, lettura SQL e proiezione
/// (QueryAllAnalisi), poi la matrice Tempo totale / Tempo medio nelle due basi e lo Stato come riferimento. Il fixture in RAM viene
/// rilasciato prima delle misure di produzione (il suo heap vivo gonfierebbe le pause GC). Ogni misura della matrice e' la mediana di
/// tre esecuzioni dopo un riscaldamento. I totali sono verificati contro un conteggio indipendente costruito insieme al fixture.
/// Misura anche la migrazione 14 -> 15 e la latenza della regola di copia. Categoria Scale: fuori dalla suite rapida.</summary>
public sealed class TempoMatriceScaleTests : IDisposable
{
    // Budget (ms): tre-quattro volte le misure di sviluppo sulla stessa macchina, per non rendere il test fragile sotto carico.
    // Un superamento segnala una regressione reale o un PC molto carico: vedi RELEASE_NOTES_6.6.1.md, «Prestazioni».
    private const double BudgetTuttiMs = 2000;
    private const double BudgetUltimoMs = 1500;
    private const double BudgetCopiaP95Ms = 500;
    private const double BudgetMigrazioneMs = 60_000;
    private const double BudgetRicostruzioneMs = 300_000;

    private static readonly DateTime Giorno = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_TempoScale_" + Guid.NewGuid().ToString("N"));

    public TempoMatriceScaleTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    [Trait("Category", "Scale")]
    public void TempoMatriceScale50k() => Esegui(50_000);

    [Fact]
    [Trait("Category", "Scale")]
    public void TempoMatriceScale100k() => Esegui(100_000);

    private void Esegui(int test)
    {
        List<TestCase>? fixture = CostruisciFixture(test, out var attesi);
        var dataDir = Path.Combine(_root, "data");
        var cacheDir = Path.Combine(_root, "cache");
        var store = new LocalTestIndexStore(dataDir, cacheDir);
        List<LocalIndexWriteEntry>? voci = fixture.Select(tc => new LocalIndexWriteEntry(Path.Combine(dataDir, tc.Id + ".json"), Giorno, 100, tc)).ToList();

        var ricostruzione = Stopwatch.StartNew();
        store.ReconcileFull(voci, DateTime.UtcNow, markFullScanCompleted: true);
        ricostruzione.Stop();

        // Il fixture in RAM non serve piu': si rilascia prima delle misure di produzione.
        voci = null;
        fixture = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var migrazione = Stopwatch.StartNew();
        var riaperto = MisuraMigrazione(store.DatabasePath, dataDir, cacheDir, out var durateNonNulle);
        migrazione.Stop();
        Assert.Equal(attesi.DurateNote, durateNonNulle);

        var copia = MisuraCopia(riaperto);

        var query = new LocalTestArchiveQueryService(riaperto.DatabasePath);
        List<TestCase> proiettati = [];
        var sql = MisuraRipetuta(() => proiettati = query.QueryAllAnalisi(new LocalTestArchiveQuery()), ripetizioni: 3);
        Assert.Equal(test, proiettati.Count);

        var config = new ConfigurazioneMatriceReport
        {
            CampiX = [DimensioniMatrice.Componente],
            CampiY = [DimensioniMatrice.Funzionalita],
        };
        RisultatoMatriceReport? risultatoStato = null;
        RisultatoMatriceReport? risultatoTutte = null;
        RisultatoMatriceReport? risultatoUltimo = null;
        var stato = MisuraRipetuta(() => risultatoStato = ReportMatriceService.Genera(proiettati, Con(config, ModalitaCellaMatrice.Percentuale), [], [], []));
        var tutte = MisuraRipetuta(() => risultatoTutte = ReportMatriceService.Genera(proiettati, Con(config, ModalitaCellaMatrice.TempoTotale), [], [], []));
        var ultimo = MisuraRipetuta(() => risultatoUltimo = ReportMatriceService.Genera(proiettati, Con(config, ModalitaCellaMatrice.TempoTotale,
            BaseTempoMatrice.UltimoStato), [], [], []));

        var valoreTutte = risultatoTutte!.TotaleGenerale.Tempo;
        var valoreUltimo = risultatoUltimo!.TotaleGenerale.Tempo;
        Assert.NotNull(risultatoStato);
        Assert.Equal(attesi.SommaMinuti, valoreTutte.SommaMinuti);
        Assert.Equal(attesi.Eleggibili, valoreTutte.Eleggibili);
        Assert.Equal(attesi.Conosciuti, valoreTutte.Conosciuti);
        Assert.True(valoreUltimo.SommaMinuti <= valoreTutte.SommaMinuti);
        Assert.True(valoreUltimo.Eleggibili <= valoreTutte.Eleggibili);

        var processi = Process.GetProcesses().Count(p => p.ProcessName is "dotnet" or "testhost");
        _output.WriteLine($"TEMPO_SCALA_PRODUZIONE|test={test}|ProcessorCount={Environment.ProcessorCount}|processi_dotnet_testhost={processi}" +
            $"|sql_mediana_ms={sql.Mediana:F0}|sql_pausa_gc_ms={sql.PausaMedia:F0}|sql_alloc_mib={sql.MibMedi:F0}" +
            $"|stato_mediana_ms={stato.Mediana:F0}|tutte_mediana_ms={tutte.Mediana:F0}|tutte_min_ms={tutte.Minimo:F0}|tutte_max_ms={tutte.Massimo:F0}" +
            $"|tutte_alloc_mib={tutte.MibMedi:F0}|ultimo_mediana_ms={ultimo.Mediana:F0}|ultimo_min_ms={ultimo.Minimo:F0}|ultimo_max_ms={ultimo.Massimo:F0}" +
            $"|ultimo_alloc_mib={ultimo.MibMedi:F0}|ricostruzione_ms={ricostruzione.Elapsed.TotalMilliseconds:F0}" +
            $"|migrazione_14_15_ms={migrazione.Elapsed.TotalMilliseconds:F0}|copia_mediana_ms={copia.Mediana:F1}|copia_p95_ms={copia.P95:F1}");

        Assert.True(tutte.Mediana <= BudgetTuttiMs, $"Tempo totale, base Tutte le esecuzioni: mediana {tutte.Mediana:F0} ms sopra il budget {BudgetTuttiMs} ms");
        Assert.True(ultimo.Mediana <= BudgetUltimoMs, $"Tempo totale, base Ultimo stato: mediana {ultimo.Mediana:F0} ms sopra il budget {BudgetUltimoMs} ms");
        Assert.True(copia.P95 <= BudgetCopiaP95Ms, $"Regola di copia: p95 {copia.P95:F0} ms sopra il budget {BudgetCopiaP95Ms} ms");
        Assert.True(migrazione.Elapsed.TotalMilliseconds <= BudgetMigrazioneMs, "Migrazione 14 -> 15 sopra il budget");
        Assert.True(ricostruzione.Elapsed.TotalMilliseconds <= BudgetRicostruzioneMs, "Ricostruzione completa sopra il budget");
    }

    /// <summary>Migrazione 14 -> 15 sugli stessi dati: colonna della durata rimossa, versione riportata a 14, riapertura.</summary>
    private static LocalTestIndexStore MisuraMigrazione(string databasePath, string dataDir, string cacheDir, out int? durateNonNulle)
    {
        SqliteConnection.ClearAllPools();
        using (var connessione = new SqliteConnection($"Data Source={databasePath}"))
        {
            connessione.Open();
            using var comando = connessione.CreateCommand();
            comando.CommandText = "ALTER TABLE test_operation_attempt DROP COLUMN duration_minutes;";
            comando.ExecuteNonQuery();
            using var meta = connessione.CreateCommand();
            meta.CommandText = "UPDATE meta SET value = '14' WHERE key = 'schema_version';";
            meta.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var riaperto = new LocalTestIndexStore(dataDir, cacheDir);
        using (var connessione = new SqliteConnection($"Data Source={riaperto.DatabasePath}"))
        {
            connessione.Open();
            using var comando = connessione.CreateCommand();
            comando.CommandText = "SELECT COUNT(*) FROM test_operation_attempt WHERE duration_minutes IS NOT NULL;";
            durateNonNulle = Convert.ToInt32(comando.ExecuteScalar());
        }
        return riaperto;
    }

    /// <summary>Latenza della regola di copia su 25 ricerche (componente, Funzionalita e veicolo variabili).</summary>
    private static (double Mediana, double P95) MisuraCopia(LocalTestIndexStore store)
    {
        var copia = new List<double>();
        for (var k = 0; k < 25; k++)
        {
            var watch = Stopwatch.StartNew();
            store.TrovaDurataCopia([$"Componente-{k % 20:D2}"], $"F-{k % 50:D2}", [$"Daily-{k % 5}"], null);
            watch.Stop();
            copia.Add(watch.Elapsed.TotalMilliseconds);
        }
        copia.Sort();
        return (copia[copia.Count / 2], copia[(int)(copia.Count * 0.95) - 1]);
    }

    /// <summary>Misura ripetuta: una chiamata di riscaldamento non conteggiata, poi mediana, minimo, massimo, allocazioni e pausa GC.</summary>
    private static (double Mediana, double Minimo, double Massimo, double MibMedi, double PausaMedia) MisuraRipetuta(Action calcolo,
        int ripetizioni = 3)
    {
        calcolo();
        var ms = new List<double>(ripetizioni);
        var mib = new List<double>(ripetizioni);
        var pausa = new List<double>(ripetizioni);
        for (var k = 0; k < ripetizioni; k++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var pausa0 = GC.GetTotalPauseDuration();
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            calcolo();
            ms.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            mib.Add((GC.GetAllocatedBytesForCurrentThread() - b0) / 1048576.0);
            pausa.Add((GC.GetTotalPauseDuration() - pausa0).TotalMilliseconds);
        }
        ms.Sort();
        return (ms[ms.Count / 2], ms[0], ms[^1], mib.Average(), pausa.Average());
    }

    private static ConfigurazioneMatriceReport Con(ConfigurazioneMatriceReport b, ModalitaCellaMatrice modalita,
        BaseTempoMatrice baseTempo = BaseTempoMatrice.TutteLeEsecuzioni) => new()
    {
        CampiX = b.CampiX,
        CampiY = b.CampiY,
        ModalitaCella = modalita,
        BaseTempo = baseTempo,
    };

    private sealed record AttesiScala(long SommaMinuti, int Conosciuti, int Eleggibili, int DueEcu, int DurateNote);

    /// <summary>Fixture: per Test due revisioni. Rev 1: operazione su un componente (durata vuota ogni 7) e operazione su due componente;
    /// rev 2: ripetizione della prima operazione (stessa radice) e un'operazione pianificata con 99 minuti che non conta.
    /// Il conteggio atteso e' calcolato insieme al fixture, senza passare dalla matrice.</summary>
    private static List<TestCase> CostruisciFixture(int test, out AttesiScala attesi)
    {
        var casi = new List<TestCase>(test);
        long somma = 0;
        var conosciuti = 0;
        var eleggibili = 0;
        var durateNote = 0;
        for (var i = 0; i < test; i++)
        {
            var ecuA = $"Componente-{i % 20:D2}";
            var ecuB = $"Componente-{(i + 7) % 20:D2}";
            var funzionalitaA = $"F-{i % 50:D2}";
            var funzionalitaB = $"F-{(i + 3) % 50:D2}";
            var tc = new TestCase
            {
                Id = $"T-{i:D6}",
                Uid = Guid.NewGuid(),
                Componente = new EcuSelection([ecuA, ecuB]),
                Veicoli = [$"Daily-{i % 5}"],
                Funzionalita = [funzionalitaA, funzionalitaB],
                DataCreazione = Giorno.AddHours(i % 5000),
                UltimaModifica = Giorno.AddHours(i % 5000),
                Assegnatario = $"Op-{i % 30}",
            };

            var durataA = i % 7 == 0 ? (int?)null : 5 + i % 40;
            var operazioneA = new Condizione { Funzionalita = funzionalitaA, Esito = Esito.Conforme, Componente = new EcuSelection([ecuA]), DurataMinuti = durataA };
            var durataB = 3 + i % 25;
            var operazioneB = new Condizione { Funzionalita = funzionalitaB, Esito = Esito.Conforme, Componente = new EcuSelection([ecuA, ecuB]), DurataMinuti = durataB };
            var revisione1 = new Esecuzione { Iterazione = 1, Data = Giorno.AddDays(i % 300), Operatore = "Op", Esito = Esito.Conforme, Condizioni = [operazioneA, operazioneB] };

            var durataRipetuta = 2 + i % 9;
            var ripetuta = new Condizione { Funzionalita = funzionalitaA, Esito = Esito.Conforme, Componente = new EcuSelection([ecuA]), DurataMinuti = durataRipetuta, OperazioneOrigineUid = operazioneA.Uid };
            var pianificata = new Condizione { Funzionalita = funzionalitaB, Esito = Esito.DaEseguire, Componente = new EcuSelection([ecuB]), DurataMinuti = 99 };
            var revisione2 = new Esecuzione { Iterazione = 2, Data = revisione1.Data.AddDays(1), Operatore = "Op", Esito = Esito.Conforme, Condizioni = [ripetuta, pianificata] };
            tc.Esecuzioni.Add(revisione1);
            tc.Esecuzioni.Add(revisione2);
            casi.Add(tc);

            // Conteggio indipendente: ogni tentativo eseguito conta una volta nel totale generale; le durate note si sommano.
            eleggibili += 3;
            if (durataA is { } a) { somma += a; conosciuti++; durateNote++; }
            somma += durataB; conosciuti++; durateNote++;
            somma += durataRipetuta; conosciuti++; durateNote++;
            // L'operazione pianificata ha 99 minuti registrati nell'indice, ma non e' eseguita: non conta nei totali.
            durateNote++;
        }
        attesi = new AttesiScala(somma, conosciuti, eleggibili, DueEcu: test, DurateNote: durateNote);
        return casi;
    }
}
