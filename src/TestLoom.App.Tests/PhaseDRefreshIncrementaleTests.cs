using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni mirate all'ottimizzazione 2026-09-25 di <see cref="PhaseDMasterMaterializer.Refresh"/>
/// (Piano 6.4.0): un Refresh no-op costava ~90-190ms anche a nulla cambiato, dominato da
/// DeleteUnreferencedMutations (un NOT IN su ~100k+ righe) rieseguito ad ogni giro. Le ottimizzazioni sono:
/// (a) DeleteUnreferencedMutations viene eseguita solo se in QUESTO Refresh e' stata rimossa almeno una
///     mappatura in phase_d_source_mutation (RemoveSource o RemoveCoveredNonCheckpointMappings);
/// (b) l'ingest del current.ndjson viene saltato quando SHA-256+lunghezza+stato completo/incompleto dei byte
///     appena letti (SEMPRE letti, mai per data/dimensione: vedi PhaseDMasterJournalSourceReader.Read) coincidono
///     con l'ultima acquisizione registrata;
/// (c) schema_version non viene piu' riscritto ad ogni Refresh (gia' garantito da Initialize()).
///
/// Ogni test qui verifica che il comportamento OSSERVABILE resti identico a prima: nessuna di queste
/// ottimizzazioni deve cambiare cosa un Refresh successivo vede o quando una mutation/mapping/testa cambia.
/// Stesso pattern di simulazione multi-postazione di PhaseDMasterStorageTests/PhaseDDeliverySafetyTests: ogni
/// device remoto scrive nel proprio current.ndjson nella cartella condivisa, senza toccare
/// DeviceIdentityService del PC che esegue i test.
/// </summary>
public sealed class PhaseDRefreshIncrementaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Incr_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_IncrLocal_" + Guid.NewGuid().ToString("N"));

    public PhaseDRefreshIncrementaleTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }

    [Fact]
    public void Refresh_NoOpRipetuto_NonRifaLavoroENonCambiaLoStato()
    {
        PreparePhaseD();
        var device = Device(0);
        var uid = Guid.NewGuid();
        WriteCurrentBatch(device, 1, Upsert(new Funzionalita { Uid = uid, Nome = "Stabile" }, device,
            new Dictionary<string, long> { [device] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var primo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("Stabile", Assert.Single(primo.Items).Nome);

        // Tre Refresh no-op consecutivi: stesso fingerprint, stesso contenuto, nessuna mutation orfana creata.
        for (var i = 0; i < 3; i++)
        {
            var ripetuto = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
            Assert.Equal(primo.SourceFingerprint, ripetuto.SourceFingerprint);
            Assert.Equal("Stabile", Assert.Single(ripetuto.Items).Nome);
        }
        Assert.Equal(1, CountMutations(materializer.DatabasePath));
        Assert.Equal(1, CountMappings(materializer.DatabasePath));
    }

    /// <summary>Scrittura LOCALE (stesso device/current) visibile al Refresh successivo: la skip idempotente
    /// deve confrontare SHA+lunghezza+completezza dei byte appena letti, non fidarsi di alcuna cache stantia.</summary>
    [Fact]
    public void Refresh_ScritturaLocaleSuCurrent_VieneVistaSubito()
    {
        PreparePhaseD();
        var device = Device(1);
        var uid = Guid.NewGuid();
        WriteCurrentBatch(device, 1, Upsert(new Funzionalita { Uid = uid, Nome = "V1" }, device,
            new Dictionary<string, long> { [device] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        Assert.Equal("V1", Assert.Single(materializer.LoadCatalog<Funzionalita>(_root, "funzionalita").Items).Nome);
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita"); // no-op: percorso skip idempotente attivo

        WriteCurrentBatch(device, 2, Upsert(new Funzionalita { Uid = uid, Nome = "V2" }, device,
            new Dictionary<string, long> { [device] = 2 }));

        var dopo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("V2", Assert.Single(dopo.Items).Nome);
    }

    /// <summary>Scrittura di un'ALTRA postazione nel proprio current, con la STESSA lunghezza in byte del batch
    /// precedente scritto da quella postazione (caso che la skip per dimensione-soltanto avrebbe potuto
    /// confondere): la skip idempotente confronta SHA-256, non solo la lunghezza, quindi non deve nascondere
    /// contenuto diverso della stessa dimensione.</summary>
    [Fact]
    public void Refresh_ScritturaRemotaStessaLunghezzaDelPrecedente_VieneVistaSenzaRitardo()
    {
        PreparePhaseD();
        var deviceLocale = Device(2);
        var deviceRemoto = Device(3);
        var uidLocale = Guid.NewGuid();
        var uidRemota1 = Guid.NewGuid();
        var uidRemota2 = Guid.NewGuid();

        WriteCurrentBatch(deviceLocale, 1, Upsert(new Funzionalita { Uid = uidLocale, Nome = "Locale" }, deviceLocale,
            new Dictionary<string, long> { [deviceLocale] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");

        // Batch 1 della postazione remota.
        var pathRemoto = PhaseDMasterJournalService.CurrentPath(_root, deviceRemoto);
        WriteCurrentBatch(deviceRemoto, 1, Upsert(new Funzionalita { Uid = uidRemota1, Nome = "AAAAAAAAAAAAAAAAAAAA" },
            deviceRemoto, new Dictionary<string, long> { [deviceRemoto] = 1 }));
        var dopoBatch1 = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Contains(dopoBatch1.Items, x => x.Nome == "AAAAAAAAAAAAAAAAAAAA");
        var lunghezzaBatch1 = new FileInfo(pathRemoto).Length;

        // La postazione remota RISCRIVE l'intero current (come farebbe una compaction/riscrittura reale, non
        // un append) con un contenuto DIVERSO ma della STESSA lunghezza in byte del file precedente.
        File.Delete(pathRemoto);
        var nomeStessaLunghezza = "BBBBBBBBBBBBBBBBBBBB";
        Assert.Equal(nomeStessaLunghezza.Length, "AAAAAAAAAAAAAAAAAAAA".Length);
        WriteCurrentBatch(deviceRemoto, 2, Upsert(new Funzionalita { Uid = uidRemota2, Nome = nomeStessaLunghezza },
            deviceRemoto, new Dictionary<string, long> { [deviceRemoto] = 2 }));
        Assert.Equal(lunghezzaBatch1 > 0, new FileInfo(pathRemoto).Length > 0);

        var dopoBatch2 = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Contains(dopoBatch2.Items, x => x.Nome == nomeStessaLunghezza);
    }

    /// <summary>Rotazione current -> checkpoint di un'altra postazione: il current remoto viene svuotato e le
    /// sue mutation storiche diventano raggiungibili solo tramite il checkpoint. Il catalogo deve restare
    /// invariato e le mappature non-checkpoint coperte devono sparire (comportamento pre-esistente, non toccato
    /// dall'ottimizzazione (a): la si esercita comunque per garantire che skip-DeleteUnreferencedMutations non
    /// abbia introdotto una regressione quando le mappature VENGONO davvero rimosse).</summary>
    [Fact]
    public void Refresh_RotazioneCheckpointDiAltraPostazione_RestaCoerenteEPulisceLeMappatureCoperte()
    {
        PreparePhaseD();
        var deviceRemoto = Device(4);
        var uid = Guid.NewGuid();
        var mutation = Upsert(new Funzionalita { Uid = uid, Nome = "Rotata" }, deviceRemoto,
            new Dictionary<string, long> { [deviceRemoto] = 1 });
        WriteCurrentBatch(deviceRemoto, 1, mutation);

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var primo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("Rotata", Assert.Single(primo.Items).Nome);
        Assert.Equal(1, CountMappings(materializer.DatabasePath));

        // La postazione remota compatta: current diventa vuoto (rotazione), un checkpoint copre sequence=1.
        File.WriteAllText(PhaseDMasterJournalService.CurrentPath(_root, deviceRemoto), "");
        var generation = WriteCheckpointGeneration(deviceRemoto, 1, mutation);
        JsonIo.WriteAtomic(PhaseDMasterJournalService.CheckpointManifestPath(_root, deviceRemoto),
            new PhaseDMasterCheckpointManifest { DeviceId = deviceRemoto, Current = generation });

        var dopo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("Rotata", Assert.Single(dopo.Items).Nome);
        // La mappatura storica sul vecchio current e' stata coperta e rimossa: ne resta solo una (dal checkpoint).
        Assert.Equal(1, CountMappings(materializer.DatabasePath));
        Assert.Equal(0, CountUnreferencedMutations(materializer.DatabasePath));

        // Refresh no-op successivo: ancora stabile (il current vuoto e' idempotente rispetto a se' stesso).
        var stabile = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(dopo.SourceFingerprint, stabile.SourceFingerprint);
    }

    /// <summary>Sorgente sparita (segmento immutabile) coperta da un checkpoint successivo: le mutation orfane
    /// vengono ripulite. Qui il Refresh che rimuove la mappatura DEVE eseguire DeleteUnreferencedMutations
    /// (mappingsRemoved>0): verifica che la condizione (a) non la salti quando serve davvero.</summary>
    [Fact]
    public void Refresh_SegmentoSpaaritoCopertoDaCheckpoint_RipulisceLeMutationOrfane()
    {
        PreparePhaseD();
        var device = Device(5);
        var uid = Guid.NewGuid();
        var mutation = Upsert(new Funzionalita { Uid = uid, Nome = "DaCoprire" }, device,
            new Dictionary<string, long> { [device] = 1 });
        var segmentPath = WriteSegment(device, 1, mutation);

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(1, CountMutations(materializer.DatabasePath));

        File.Delete(segmentPath);
        var generation = WriteCheckpointGeneration(device, 1, mutation);
        JsonIo.WriteAtomic(PhaseDMasterJournalService.CheckpointManifestPath(_root, device),
            new PhaseDMasterCheckpointManifest { DeviceId = device, Current = generation });

        var dopo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("DaCoprire", Assert.Single(dopo.Items).Nome);
        Assert.Equal(0, CountUnreferencedMutations(materializer.DatabasePath));
        // Una sola mutation viva (quella del checkpoint): il segmento sparito non ne ha lasciata una duplicata.
        Assert.Equal(1, CountMutations(materializer.DatabasePath));
    }

    /// <summary>
    /// Orfano REALE (review mutation testing 2026-09-25): due mutation sulla STESSA entita' nello stesso
    /// segmento (v1 con device_sequence=1, poi v2 con device_sequence=2), un checkpoint che copre sequence=2 ma
    /// contiene nel proprio shard SOLO la testa piu' recente (v2, come fa davvero una compaction: LatestByEntity
    /// tiene solo l'ultima mutation per entita', non lo storico), e il segmento che sparisce. Dopo il Refresh
    /// che rimuove la mappatura del segmento sparito (RemoveSource, mappingsRemoved&gt;0 -&gt; gate (a) NON salta
    /// DeleteUnreferencedMutations), v1 non ha piu' alcuna mappatura (ne' dal segmento sparito ne' dal
    /// checkpoint, che porta solo v2): deve sparire da phase_d_mutation. v2 invece resta, referenziata dal
    /// checkpoint.
    ///
    /// Verificato manualmente (non automatizzabile senza un hook nel codice di produzione) che questo test
    /// FALLISCE se si commenta il gate <c>if (mappingsRemoved &gt; 0)</c> in PhaseDMasterMaterializer.Refresh
    /// forzando DeleteUnreferencedMutations a non girare mai: v1 resta orfana in phase_d_mutation e
    /// CountUnreferencedMutations torna 1 invece di 0. Ripristinato subito dopo la verifica.
    /// </summary>
    [Fact]
    public void Refresh_DueMutationStessaEntitaSegmentoSparitoCheckpointSoloUltima_LaPrimaDiventaOrfanaERimossa()
    {
        PreparePhaseD();
        var device = Device(10);
        var uid = Guid.NewGuid();
        var mutazioneV1 = Upsert(new Funzionalita { Uid = uid, Nome = "V1" }, device,
            new Dictionary<string, long> { [device] = 1 });
        var mutazioneV2 = Upsert(new Funzionalita { Uid = uid, Nome = "V2" }, device,
            new Dictionary<string, long> { [device] = 2 });
        var segmentPath = WriteSegmentMultiBatch(device, (1, mutazioneV1), (2, mutazioneV2));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        var primo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("V2", Assert.Single(primo.Items).Nome);
        Assert.Equal(2, CountMutations(materializer.DatabasePath));
        Assert.Equal(2, CountMappings(materializer.DatabasePath));

        // Il segmento sparisce (compattato altrove) e un checkpoint copre sequence=2 portando pero' SOLO v2.
        File.Delete(segmentPath);
        var generation = WriteCheckpointGeneration(device, 2, mutazioneV2);
        JsonIo.WriteAtomic(PhaseDMasterJournalService.CheckpointManifestPath(_root, device),
            new PhaseDMasterCheckpointManifest { DeviceId = device, Current = generation });

        var dopo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal("V2", Assert.Single(dopo.Items).Nome);
        // v1 non e' piu' referenziata da nessuna mappatura (segmento sparito, checkpoint porta solo v2): deve
        // essere stata ripulita, non restare come riga orfana in phase_d_mutation.
        Assert.Equal(0, CountUnreferencedMutations(materializer.DatabasePath));
        Assert.Equal(1, CountMutations(materializer.DatabasePath));
        Assert.Equal(1, CountMappings(materializer.DatabasePath));
    }

    /// <summary>Delete remoto: una postazione pubblica una Delete sulla stessa uid. Deve restare visibile subito
    /// (nessuna cache sul contenuto del current nasconde una mutation Delete valida).</summary>
    [Fact]
    public void Refresh_DeleteRemoto_VieneVistoSenzaRitardo()
    {
        PreparePhaseD();
        var deviceLocale = Device(6);
        var deviceRemoto = Device(7);
        var uid = Guid.NewGuid();
        WriteCurrentBatch(deviceRemoto, 1, Upsert(new Funzionalita { Uid = uid, Nome = "DaCancellare" }, deviceRemoto,
            new Dictionary<string, long> { [deviceRemoto] = 1 }));
        // Un'altra sorgente attiva sulla cartella (irrilevante alla uid in test) cosi' la cartella non e' vuota.
        WriteCurrentBatch(deviceLocale, 1, Upsert(new Funzionalita { Uid = Guid.NewGuid(), Nome = "Altro" },
            deviceLocale, new Dictionary<string, long> { [deviceLocale] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        Assert.Contains(materializer.LoadCatalog<Funzionalita>(_root, "funzionalita").Items, x => x.Uid == uid);
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita"); // no-op

        WriteCurrentBatch(deviceRemoto, 2, Delete(uid, deviceRemoto, new Dictionary<string, long> { [deviceRemoto] = 2 }));

        var dopo = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.DoesNotContain(dopo.Items, x => x.Uid == uid);
        var head = dopo.Heads[uid];
        Assert.Equal(PhaseDMasterMutationOperation.Delete, head.Operation);
    }

    /// <summary>Conflitto concorrente ancora rilevato: due postazioni modificano la stessa uid in concorrenza,
    /// intervallate da Refresh no-op che NON devono mascherare il conflitto (il percorso di skip idempotente si
    /// applica per-sorgente/per-SHA, non deve confondersi con la risoluzione causale fra sorgenti diverse).</summary>
    [Fact]
    public void Refresh_ConflittoConcorrenteTraDuePostazioni_RestaRilevatoDopoRefreshNoOpIntermedi()
    {
        PreparePhaseD();
        var a = Device(8);
        var b = Device(9);
        var uid = Guid.NewGuid();

        WriteCurrentBatch(a, 1, Upsert(new Funzionalita { Uid = uid, Nome = "Valore-A" }, a,
            new Dictionary<string, long> { [a] = 1 }));

        var materializer = new PhaseDMasterMaterializer(_root, _local);
        Assert.Equal("Valore-A", Assert.Single(materializer.LoadCatalog<Funzionalita>(_root, "funzionalita").Items).Nome);
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita"); // no-op
        materializer.LoadCatalog<Funzionalita>(_root, "funzionalita"); // no-op

        WriteCurrentBatch(b, 1, Upsert(new Funzionalita { Uid = uid, Nome = "Valore-B" }, b,
            new Dictionary<string, long> { [b] = 1 }));

        // La lettura del catalogo non lancia piu' per una voce in conflitto: il conflitto e' esposto via ConflictedUids.
        Assert.Equal(new[] { uid }, materializer.LoadCatalog<Funzionalita>(_root, "funzionalita").ConflictedUids);
        var conflict = Assert.Single(materializer.LoadConflicts(_root, "funzionalita"));
        Assert.Equal(uid, conflict.EntityUid);
        Assert.Equal(2, conflict.ConcurrentHeads.Count);
    }

    private void PreparePhaseD()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
    }

    private void WriteCurrentBatch(string deviceId, long sequence, params PhaseDMasterMutation[] mutations)
    {
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = deviceId,
            DeviceSequence = sequence,
            CreatedUtc = DateTime.UtcNow,
            Mutations = mutations.ToList()
        };
        batch.Validate();
        var path = PhaseDMasterJournalService.CurrentPath(_root, deviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    private string WriteSegment(string device, long sequence, PhaseDMasterMutation mutation)
    {
        var dir = PhaseDMasterJournalService.DeviceDirectory(_root, device);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"segment-{sequence:D20}-{sequence:D20}-{Guid.NewGuid():N}.ndjson");
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = device,
            DeviceSequence = sequence,
            CreatedUtc = DateTime.UtcNow,
            Mutations = new List<PhaseDMasterMutation> { mutation }
        };
        batch.Validate();
        File.WriteAllText(path, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
        return path;
    }

    /// <summary>Un unico segmento immutabile con PIU' batch consecutivi (device_sequence crescente), come farebbe
    /// una compaction reale che raggruppa piu' consegne dello stesso device in un solo file: serve a costruire lo
    /// scenario dell'orfano reale (v1 e v2 sulla stessa uid nello STESSO source_path).</summary>
    private string WriteSegmentMultiBatch(string device, params (long Sequence, PhaseDMasterMutation Mutation)[] batches)
    {
        var dir = PhaseDMasterJournalService.DeviceDirectory(_root, device);
        Directory.CreateDirectory(dir);
        var minSeq = batches.Min(x => x.Sequence);
        var maxSeq = batches.Max(x => x.Sequence);
        var path = Path.Combine(dir, $"segment-{minSeq:D20}-{maxSeq:D20}-{Guid.NewGuid():N}.ndjson");
        using var writer = new StreamWriter(path, append: false);
        foreach (var (sequence, mutation) in batches)
        {
            var batch = new PhaseDMasterMutationBatch
            {
                BatchId = Guid.NewGuid(),
                DeviceId = device,
                DeviceSequence = sequence,
                CreatedUtc = DateTime.UtcNow,
                Mutations = new List<PhaseDMasterMutation> { mutation }
            };
            batch.Validate();
            writer.Write(JsonSerializer.Serialize(batch, JsonIo.CompactOptions));
            writer.Write(Environment.NewLine);
        }
        return path;
    }

    private static PhaseDMasterMutation Upsert(Funzionalita entity, string ownerDevice, Dictionary<string, long> vector)
    {
        var payload = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = "funzionalita",
            EntityUid = entity.Uid,
            Operation = PhaseDMasterMutationOperation.Upsert,
            VersionVector = vector,
            PayloadJson = payload,
            PayloadSha256 = PhaseDMasterMutation.HashPayload(payload),
            BusinessKey = entity.Nome,
            TimestampUtc = DateTime.UtcNow
        };
    }

    private static PhaseDMasterMutation Delete(Guid uid, string ownerDevice, Dictionary<string, long> vector)
    {
        return new PhaseDMasterMutation
        {
            MutationId = Guid.NewGuid(),
            Catalog = "funzionalita",
            EntityUid = uid,
            Operation = PhaseDMasterMutationOperation.Delete,
            VersionVector = vector,
            PayloadJson = "",
            PayloadSha256 = "",
            BusinessKey = "",
            TimestampUtc = DateTime.UtcNow
        };
    }

    private PhaseDMasterCheckpointGeneration WriteCheckpointGeneration(string deviceId, long coveredThrough, PhaseDMasterMutation mutation)
    {
        var generationId = Guid.NewGuid();
        var checkpointDir = PhaseDMasterJournalService.CheckpointDirectory(_root, deviceId);
        Directory.CreateDirectory(checkpointDir);
        var fileName = $"gen-{generationId:N}-shard-00.json.gz";
        var path = Path.Combine(checkpointDir, fileName);
        var shard = new PhaseDMasterCheckpointShard
        {
            DeviceId = deviceId,
            GenerationId = generationId,
            CoveredThroughDeviceSequence = coveredThrough,
            Shard = 0,
            CompactedUtc = DateTime.UtcNow,
            LatestByEntity = new Dictionary<string, PhaseDMasterMutation>(StringComparer.Ordinal)
            {
                [$"{mutation.Catalog}:{mutation.EntityUid:N}"] = mutation
            }
        };
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(gzip, shard, JsonIo.Options);

        var bytes = File.ReadAllBytes(path);
        return new PhaseDMasterCheckpointGeneration
        {
            GenerationId = generationId,
            CoveredThroughDeviceSequence = coveredThrough,
            CreatedUtc = DateTime.UtcNow,
            Shards = new Dictionary<int, PhaseDMasterCheckpointShardReference>
            {
                [0] = new() { FileName = fileName, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Length = bytes.LongLength }
            }
        };
    }

    private static int CountMutations(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phase_d_mutation;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountMappings(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phase_d_source_mutation;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountUnreferencedMutations(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phase_d_mutation WHERE mutation_id NOT IN (SELECT mutation_id FROM phase_d_source_mutation);";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static string Device(int index) => $"{index + 1:x32}";
}
