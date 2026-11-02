using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

public enum PhaseDMasterMutationOperation
{
    Upsert,
    Delete
}

/// <summary>
/// Version vector compatto per una singola entita master. Con 15 postazioni il costo e' limitato a poche
/// coppie device->counter, mentre la semantica e' molto piu forte di un revision++ globale: due mutazioni sono
/// ordinabili solo se una ha realmente osservato l'altra. Se nessuna domina l'altra, sono concorrenti.
/// </summary>
public static class PhaseDVersionVector
{
    public static Dictionary<string, long> Increment(
        IReadOnlyDictionary<string, long>? observed,
        string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var result = observed == null
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : new Dictionary<string, long>(observed, StringComparer.Ordinal);
        result.TryGetValue(deviceId, out var current);
        checked { result[deviceId] = current + 1; }
        return result;
    }

    public static Dictionary<string, long> Join(params IReadOnlyDictionary<string, long>[] vectors)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var vector in vectors)
        foreach (var pair in vector)
            if (!result.TryGetValue(pair.Key, out var existing) || pair.Value > existing)
                result[pair.Key] = pair.Value;
        return result;
    }

    public static bool Dominates(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
    {
        var strictlyGreater = false;
        foreach (var pair in right)
        {
            left.TryGetValue(pair.Key, out var leftValue);
            if (leftValue < pair.Value) return false;
            if (leftValue > pair.Value) strictlyGreater = true;
        }

        if (!strictlyGreater)
        {
            foreach (var pair in left)
            {
                right.TryGetValue(pair.Key, out var rightValue);
                if (pair.Value > rightValue)
                {
                    strictlyGreater = true;
                    break;
                }
            }
        }
        return strictlyGreater;
    }

    public static bool Equivalent(
        IReadOnlyDictionary<string, long> left,
        IReadOnlyDictionary<string, long> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out var value) || value != pair.Value)
                return false;
        return true;
    }

    public static string Fingerprint(IReadOnlyDictionary<string, long> vector)
    {
        var canonical = string.Join("\n", vector.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + "=" + x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed class PhaseDMasterMutation
{
    public int SchemaVersion { get; set; } = 1;
    public Guid MutationId { get; set; } = Guid.NewGuid();
    public string Catalog { get; set; } = "";
    public Guid EntityUid { get; set; }
    public PhaseDMasterMutationOperation Operation { get; set; }
    public Dictionary<string, long> VersionVector { get; set; } = new(StringComparer.Ordinal);
    public string PayloadJson { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public string BusinessKey { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Schema mutation Phase D non supportato: {SchemaVersion}.");
        if (MutationId == Guid.Empty) throw new InvalidDataException("MutationId Phase D vuoto.");
        if (EntityUid == Guid.Empty) throw new InvalidDataException("EntityUid Phase D vuoto.");
        if (string.IsNullOrWhiteSpace(Catalog)) throw new InvalidDataException("Catalog Phase D vuoto.");
        if (VersionVector.Count == 0 || VersionVector.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value <= 0))
            throw new InvalidDataException("Version vector Phase D non valido.");

        if (Operation == PhaseDMasterMutationOperation.Upsert)
        {
            if (string.IsNullOrWhiteSpace(PayloadJson) || string.IsNullOrWhiteSpace(PayloadSha256))
                throw new InvalidDataException("Mutation Upsert Phase D senza payload/hash.");
            if (string.IsNullOrWhiteSpace(BusinessKey))
                throw new InvalidDataException("Mutation Upsert Phase D senza chiave business.");
            var computed = HashPayload(PayloadJson);
            if (!computed.Equals(PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hash payload Phase D non coerente.");
        }
        else
        {
            if (!string.IsNullOrEmpty(PayloadJson) || !string.IsNullOrEmpty(PayloadSha256))
                throw new InvalidDataException("Mutation Delete Phase D non deve contenere payload.");
            if (!string.IsNullOrEmpty(BusinessKey))
                throw new InvalidDataException("Mutation Delete Phase D non deve dichiarare una chiave business attiva.");
        }
    }

    public static string HashPayload(string payloadJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
}

/// <summary>
/// Una riga NDJSON e' un batch atomico di mutazioni dello stesso Save master. Se OneDrive consegna una riga
/// parziale il parser strict la rifiuta integralmente; non puo' quindi esporre meta' di un salvataggio multi-riga.
/// </summary>
public sealed class PhaseDMasterMutationBatch
{
    public int SchemaVersion { get; set; } = 1;
    public Guid BatchId { get; set; } = Guid.NewGuid();
    public string DeviceId { get; set; } = "";
    public long DeviceSequence { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<PhaseDMasterMutation> Mutations { get; set; } = new();

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Schema batch Phase D non supportato: {SchemaVersion}.");
        if (BatchId == Guid.Empty) throw new InvalidDataException("BatchId Phase D vuoto.");
        if (string.IsNullOrWhiteSpace(DeviceId)) throw new InvalidDataException("DeviceId Phase D vuoto.");
        if (DeviceSequence <= 0) throw new InvalidDataException("DeviceSequence Phase D non valida.");
        if (Mutations.Count == 0) throw new InvalidDataException("Batch Phase D senza mutazioni.");
        foreach (var mutation in Mutations)
        {
            mutation.Validate();
            if (!mutation.VersionVector.TryGetValue(DeviceId, out var own) || own <= 0)
                throw new InvalidDataException("Version vector mutation non contiene il device autore del batch.");
        }
    }
}

public sealed class PhaseDMasterStoreManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string StorageMode { get; set; } = "phase-d-master-v1";
    public int CheckpointShardCount { get; set; } = 64;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Shard immutabile di UNA generazione checkpoint. Il file non viene mai sovrascritto: un nuovo
/// checkpoint crea nuovi nomi gen-*.json.gz e soltanto dopo pubblica checkpoint/manifest.json.</summary>
public sealed class PhaseDMasterCheckpointShard
{
    public int SchemaVersion { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public Guid GenerationId { get; set; }
    public long CoveredThroughDeviceSequence { get; set; }
    public int Shard { get; set; }
    public DateTime CompactedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, PhaseDMasterMutation> LatestByEntity { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PhaseDMasterCheckpointShardReference
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>Dimensione esatta del file gzip immutabile. Permette di riconoscere quasi a costo zero una
    /// generazione OneDrive ancora parzialmente materializzata e ripiegare sulla previous prima di leggerla.</summary>
    public long Length { get; set; }
}

public sealed class PhaseDMasterCheckpointGeneration
{
    public Guid GenerationId { get; set; }
    public long CoveredThroughDeviceSequence { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Dictionary<int, PhaseDMasterCheckpointShardReference> Shards { get; set; } = new();
}

/// <summary>
/// Puntatore atomico per-device. Mantiene current + previous: se OneDrive consegna manifest prima di tutti gli
/// shard della nuova generazione, un reader puo continuare sulla previous invece di perdere temporaneamente
/// stato. Al massimo due generazioni x 64 shard per device restano condivise.
/// </summary>
public sealed class PhaseDMasterCheckpointManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string DeviceId { get; set; } = "";
    public PhaseDMasterCheckpointGeneration? Current { get; set; }
    public PhaseDMasterCheckpointGeneration? Previous { get; set; }
}

public sealed record PhaseDMaterializedHead(
    string Catalog,
    Guid EntityUid,
    PhaseDMasterMutationOperation Operation,
    string BusinessKey,
    string PayloadJson,
    string PayloadSha256,
    IReadOnlyDictionary<string, long> VersionVector,
    bool HasConflict,
    IReadOnlyList<PhaseDMasterMutation> ConcurrentHeads);
