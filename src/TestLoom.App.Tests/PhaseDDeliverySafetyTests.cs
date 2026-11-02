using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Regressioni per gli stati transitori tipici di OneDrive/SharePoint: coda current parziale, delete di un
/// segmento consegnata prima del checkpoint, duplicati/fork tecnici. Il DB SQLite usato qui è soltanto una
/// lente di verifica del read-model locale: nessuna asserzione lo tratta come source of truth condivisa.
/// </summary>
public sealed class PhaseDDeliverySafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_Delivery_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_PhaseD_DeliveryLocal_" + Guid.NewGuid().ToString("N"));

    public PhaseDDeliverySafetyTests()
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
    public void Current_TrailingPartialLine_EsponeSoloPrefissoCompleto()
    {
        PreparePhaseD();
        var device = Device(1);
        var entity = new Funzionalita { Uid = Guid.NewGuid(), Nome = "Completa" };
        var batch = Batch(device, 1, Upsert(entity, device, new Dictionary<string, long> { [device] = 1 }));
        var path = PhaseDMasterJournalService.CurrentPath(_root, device);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine +
            "{\"SchemaVersion\":1,\"BatchId\":\"");

        var read = PhaseDMasterJournalSourceReader.Read(new PhaseDMasterJournalSource(
            path, device, PhaseDMasterJournalSourceKind.Current));

        Assert.False(read.Complete);
        var mutation = Assert.Single(read.Mutations);
        Assert.Equal(entity.Uid, mutation.EntityUid);
        Assert.Equal(1, read.MaxDeviceSequence);
    }

    [Fact]
    public void SegmentoSparitoPrimaDelCheckpoint_RestaVisibileFincheLaCoperturaNonArriva()
    {
        PreparePhaseD();
        var device = Device(2);
        var entity = new Funzionalita { Uid = Guid.NewGuid(), Nome = "Non perdere" };
        var mutation = Upsert(entity, device, new Dictionary<string, long> { [device] = 1 });
        var segment = WriteSegment(device, 1, mutation);
        var materializer = new PhaseDMasterMaterializer(_root, _local);

        var first = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(entity.Uid, Assert.Single(first.Items).Uid);
        Assert.Equal(1, CountNonCheckpointSources(materializer.DatabasePath, device));

        // Simula la replica che riceve prima la delete del segmento generata dalla compaction remota.
        File.Delete(segment);
        var duringGap = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(entity.Uid, Assert.Single(duringGap.Items).Uid);
        Assert.Equal(1, CountNonCheckpointSources(materializer.DatabasePath, device));

        // Solo un checkpoint integro che dichiara di coprire sequence=1 autorizza a dimenticare il segmento.
        var generation = WriteCheckpointGeneration(device, 1, mutation);
        JsonIo.WriteAtomic(PhaseDMasterJournalService.CheckpointManifestPath(_root, device),
            new PhaseDMasterCheckpointManifest { DeviceId = device, Current = generation });

        var afterCoverage = materializer.LoadCatalog<Funzionalita>(_root, "funzionalita");
        Assert.Equal(entity.Uid, Assert.Single(afterCoverage.Items).Uid);
        Assert.Equal(0, CountNonCheckpointSources(materializer.DatabasePath, device));
        Assert.Equal(1, CountMappings(materializer.DatabasePath));
    }

    [Fact]
    public void MutationIdDuplicatoConContenutoDivergente_FallisceChiuso()
    {
        PreparePhaseD();
        var device = Device(3);
        var mutationId = Guid.NewGuid();
        var uid = Guid.NewGuid();
        var first = Upsert(new Funzionalita { Uid = uid, Nome = "Versione-A" }, device,
            new Dictionary<string, long> { [device] = 1 }, mutationId);
        var divergent = Upsert(new Funzionalita { Uid = uid, Nome = "Versione-B" }, device,
            new Dictionary<string, long> { [device] = 1 }, mutationId);
        WriteSegment(device, 1, first);
        WriteCurrent(device, Batch(device, 2, divergent));

        var materializer = new PhaseDMasterMaterializer(_root, _local);

        var error = Assert.Throws<InvalidDataException>(() => materializer.Refresh(_root));
        Assert.Contains("MutationId", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BootstrapCorrotto_NonPubblicaReadyEIlGateRestaSolaLettura()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        File.WriteAllText(paths.RequisitiFile, "[{ json-corrotto");
        var original = File.ReadAllBytes(paths.RequisitiFile);
        var health = CondivisioneHealthService.Verifica(_root);
        Assert.Equal(StatoCondivisione.Operativa, health.Stato);

        var result = CompatibilitaVersioneService.Applica(paths, health, new Version(6, 1, 0));

        Assert.Equal(StatoCondivisione.SolaLettura, result.Stato);
        Assert.False(PhaseDMasterBootstrapService.IsReady(_root));
        Assert.Equal(original, File.ReadAllBytes(paths.RequisitiFile));
    }

    private void PreparePhaseD()
    {
        var paths = new DataPaths(_root);
        paths.EnsureStructure();
        PhaseDMasterBootstrapService.EnsureReady(paths);
        Assert.True(PhaseDMasterBootstrapService.IsReady(_root));
    }

    private string WriteSegment(string device, long sequence, PhaseDMasterMutation mutation)
    {
        var dir = PhaseDMasterJournalService.DeviceDirectory(_root, device);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"segment-{sequence:D20}-{sequence:D20}-{Guid.NewGuid():N}.ndjson");
        File.WriteAllText(path, JsonSerializer.Serialize(Batch(device, sequence, mutation), JsonIo.CompactOptions) + Environment.NewLine);
        return path;
    }

    private void WriteCurrent(string device, PhaseDMasterMutationBatch batch)
    {
        var path = PhaseDMasterJournalService.CurrentPath(_root, device);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(batch, JsonIo.CompactOptions) + Environment.NewLine);
    }

    private static PhaseDMasterMutationBatch Batch(string device, long sequence, params PhaseDMasterMutation[] mutations)
    {
        var batch = new PhaseDMasterMutationBatch
        {
            BatchId = Guid.NewGuid(),
            DeviceId = device,
            DeviceSequence = sequence,
            CreatedUtc = DateTime.UtcNow,
            Mutations = mutations.ToList()
        };
        batch.Validate();
        return batch;
    }

    private static PhaseDMasterMutation Upsert(
        Funzionalita entity,
        string ownerDevice,
        Dictionary<string, long> vector,
        Guid? mutationId = null)
    {
        var payload = JsonSerializer.Serialize(entity, JsonIo.CompactOptions);
        return new PhaseDMasterMutation
        {
            MutationId = mutationId ?? Guid.NewGuid(),
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

    private PhaseDMasterCheckpointGeneration WriteCheckpointGeneration(
        string deviceId,
        long coveredThrough,
        PhaseDMasterMutation mutation)
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
                [0] = new()
                {
                    FileName = fileName,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    Length = bytes.LongLength
                }
            }
        };
    }

    private static int CountNonCheckpointSources(string databasePath, string device)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phase_d_source WHERE device_id=$device AND source_kind <> $checkpoint;";
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$checkpoint", (int)PhaseDMasterJournalSourceKind.CheckpointShard);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountMappings(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phase_d_source_mutation;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string Device(int index) => $"{index:x32}";
}
