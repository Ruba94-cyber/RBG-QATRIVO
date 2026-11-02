using System.Diagnostics;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>
/// Benchmark riproducibile richiesto dalla review del fix bfbd590 (F1, AGENTS.md "Performance/scale claims"):
/// Load() normalizza lo sha storico di ogni riga upsert attiva prima di restituire la baseline al caller.
/// Prima di questo file la normalizzazione veniva calcolata EAGER in Load() per ogni riga (seconda
/// deserializzazione dell'intero catalogo); ora e' differita in PhaseDLoadedEntityState.BaselinePayloadSha256Factory
/// e viene pagata solo per le UID effettivamente confrontate in Save()/SavePatch(). Questo test isola il costo
/// di Load() da quello di Save() (il journal viene scritto direttamente, senza passare dall'engine) e misura
/// tre scenari su un catalogo grande, scrivendo i numeri in .ai/artifacts/benchmark_load_phaseD.txt.
/// </summary>
public sealed class PhaseDMasterLoadBenchmarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Bench_Shared_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Bench_Local_" + Guid.NewGuid().ToString("N"));

    public PhaseDMasterLoadBenchmarkTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(20_000)]
    [InlineData(100_000)]
    public void Benchmark_Load_CatalogoGrande(int righe)
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);

        SeedCatalog(righe);

        // Prima Load(): materializzazione SQLite locale a freddo (ingest completo del journal) + normalizzazione
        // (lazy dopo il fix: qui NON viene invocata, dato che nessun caller accede a BaselinePayloadSha256).
        var engineFreddo = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var swFreddo = Stopwatch.StartNew();
        var loadedFreddo = engineFreddo.Load();
        swFreddo.Stop();
        Assert.Equal(righe, loadedFreddo.Items.Count);

        // Seconda Load(), STESSA istanza engine: verifica che una rilettura successiva non ripaghi il costo di
        // materializzazione (SQLite gia' aggiornato, nessuna source cambiata).
        var swSecondaLettura = Stopwatch.StartNew();
        var loadedSecondaLettura = engineFreddo.Load();
        swSecondaLettura.Stop();
        Assert.Equal(righe, loadedSecondaLettura.Items.Count);

        // Terza Load(), NUOVA istanza engine sullo stesso local db: simula un riavvio dell'app (SQLite gia'
        // materializzato, ma cache per-istanza _normalizedShaCache vuota) — lo scenario piu' realistico per un
        // utente che riapre una pagina master dopo l'avvio, che e' esattamente il caso che F1 contestava.
        var engineNuovaIstanza = new PhaseDMasterRepositoryEngine<Funzionalita>(_root, "funzionalita", localBaseDirectory: _local);
        var swNuovaIstanza = Stopwatch.StartNew();
        var loadedNuovaIstanza = engineNuovaIstanza.Load();
        swNuovaIstanza.Stop();
        Assert.Equal(righe, loadedNuovaIstanza.Items.Count);

        var report =
            $"[{DateTime.UtcNow:O}] Righe={righe} " +
            $"LoadFreddo(materializzazione_sqlite_completa)={swFreddo.ElapsedMilliseconds}ms " +
            $"LoadStessaIstanza(sqlite_invariato)={swSecondaLettura.ElapsedMilliseconds}ms " +
            $"LoadNuovaIstanza(sqlite_gia_materializzato_cache_vuota)={swNuovaIstanza.ElapsedMilliseconds}ms" +
            Environment.NewLine;

        var artifactsDir = TrovaCartellaArtifacts();
        if (artifactsDir != null)
            File.AppendAllText(Path.Combine(artifactsDir, "benchmark_load_phaseD.txt"), report);
    }

    private void SeedCatalog(int righe)
    {
        // Scrittura diretta del journal, senza passare dall'engine: isola il costo di Load() da quello di
        // Save() (che per un seed di questa scala avrebbe un costo dominante e diverso, gia' coperto dal test
        // di scala esistente Save_100kPoiUnaModifica_PubblicaUnaSolaMutationNelNuovoCurrent).
        const int perBatch = 2000;
        var device = Device(1);
        var scritte = 0;
        var sequenza = 0L;
        while (scritte < righe)
        {
            var count = Math.Min(perBatch, righe - scritte);
            var mutations = new List<PhaseDMasterMutation>(count);
            for (var i = 0; i < count; i++)
            {
                var entity = new Funzionalita { Uid = Guid.NewGuid(), Nome = $"FUN-BENCH-{scritte + i:D6}" };
                var payload = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
                mutations.Add(new PhaseDMasterMutation
                {
                    MutationId = Guid.NewGuid(),
                    Catalog = "funzionalita",
                    EntityUid = entity.Uid,
                    Operation = PhaseDMasterMutationOperation.Upsert,
                    VersionVector = new Dictionary<string, long> { [device] = ++sequenza },
                    PayloadJson = payload,
                    PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
                    BusinessKey = entity.Nome,
                    TimestampUtc = DateTime.UtcNow
                });
            }
            var batch = new PhaseDMasterMutationBatch
            {
                BatchId = Guid.NewGuid(),
                DeviceId = device,
                DeviceSequence = (scritte / perBatch) + 1,
                CreatedUtc = DateTime.UtcNow,
                Mutations = mutations
            };
            batch.Validate();
            var path = PhaseDMasterJournalService.CurrentPath(_root, device);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
            scritte += count;
        }
    }

    private static string? TrovaCartellaArtifacts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, ".ai", "artifacts");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string Device(int index) => $"{index + 1:x32}";
}
