using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

public enum PhaseDMasterJournalSourceKind
{
    CheckpointShard,
    ImmutableSegment,
    Current
}

public sealed record PhaseDMasterJournalSource(
    string Path,
    string DeviceId,
    PhaseDMasterJournalSourceKind Kind,
    string? ExpectedSha256 = null,
    long ExpectedLength = 0,
    Guid CheckpointGenerationId = default,
    int CheckpointShard = -1,
    long CoveredThroughDeviceSequence = 0);

public sealed record PhaseDMasterJournalSourceRead(
    IReadOnlyList<PhaseDMasterMutation> Mutations,
    bool Complete,
    string Sha256,
    long Length,
    long MaxDeviceSequence,
    IReadOnlyDictionary<Guid, long> DeviceSequenceByMutation);

/// <summary>
/// Reader per-file del materializer Phase D. I checkpoint sono generazionali: viene selezionata current solo
/// quando tutti gli shard referenziati sono materializzati con la dimensione attesa; altrimenti si usa previous.
/// Il contenuto viene poi verificato con SHA-256 prima di essere accettato. Segmenti e checkpoint sono strict;
/// current.ndjson puo esporre soltanto il prefisso di righe complete prima di una coda finale parziale.
///
/// Ogni mutation viene inoltre associata alla DeviceSequence del batch che la trasporta. Il materializer locale
/// usa questa informazione per trattenere contribution gia osservate quando OneDrive consegna una delete prima
/// del checkpoint che le incorpora, e le elimina soltanto quando CoveredThroughDeviceSequence ne prova la
/// copertura. Nessuna euristica mtime/length diventa quindi una prova di causalita'.
/// </summary>
public static class PhaseDMasterJournalSourceReader
{
    private static readonly JsonSerializerOptions Compact = new(JsonIo.Options) { WriteIndented = false };

    public static IReadOnlyList<PhaseDMasterJournalSource> Enumerate(string root)
    {
        var journalRoot = PhaseDMasterJournalService.JournalRoot(root);
        if (!Directory.Exists(journalRoot)) return Array.Empty<PhaseDMasterJournalSource>();

        var result = new List<PhaseDMasterJournalSource>();
        foreach (var deviceDir in Directory.EnumerateDirectories(journalRoot)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var deviceId = Path.GetFileName(deviceDir);
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new InvalidDataException("Directory device Phase D senza identificativo.");

            result.AddRange(EnumerateCheckpointGeneration(root, deviceId));

            foreach (var file in Directory.EnumerateFiles(deviceDir, "segment-*.ndjson", SearchOption.TopDirectoryOnly)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                result.Add(new PhaseDMasterJournalSource(file, deviceId, PhaseDMasterJournalSourceKind.ImmutableSegment));

            var current = PhaseDMasterJournalService.CurrentPath(root, deviceId);
            if (File.Exists(current))
                result.Add(new PhaseDMasterJournalSource(current, deviceId, PhaseDMasterJournalSourceKind.Current));
        }
        return result;
    }

    public static PhaseDMasterJournalSourceRead Read(PhaseDMasterJournalSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > int.MaxValue)
                throw new InvalidDataException("Sorgente Phase D troppo grande per il formato bounded previsto.");
            if (source.ExpectedLength > 0 && stream.Length != source.ExpectedLength)
                throw new InvalidDataException("Shard Phase D non ancora integralmente materializzato: dimensione diversa dal manifest.");
            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Sorgente Phase D non leggibile: {Path.GetFileName(source.Path)}", ex);
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.IsNullOrWhiteSpace(source.ExpectedSha256) &&
            !sha.Equals(source.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 shard Phase D diverso dal manifest: {Path.GetFileName(source.Path)}");

        return source.Kind switch
        {
            PhaseDMasterJournalSourceKind.CheckpointShard => ReadCheckpoint(source, bytes, sha),
            PhaseDMasterJournalSourceKind.ImmutableSegment => ReadNdjson(source, bytes, sha, allowTrailingPartial: false),
            PhaseDMasterJournalSourceKind.Current => ReadNdjson(source, bytes, sha, allowTrailingPartial: true),
            _ => throw new InvalidDataException("Tipo sorgente Phase D sconosciuto.")
        };
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static IReadOnlyList<PhaseDMasterMutation> ReadAll(string root)
    {
        var mutations = new Dictionary<Guid, PhaseDMasterMutation>();
        foreach (var source in Enumerate(root))
        {
            var read = Read(source);
            if (source.Kind != PhaseDMasterJournalSourceKind.Current && !read.Complete)
                throw new InvalidDataException($"Sorgente Phase D non completa: {Path.GetFileName(source.Path)}");
            foreach (var mutation in read.Mutations) AddMutation(mutations, mutation);
        }
        return mutations.Values.OrderBy(x => x.TimestampUtc).ThenBy(x => x.MutationId).ToList();
    }

    private static IReadOnlyList<PhaseDMasterJournalSource> EnumerateCheckpointGeneration(string root, string deviceId)
    {
        var manifestPath = PhaseDMasterJournalService.CheckpointManifestPath(root, deviceId);
        if (!File.Exists(manifestPath)) return Array.Empty<PhaseDMasterJournalSource>();

        var manifest = JsonIo.TryRead<PhaseDMasterCheckpointManifest>(manifestPath, out var error)
            ?? throw new InvalidDataException($"Manifest checkpoint Phase D non leggibile: {error}");
        if (manifest.SchemaVersion != 1 || !manifest.DeviceId.Equals(deviceId, StringComparison.Ordinal))
            throw new InvalidDataException("Manifest checkpoint Phase D incompatibile o attribuito al device sbagliato.");

        var selected = QuickGenerationAvailable(root, deviceId, manifest.Current)
            ? manifest.Current
            : QuickGenerationAvailable(root, deviceId, manifest.Previous)
                ? manifest.Previous
                : null;
        if (selected == null)
            throw new InvalidDataException($"Nessuna generazione checkpoint Phase D materializzata per device {deviceId}.");

        var checkpointDir = PhaseDMasterJournalService.CheckpointDirectory(root, deviceId);
        return selected.Shards.OrderBy(x => x.Key)
            .Select(pair => new PhaseDMasterJournalSource(
                Path.Combine(checkpointDir, pair.Value.FileName),
                deviceId,
                PhaseDMasterJournalSourceKind.CheckpointShard,
                pair.Value.Sha256,
                pair.Value.Length,
                selected.GenerationId,
                pair.Key,
                selected.CoveredThroughDeviceSequence))
            .ToList();
    }

    private static bool QuickGenerationAvailable(
        string root,
        string deviceId,
        PhaseDMasterCheckpointGeneration? generation)
    {
        if (generation == null || generation.GenerationId == Guid.Empty ||
            generation.CoveredThroughDeviceSequence <= 0 || generation.Shards == null)
            return false;

        var checkpointDir = PhaseDMasterJournalService.CheckpointDirectory(root, deviceId);
        foreach (var pair in generation.Shards)
        {
            var reference = pair.Value;
            if (pair.Key < 0 || pair.Key >= PhaseDMasterJournalService.CheckpointShardCount ||
                reference == null || string.IsNullOrWhiteSpace(reference.FileName) ||
                Path.GetFileName(reference.FileName) != reference.FileName ||
                string.IsNullOrWhiteSpace(reference.Sha256)) return false;
            var path = Path.Combine(checkpointDir, reference.FileName);
            if (!File.Exists(path)) return false;
            if (reference.Length > 0)
            {
                try { if (new FileInfo(path).Length != reference.Length) return false; }
                catch { return false; }
            }
        }
        return true;
    }

    private static PhaseDMasterJournalSourceRead ReadCheckpoint(
        PhaseDMasterJournalSource source,
        byte[] bytes,
        string sha)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var shard = JsonSerializer.Deserialize<PhaseDMasterCheckpointShard>(gzip, JsonIo.Options)
                ?? throw new InvalidDataException("Checkpoint shard Phase D vuoto.");
            if (shard.SchemaVersion != 1 ||
                !shard.DeviceId.Equals(source.DeviceId, StringComparison.Ordinal) ||
                source.CheckpointGenerationId == Guid.Empty ||
                shard.GenerationId != source.CheckpointGenerationId ||
                shard.CoveredThroughDeviceSequence != source.CoveredThroughDeviceSequence ||
                shard.Shard != source.CheckpointShard ||
                shard.LatestByEntity == null)
                throw new InvalidDataException("Checkpoint shard Phase D incompatibile con il manifest selezionato.");

            var result = new List<PhaseDMasterMutation>();
            var sequenceByMutation = new Dictionary<Guid, long>();
            foreach (var pair in shard.LatestByEntity)
            {
                var mutation = pair.Value ?? throw new InvalidDataException("Mutation checkpoint Phase D nulla.");
                ValidateOwnedMutation(source.DeviceId, mutation);
                var expectedKey = mutation.Catalog + ":" + mutation.EntityUid.ToString("N");
                if (!expectedKey.Equals(pair.Key, StringComparison.Ordinal))
                    throw new InvalidDataException("Indice checkpoint Phase D non coerente con la mutation contenuta.");
                result.Add(mutation);
                sequenceByMutation[mutation.MutationId] = shard.CoveredThroughDeviceSequence;
            }
            return new PhaseDMasterJournalSourceRead(
                result, Complete: true, sha, bytes.LongLength,
                shard.CoveredThroughDeviceSequence, sequenceByMutation);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidDataException($"Checkpoint Phase D non leggibile: {Path.GetFileName(source.Path)}", ex);
        }
    }

    private static PhaseDMasterJournalSourceRead ReadNdjson(
        PhaseDMasterJournalSource source,
        byte[] bytes,
        string sha,
        bool allowTrailingPartial)
    {
        var result = new List<PhaseDMasterMutation>();
        var sequenceByMutation = new Dictionary<Guid, long>();
        var maxSequence = 0L;
        var complete = true;
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            PhaseDMasterMutationBatch? batch;
            try { batch = JsonSerializer.Deserialize<PhaseDMasterMutationBatch>(line, Compact); }
            catch (JsonException) when (allowTrailingPartial && i == lines.Length - 1 && bytes.Length > 0 && bytes[^1] != (byte)'\n')
            {
                complete = false;
                break;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Sorgente Phase D contiene una riga JSON non valida/non finale.", ex);
            }

            if (batch == null) throw new InvalidDataException("Batch Phase D nullo.");
            batch.Validate();
            if (!batch.DeviceId.Equals(source.DeviceId, StringComparison.Ordinal))
                throw new InvalidDataException($"Batch Phase D attribuito a '{batch.DeviceId}' trovato nel journal di '{source.DeviceId}'.");
            maxSequence = Math.Max(maxSequence, batch.DeviceSequence);
            foreach (var mutation in batch.Mutations)
            {
                ValidateOwnedMutation(source.DeviceId, mutation);
                result.Add(mutation);
                if (sequenceByMutation.TryGetValue(mutation.MutationId, out var oldSequence))
                    sequenceByMutation[mutation.MutationId] = Math.Max(oldSequence, batch.DeviceSequence);
                else
                    sequenceByMutation[mutation.MutationId] = batch.DeviceSequence;
            }
        }

        if (!allowTrailingPartial && !complete)
            throw new InvalidDataException("Segmento Phase D non integralmente leggibile.");
        return new PhaseDMasterJournalSourceRead(result, complete, sha, bytes.LongLength, maxSequence, sequenceByMutation);
    }

    private static void ValidateOwnedMutation(string deviceId, PhaseDMasterMutation mutation)
    {
        mutation.Validate();
        if (!mutation.VersionVector.TryGetValue(deviceId, out var ownCounter) || ownCounter <= 0)
            throw new InvalidDataException($"Mutation Phase D nel journal '{deviceId}' senza counter causale del device proprietario.");
    }

    private static void AddMutation(Dictionary<Guid, PhaseDMasterMutation> target, PhaseDMasterMutation mutation)
    {
        if (!target.TryGetValue(mutation.MutationId, out var existing))
        {
            target.Add(mutation.MutationId, mutation);
            return;
        }

        var a = JsonSerializer.SerializeToUtf8Bytes(existing, Compact);
        var b = JsonSerializer.SerializeToUtf8Bytes(mutation, Compact);
        if (!a.AsSpan().SequenceEqual(b))
            throw new InvalidDataException($"MutationId Phase D duplicato con contenuto differente: {mutation.MutationId:N}");
    }
}
