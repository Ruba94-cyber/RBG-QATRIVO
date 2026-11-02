using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>
/// Source of truth condivisa dei master Phase D. Ogni device scrive esclusivamente nel proprio journal:
/// current.ndjson -> segmenti immutabili -> checkpoint shard generazionali gzip. Due PC diversi non competono
/// quindi sullo stesso file durante le normali mutation.
///
/// La compattazione NON sovrascrive mai gli shard esistenti: crea una generazione completa con nomi nuovi,
/// pubblica per ultimo checkpoint/manifest.json e conserva anche la generazione precedente. Se OneDrive consegna
/// il manifest prima di uno shard nuovo, i reader possono restare sulla previous; se consegna prima le delete dei
/// segmenti, il materializer locale conserva le vecchie contribution finche' una generazione ne prova la copertura.
/// Il file-count resta bounded a massimo due generazioni x 64 shard per device, piu current/pochi segmenti.
/// </summary>
public static class PhaseDMasterJournalService
{
    public const int CheckpointShardCount = 64;
    private const long RotateAfterBytes = 512 * 1024;
    private const int CompactAfterSegments = 4;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions CompactJson = new(JsonIo.Options) { WriteIndented = false };

    /// <summary>Attesa massima del mutex cross-processo del journal: oltre, l'operazione fallisce senza modifiche invece di bloccarsi.</summary>
    private static readonly TimeSpan AttesaMutexJournal = TimeSpan.FromSeconds(30);

    /// <summary>File laterali `current.ndjson.torn-*` conservati (i piu' recenti): i piu' vecchi vengono eliminati.</summary>
    internal const int TornConservati = 10;

    /// <summary>Nome del mutex nominato che serializza append e riparazione della coda di UN journal fra processi diversi dello
    /// stesso PC. Derivato dal percorso completo di current.ndjson (hash SHA-256): ogni device ha il suo mutex. Local\: il journal
    /// di un device e' scritto da un solo PC, quindi basta la sessione di logon.</summary>
    internal static string NomeMutexJournal(string currentPath)
    {
        var chiave = Path.GetFullPath(currentPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chiave)));
        return @"Local\TestLoom-PhaseD-Journal-" + hash;
    }

    /// <summary>Mutex cross-processo del journal. Si acquisisce SEMPRE dentro Gate (stesso ordine ovunque: prima Gate, poi mutex).
    /// AbandonedMutexException (un processo e' morto a meta' di un append) vale come acquisizione: la coda incompleta
    /// viene poi riparata dai percorsi di lettura/append come gia' avviene.</summary>
    private sealed class LockJournalCrossProcesso : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _posseduto;

        public LockJournalCrossProcesso(string currentPath)
        {
            _mutex = new Mutex(false, NomeMutexJournal(currentPath));
            try
            {
                _posseduto = _mutex.WaitOne(AttesaMutexJournal);
                if (!_posseduto)
                    throw new IOException("Journal Phase D occupato da un altro processo: operazione annullata senza modifiche.");
            }
            catch (AbandonedMutexException)
            {
                _posseduto = true;
            }
            catch
            {
                _mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try { if (_posseduto) _mutex.ReleaseMutex(); }
            finally { _mutex.Dispose(); }
        }
    }

    public static bool IsInitialized(string root) => PhaseDMasterBootstrapService.IsReady(root);

    public static void EnsureInitialized(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Inizializzazione master Phase D");
        lock (Gate)
        {
            WorkspaceWritePolicy.EnsureCanWrite(paths, "Inizializzazione master Phase D");
            var manifestPath = ManifestPath(paths.Root);
            if (File.Exists(manifestPath))
            {
                ReadManifestRequired(paths.Root);
                return;
            }

            Directory.CreateDirectory(PhaseDRoot(paths.Root));
            Directory.CreateDirectory(JournalRoot(paths.Root));
            var manifest = new PhaseDMasterStoreManifest
            {
                SchemaVersion = 1,
                StorageMode = "phase-d-master-v1",
                CheckpointShardCount = CheckpointShardCount,
                CreatedUtc = DateTime.UnixEpoch
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonIo.Options);
            try { WriteBytesAtomicCreateOnly(manifestPath, bytes); }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(manifestPath))
            {
                var existing = File.ReadAllBytes(manifestPath);
                if (!existing.AsSpan().SequenceEqual(bytes)) ReadManifestRequired(paths.Root);
            }
        }
    }

    public static void Append(DataPaths paths, PhaseDMasterMutationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(batch);
        batch.Validate();
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Pubblicazione mutation master Phase D");
        EnsureInitialized(paths);

        lock (Gate)
        {
            WorkspaceWritePolicy.EnsureCanWrite(paths, "Pubblicazione mutation master Phase D");
            var currentDevice = DeviceIdentityService.GetOrCreate();
            if (!batch.DeviceId.Equals(currentDevice, StringComparison.Ordinal))
                throw new InvalidDataException("Un device non puo pubblicare mutation Phase D nel journal di un altro device.");

            var dir = DeviceDirectory(paths.Root, batch.DeviceId);
            Directory.CreateDirectory(dir);
            var current = CurrentPath(paths.Root, batch.DeviceId);
            // Gate serializza i thread di questo processo; il mutex serializza i processi (altra istanza TestLoom sullo stesso PC):
            // riparazione della coda e append devono vedere la coda sotto lo stesso lock, altrimenti una riga in scrittura
            // altrui viene troncata.
            using var lockCrossProcesso = new LockJournalCrossProcesso(current);
            var nonVuoto = File.Exists(current) && new FileInfo(current).Length > 0;
            if (nonVuoto && !TryReadBatchesStrict(current, batch.DeviceId, out _) &&
                !TryRiparaCodaTroncata(current, batch.DeviceId))
                throw new InvalidDataException("current.ndjson Phase D non integralmente leggibile/attribuibile: append bloccato senza modificare il file.");

            // Una coda valida ma senza newline finale va chiusa prima di accodare, altrimenti due batch finiscono sulla stessa riga.
            var chiusuraRiga = nonVuoto && !UltimoByteENewline(current) ? Environment.NewLine : "";
            var line = chiusuraRiga + JsonSerializer.Serialize(batch, CompactJson) + Environment.NewLine;
            using (var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read,
                       64 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            RotateIfNeeded(paths, batch.DeviceId);
            TryCompact(paths, batch.DeviceId);
        }
    }

    public static IReadOnlyList<PhaseDMasterMutation> ReadAllMutations(string root) =>
        PhaseDMasterJournalSourceReader.ReadAll(root);

    public static long NextDeviceSequence(string root, string? localBaseDirectory = null)
    {
        var deviceId = DeviceIdentityService.GetOrCreate();
        var baseDir = localBaseDirectory ?? Path.Combine(
            WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "master-v2-sequence");
        // Stessa chiave di LocalTestIndexStore (pinned all'apertura o canonica). Il contatore scritto prima della 6.7.1 sotto la
        // chiave grezza NON viene spostato: resta in sola lettura e fa da ulteriore limite inferiore (vedi sotto), cosi' una
        // DeviceSequence ancora in volo nel vecchio contatore non puo' essere riassegnata.
        var workspaceKey = WorkspaceLocalPaths.ComputeWorkspaceKey(root);
        var legacyDir = WorkspaceLocalPaths.CartellaLegacyDaLeggere(baseDir, root);
        var dir = Path.Combine(baseDir, workspaceKey, deviceId);
        Directory.CreateDirectory(dir);
        var sequencePath = Path.Combine(dir, "sequence.txt");
        var lockPath = Path.Combine(dir, "sequence.lock");

        // Il contatore locale e' indicizzato per hash del PERCORSO della cartella (workspaceKey): se lo stesso
        // device apre la stessa cartella dati condivisa da un percorso diverso (copia/spostamento/rinomina/zip
        // riestratto/OneDrive "Cartella(1)"), questo contatore locale riparte da zero pur essendo il journal
        // condiviso gia' avanti per questo stesso deviceId. Il floor sotto forza la sequenza ad essere sempre
        // strettamente maggiore di quanto questo device ha gia' pubblicato nel journal condiviso della cartella
        // (checkpoint incluso), cosi' il contatore locale non puo mai riassegnare un DeviceSequence gia' usato.
        // La lettura del journal avviene sotto Gate, lo stesso lock che serializza Append/rotazione/compattazione:
        // senza, un'altra thread dello stesso processo che sta appendendo (FileShare.Read sul writer, riga ancora
        // parziale) faceva fallire la lettura strict con un InvalidDataException/IOException spurio pur essendo il
        // journal integro. Il lock e' un Monitor (rientrante) e non e' mai preso mentre si attende il lock file.
        long journalFloor;
        lock (Gate)
        {
            // Stesso mutex cross-processo di Append: la riparazione della coda in lettura non deve competere con un append altrui.
            using (new LockJournalCrossProcesso(CurrentPath(root, deviceId)))
                journalFloor = ResolveJournalFloorForThisDevice(root, deviceId);
        }

        Exception? ultimoErroreLock = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            FileStream? lockStream = null;
            var acquired = false;
            try
            {
                lockStream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.WriteThrough);
                acquired = true;
                long current = 0;
                if (File.Exists(sequencePath))
                {
                    var text = File.ReadAllText(sequencePath).Trim();
                    if (!long.TryParse(text, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out current) || current < 0)
                        throw new InvalidDataException("Contatore locale Phase D corrotto.");
                }
                // Il contatore non puo' mai tornare indietro: parte dal massimo tra cio' che il file locale
                // ricorda, cio' che il journal condiviso della cartella dice gia' pubblicato da questo device e il
                // contatore legacy (chiave grezza, prima della 6.7.1), se presente.
                var baseline = Math.Max(Math.Max(current, journalFloor), LeggiContatoreLegacy(legacyDir, deviceId));
                var next = checked(baseline + 1);
                WriteTextAtomic(sequencePath, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return next;
            }
            catch (Exception ex) when (!acquired && (ex is IOException || ex is UnauthorizedAccessException)
                                       && !ErroreLockNonTransitorio(ex))
            // Anche se il lock e' gia' sparito, o su Windows e' in delete-pending (UnauthorizedAccessException)
            // perche' l'holder lo sta eliminando, si ritenta: il limite di tentativi sotto rende il fallimento
            // definitivo e bounded. Disco pieno / percorso inesistente non si risolvono ritentando: escono subito
            // con l'eccezione originale (review 2026-10-01), e l'ultima causa resta come InnerException.
            {
                ultimoErroreLock = ex;
                Thread.Sleep(25);
            }
            finally
            {
                try { lockStream?.Dispose(); } catch { }
                if (acquired)
                {
                    try { if (File.Exists(lockPath)) File.Delete(lockPath); } catch { }
                }
            }
        }
        throw new IOException("Impossibile acquisire il lock locale del contatore mutation Phase D.", ultimoErroreLock);
    }

    /// <summary>Errori del lock file del contatore che ritentare non risolve: disco pieno e percorso non
    /// trovato (HRESULT Win32 0x70, 0x27, 0x03).</summary>
    internal static bool ErroreLockNonTransitorio(Exception ex) =>
        ex is DirectoryNotFoundException
        || (ex.HResult & 0xFFFF) is 0x70 or 0x27 or 0x03;

    public static string PhaseDRoot(string root) => Path.Combine(root, "_ditm", "master-v2");
    public static string ManifestPath(string root) => Path.Combine(PhaseDRoot(root), "manifest.json");
    public static string JournalRoot(string root) => Path.Combine(PhaseDRoot(root), "journal");
    public static string DeviceDirectory(string root, string deviceId) => Path.Combine(JournalRoot(root), deviceId);
    public static string CurrentPath(string root, string deviceId) => Path.Combine(DeviceDirectory(root, deviceId), "current.ndjson");
    public static string CheckpointDirectory(string root, string deviceId) => Path.Combine(DeviceDirectory(root, deviceId), "checkpoint");
    public static string CheckpointManifestPath(string root, string deviceId) => Path.Combine(CheckpointDirectory(root, deviceId), "manifest.json");

    internal static PhaseDMasterCheckpointGeneration? ResolveReadableCheckpointGeneration(string root, string deviceId)
    {
        var path = CheckpointManifestPath(root, deviceId);
        if (!File.Exists(path)) return null;
        var manifest = ReadCheckpointManifestRequired(path, deviceId);
        if (manifest.Current != null && TryValidateGeneration(root, deviceId, manifest.Current, out _)) return manifest.Current;
        if (manifest.Previous != null && TryValidateGeneration(root, deviceId, manifest.Previous, out _)) return manifest.Previous;
        throw new InvalidDataException($"Nessuna generazione checkpoint Phase D integralmente disponibile per device {deviceId}.");
    }

    internal static IReadOnlyList<PhaseDMasterMutation> ReadReadableCheckpointMutations(string root, string deviceId)
    {
        var path = CheckpointManifestPath(root, deviceId);
        if (!File.Exists(path)) return Array.Empty<PhaseDMasterMutation>();
        var manifest = ReadCheckpointManifestRequired(path, deviceId);
        if (manifest.Current != null && TryValidateGeneration(root, deviceId, manifest.Current, out var current)) return current;
        if (manifest.Previous != null && TryValidateGeneration(root, deviceId, manifest.Previous, out var previous)) return previous;
        throw new InvalidDataException($"Checkpoint Phase D del device {deviceId} non ricostruibile ne' dalla current ne' dalla previous generation.");
    }

    /// <summary>
    /// Limite inferiore economico della DeviceSequence gia' pubblicata da <paramref name="deviceId"/> nel
    /// journal CONDIVISO della cartella <paramref name="root"/>: massimo tra CoveredThroughDeviceSequence del
    /// checkpoint del device (current e previous, letti dal solo manifest.json, senza rimaterializzare gli
    /// shard: qui serve solo il numero, non il contenuto) e la DeviceSequence piu' alta nei segmenti immutabili
    /// e in current.ndjson dello stesso device. Costo bounded: manifest.json e' piccolo, i segmenti restano
    /// pochi grazie alla compattazione periodica e current.ndjson e' limitato a RotateAfterBytes (~512KB).
    /// Usato da NextDeviceSequence per evitare che il contatore LOCALE (indicizzato per percorso) riassegni una
    /// DeviceSequence gia' presente nel journal condiviso quando la stessa cartella dati viene copiata,
    /// spostata, rinominata o rieestratta su un percorso diverso sullo stesso PC.
    /// </summary>
    private static long ResolveJournalFloorForThisDevice(string root, string deviceId)
    {
        long floor = 0;

        var manifestPath = CheckpointManifestPath(root, deviceId);
        if (File.Exists(manifestPath))
        {
            var manifest = ReadCheckpointManifestRequired(manifestPath, deviceId);
            if (manifest.Current != null) floor = Math.Max(floor, manifest.Current.CoveredThroughDeviceSequence);
            if (manifest.Previous != null) floor = Math.Max(floor, manifest.Previous.CoveredThroughDeviceSequence);
        }

        var dir = DeviceDirectory(root, deviceId);
        if (!Directory.Exists(dir)) return floor;

        foreach (var segment in Directory.EnumerateFiles(dir, "segment-*.ndjson", SearchOption.TopDirectoryOnly))
        {
            if (!TryReadBatchesStrict(segment, deviceId, out var batches))
                throw new InvalidDataException(
                    $"Segmento Phase D del device {deviceId} non leggibile: impossibile calcolare in sicurezza la prossima DeviceSequence.");
            foreach (var batch in batches) floor = Math.Max(floor, batch.DeviceSequence);
        }

        var current = CurrentPath(root, deviceId);
        if (File.Exists(current) && new FileInfo(current).Length > 0)
        {
            // Stessa riparazione della coda di Append (6.7.1): senza, una coda troncata bloccherebbe la sequenza prima dell'append.
            if (!TryReadBatchesStrict(current, deviceId, out var batches) &&
                (!TryRiparaCodaTroncata(current, deviceId) || !TryReadBatchesStrict(current, deviceId, out batches)))
                throw new InvalidDataException(
                    $"current.ndjson Phase D del device {deviceId} non leggibile: impossibile calcolare in sicurezza la prossima DeviceSequence.");
            foreach (var batch in batches) floor = Math.Max(floor, batch.DeviceSequence);
        }

        return floor;
    }

    private static void RotateIfNeeded(DataPaths paths, string deviceId)
    {
        var current = CurrentPath(paths.Root, deviceId);
        if (!File.Exists(current) || new FileInfo(current).Length < RotateAfterBytes) return;
        if (!TryReadBatchesStrict(current, deviceId, out var batches) || batches.Count == 0)
            throw new InvalidDataException("Rotazione Phase D bloccata: current.ndjson non integralmente leggibile.");

        var first = batches.Min(x => x.DeviceSequence);
        var last = batches.Max(x => x.DeviceSequence);
        var segment = Path.Combine(DeviceDirectory(paths.Root, deviceId),
            $"segment-{first:D20}-{last:D20}-{Guid.NewGuid():N}.ndjson");
        File.Move(current, segment, overwrite: false);
    }

    private static void TryCompact(DataPaths paths, string deviceId)
    {
        var dir = DeviceDirectory(paths.Root, deviceId);
        var segments = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "segment-*.ndjson", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        if (segments.Count < CompactAfterSegments) return;

        var checkpointManifestPath = CheckpointManifestPath(paths.Root, deviceId);
        PhaseDMasterCheckpointManifest? oldManifest = null;
        PhaseDMasterCheckpointGeneration? oldReadable = null;
        var latest = new Dictionary<string, PhaseDMasterMutation>(StringComparer.Ordinal);
        long coveredThrough = 0;
        if (File.Exists(checkpointManifestPath))
        {
            oldManifest = ReadCheckpointManifestRequired(checkpointManifestPath, deviceId);
            if (oldManifest.Current != null && TryValidateGeneration(paths.Root, deviceId, oldManifest.Current, out var currentMutations))
            {
                oldReadable = oldManifest.Current;
                coveredThrough = oldReadable.CoveredThroughDeviceSequence;
                foreach (var mutation in currentMutations) MergeLatestForDevice(latest, deviceId, mutation);
            }
            else if (oldManifest.Previous != null && TryValidateGeneration(paths.Root, deviceId, oldManifest.Previous, out var previousMutations))
            {
                oldReadable = oldManifest.Previous;
                coveredThrough = oldReadable.CoveredThroughDeviceSequence;
                foreach (var mutation in previousMutations) MergeLatestForDevice(latest, deviceId, mutation);
            }
            else
            {
                throw new InvalidDataException("Compattazione Phase D bloccata: nessuna generazione checkpoint precedente e' integralmente leggibile.");
            }
        }

        foreach (var segment in segments)
        {
            if (!TryReadBatchesStrict(segment, deviceId, out var batches))
                throw new InvalidDataException($"Compattazione Phase D bloccata: segmento non leggibile {Path.GetFileName(segment)}.");
            foreach (var batch in batches)
            {
                coveredThrough = Math.Max(coveredThrough, batch.DeviceSequence);
                foreach (var mutation in batch.Mutations) MergeLatestForDevice(latest, deviceId, mutation);
            }
        }

        var generationId = Guid.NewGuid();
        var checkpointDir = CheckpointDirectory(paths.Root, deviceId);
        Directory.CreateDirectory(checkpointDir);
        var refs = new Dictionary<int, PhaseDMasterCheckpointShardReference>();
        foreach (var group in latest.Values.GroupBy(x => ShardOf(x.Catalog, x.EntityUid)))
        {
            var shard = new PhaseDMasterCheckpointShard
            {
                SchemaVersion = 1,
                DeviceId = deviceId,
                GenerationId = generationId,
                CoveredThroughDeviceSequence = coveredThrough,
                Shard = group.Key,
                CompactedUtc = DateTime.UtcNow,
                LatestByEntity = group.ToDictionary(EntityKey, x => x, StringComparer.Ordinal)
            };
            var fileName = $"gen-{generationId:N}-shard-{group.Key:D2}.json.gz";
            var shardPath = Path.Combine(checkpointDir, fileName);
            var sha = WriteCheckpointShardCreateOnly(shardPath, shard);
            refs[group.Key] = new PhaseDMasterCheckpointShardReference
            {
                FileName = fileName,
                Sha256 = sha,
                Length = new FileInfo(shardPath).Length
            };
        }

        var generation = new PhaseDMasterCheckpointGeneration
        {
            GenerationId = generationId,
            CoveredThroughDeviceSequence = coveredThrough,
            CreatedUtc = DateTime.UtcNow,
            Shards = refs
        };
        var newManifest = new PhaseDMasterCheckpointManifest
        {
            SchemaVersion = 1,
            DeviceId = deviceId,
            Current = generation,
            Previous = oldReadable
        };
        WriteObjectAtomic(checkpointManifestPath, newManifest);

        // Solo ora la nuova generazione e' pubblicata. La delete dei segmenti puo arrivare prima del manifest su
        // un'altra replica: il materializer locale non scarta contribution mancanti finche' non vede una
        // generazione valida con CoveredThroughDeviceSequence sufficiente.
        foreach (var segment in segments) File.Delete(segment);
        CleanupUnreferencedGenerations(checkpointDir, newManifest);
    }

    private static PhaseDMasterCheckpointManifest ReadCheckpointManifestRequired(string path, string expectedDeviceId)
    {
        var manifest = JsonIo.TryRead<PhaseDMasterCheckpointManifest>(path, out var error)
            ?? throw new InvalidDataException($"Manifest checkpoint Phase D non leggibile: {error}");
        if (manifest.SchemaVersion != 1 || !manifest.DeviceId.Equals(expectedDeviceId, StringComparison.Ordinal))
            throw new InvalidDataException("Manifest checkpoint Phase D incompatibile o attribuito al device sbagliato.");
        ValidateGenerationMetadata(manifest.Current);
        ValidateGenerationMetadata(manifest.Previous);
        return manifest;
    }

    private static void ValidateGenerationMetadata(PhaseDMasterCheckpointGeneration? generation)
    {
        if (generation == null) return;
        if (generation.GenerationId == Guid.Empty || generation.CoveredThroughDeviceSequence <= 0 || generation.Shards == null)
            throw new InvalidDataException("Metadati generazione checkpoint Phase D invalidi.");
        foreach (var pair in generation.Shards)
        {
            if (pair.Key < 0 || pair.Key >= CheckpointShardCount || pair.Value == null ||
                string.IsNullOrWhiteSpace(pair.Value.FileName) || Path.GetFileName(pair.Value.FileName) != pair.Value.FileName ||
                string.IsNullOrWhiteSpace(pair.Value.Sha256) || pair.Value.Length <= 0)
                throw new InvalidDataException("Riferimento shard Phase D non valido.");
        }
    }

    private static bool TryValidateGeneration(
        string root,
        string deviceId,
        PhaseDMasterCheckpointGeneration generation,
        out IReadOnlyList<PhaseDMasterMutation> mutations)
    {
        mutations = Array.Empty<PhaseDMasterMutation>();
        try
        {
            ValidateGenerationMetadata(generation);
            var result = new List<PhaseDMasterMutation>();
            foreach (var pair in generation.Shards.OrderBy(x => x.Key))
            {
                var path = Path.Combine(CheckpointDirectory(root, deviceId), pair.Value.FileName);
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                if (info.Length != pair.Value.Length) return false;
                var actualHash = HashFile(path);
                if (!actualHash.Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                var shard = ReadCheckpointShardRequired(path, deviceId, generation.GenerationId,
                    generation.CoveredThroughDeviceSequence, pair.Key);
                result.AddRange(shard.LatestByEntity.Values);
            }
            mutations = result;
            return true;
        }
        catch { return false; }
    }

    private static PhaseDMasterCheckpointShard ReadCheckpointShardRequired(
        string path,
        string expectedDeviceId,
        Guid expectedGenerationId,
        long expectedCoveredThrough,
        int expectedShard)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var shard = JsonSerializer.Deserialize<PhaseDMasterCheckpointShard>(gzip, JsonIo.Options)
                ?? throw new InvalidDataException("Checkpoint shard Phase D vuoto.");
            if (shard.SchemaVersion != 1 ||
                !shard.DeviceId.Equals(expectedDeviceId, StringComparison.Ordinal) ||
                shard.GenerationId != expectedGenerationId ||
                shard.CoveredThroughDeviceSequence != expectedCoveredThrough ||
                shard.Shard != expectedShard ||
                shard.LatestByEntity == null)
                throw new InvalidDataException("Checkpoint shard Phase D incompatibile con il manifest generazione.");
            foreach (var pair in shard.LatestByEntity)
            {
                var mutation = pair.Value ?? throw new InvalidDataException("Mutation checkpoint Phase D nulla.");
                ValidateOwnedMutation(expectedDeviceId, mutation);
                if (!EntityKey(mutation).Equals(pair.Key, StringComparison.Ordinal))
                    throw new InvalidDataException("Indice checkpoint Phase D non coerente con la mutation contenuta.");
            }
            return shard;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) { throw new InvalidDataException($"Checkpoint Phase D non leggibile: {Path.GetFileName(path)}", ex); }
    }

    private static string WriteCheckpointShardCreateOnly(string path, PhaseDMasterCheckpointShard shard)
    {
        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true))
                JsonSerializer.Serialize(gzip, shard, JsonIo.Options);
            bytes = memory.ToArray();
        }
        WriteBytesAtomicCreateOnly(path, bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void CleanupUnreferencedGenerations(string checkpointDir, PhaseDMasterCheckpointManifest manifest)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var generation in new[] { manifest.Current, manifest.Previous })
        {
            if (generation == null) continue;
            foreach (var reference in generation.Shards.Values) keep.Add(reference.FileName);
        }

        foreach (var file in Directory.EnumerateFiles(checkpointDir, "gen-*-shard-*.json.gz", SearchOption.TopDirectoryOnly))
        {
            if (!keep.Contains(Path.GetFileName(file)))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    internal static bool TryReadBatchesStrict(
        string path,
        string expectedDeviceId,
        out IReadOnlyList<PhaseDMasterMutationBatch> batches)
        // ReadLinesShared (FileShare.ReadWrite|Delete): un handle del client di sincronizzazione non deve far fallire la lettura.
        => TryParseBatchesStrict(JsonIo.ReadLinesShared(path), expectedDeviceId, out batches);

    private static bool TryParseBatchesStrict(
        IEnumerable<string> lines,
        string expectedDeviceId,
        out IReadOnlyList<PhaseDMasterMutationBatch> batches)
    {
        batches = Array.Empty<PhaseDMasterMutationBatch>();
        try
        {
            var result = new List<PhaseDMasterMutationBatch>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var batch = JsonSerializer.Deserialize<PhaseDMasterMutationBatch>(line, CompactJson)
                    ?? throw new InvalidDataException("Batch Phase D nullo.");
                batch.Validate();
                if (!batch.DeviceId.Equals(expectedDeviceId, StringComparison.Ordinal))
                    throw new InvalidDataException("Batch Phase D presente nel journal del device sbagliato.");
                foreach (var mutation in batch.Mutations) ValidateOwnedMutation(expectedDeviceId, mutation);
                result.Add(batch);
            }
            batches = result;
            return true;
        }
        catch { return false; }
    }

    private static bool UltimoByteENewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    /// <summary>
    /// Ripara la coda lasciata da un'append interrotta (per esempio VPN caduta a metà riga). Agisce SOLO se tutte le righe
    /// terminate da newline sono valide e l'ultima riga è incompleta e non è un batch valido. In quel caso la coda viene
    /// copiata integralmente in un file laterale `current.ndjson.torn-&lt;timestamp&gt;` (prima della modifica) e poi
    /// troncata dal journal. Una riga completa e valida non viene mai scartata. Ritorna false senza modifiche in ogni altro caso.
    /// </summary>
    private static bool TryRiparaCodaTroncata(string current, string deviceId)
    {
        try
        {
            var bytes = JsonIo.ReadAllBytesShared(current);
            var prefissoLen = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            var codaLen = bytes.Length - prefissoLen;
            if (codaLen == 0) return false;   // il file termina con newline: l'errore è in una riga intermedia, non si ripara

            var prefisso = Encoding.UTF8.GetString(bytes, 0, prefissoLen);
            if (!TryParseBatchesStrict(prefisso.Split('\n'), deviceId, out _)) return false;

            var coda = bytes.AsSpan(prefissoLen).ToArray();
            if (TryParseBatchesStrict(new[] { Encoding.UTF8.GetString(coda) }, deviceId, out _)) return false;

            var laterale = current + ".torn-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            using (var copia = new FileStream(laterale, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough))
            {
                copia.Write(coda);
                copia.Flush(flushToDisk: true);
            }
            using (var journal = new FileStream(current, FileMode.Open, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                journal.SetLength(prefissoLen);
                journal.Flush(flushToDisk: true);
            }
            DiagnosticTraceService.Registra("PhaseD.CodaJournaleRiparata",
                $"journal={current}; laterale={Path.GetFileName(laterale)}; byte={codaLen}");
            PotaTornObsoleti(current);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("PhaseD.CodaJournaleRiparata.Errore", $"journal={current}; {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Conserva solo i <see cref="TornConservati"/> file laterali `current.ndjson.torn-*` piu' recenti. Il nome porta un
    /// timestamp UTC a larghezza fissa, quindi l'ordinamento lessicografico coincide con quello cronologico. Best-effort: un
    /// errore non annulla la riparazione gia' avvenuta.</summary>
    internal static void PotaTornObsoleti(string current)
    {
        try
        {
            var dir = Path.GetDirectoryName(current)!;
            var torn = Directory.EnumerateFiles(dir, Path.GetFileName(current) + ".torn-*", SearchOption.TopDirectoryOnly)
                .OrderBy(x => Path.GetFileName(x), StringComparer.Ordinal)
                .ToList();
            var daEliminare = torn.Count - TornConservati;
            if (daEliminare <= 0) return;

            var eliminati = 0;
            foreach (var file in torn.Take(daEliminare))
            {
                try { File.Delete(file); eliminati++; }
                catch { }
            }
            DiagnosticTraceService.Registra("PhaseD.TornPotati",
                $"journal={current}; trovati={torn.Count}; eliminati={eliminati}; conservati={TornConservati}");
        }
        catch (Exception ex)
        {
            DiagnosticTraceService.Registra("PhaseD.TornPotati.Errore", $"journal={current}; {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ValidateOwnedMutation(string deviceId, PhaseDMasterMutation mutation)
    {
        mutation.Validate();
        if (!mutation.VersionVector.TryGetValue(deviceId, out var ownCounter) || ownCounter <= 0)
            throw new InvalidDataException("Mutation Phase D senza counter del device proprietario del journal.");
    }

    private static void MergeLatestForDevice(
        Dictionary<string, PhaseDMasterMutation> latest,
        string deviceId,
        PhaseDMasterMutation mutation)
    {
        ValidateOwnedMutation(deviceId, mutation);
        var ownCounter = mutation.VersionVector[deviceId];
        var key = EntityKey(mutation);
        if (!latest.TryGetValue(key, out var existing))
        {
            latest[key] = mutation;
            return;
        }
        existing.VersionVector.TryGetValue(deviceId, out var existingCounter);
        if (ownCounter > existingCounter) latest[key] = mutation;
        else if (ownCounter == existingCounter && existing.MutationId != mutation.MutationId &&
                 (!existing.PayloadSha256.Equals(mutation.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
                  existing.Operation != mutation.Operation ||
                  !existing.BusinessKey.Equals(mutation.BusinessKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Due mutation Phase D dello stesso device hanno lo stesso counter ma contenuto divergente.");
    }

    private static PhaseDMasterStoreManifest ReadManifestRequired(string root)
    {
        var path = ManifestPath(root);
        if (!File.Exists(path)) throw new InvalidDataException("Master Phase D non inizializzato.");
        var manifest = JsonIo.TryRead<PhaseDMasterStoreManifest>(path, out var error)
            ?? throw new InvalidDataException($"Manifest master Phase D non leggibile: {error}");
        if (manifest.SchemaVersion != 1 || manifest.StorageMode != "phase-d-master-v1" ||
            manifest.CheckpointShardCount != CheckpointShardCount)
            throw new InvalidDataException("Manifest master Phase D incompatibile; scrittura/lettura bloccata.");
        return manifest;
    }

    private static int ShardOf(string catalog, Guid uid)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(catalog + ":" + uid.ToString("N")));
        return bytes[0] % CheckpointShardCount;
    }

    private static string EntityKey(PhaseDMasterMutation mutation) => mutation.Catalog + ":" + mutation.EntityUid.ToString("N");

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteObjectAtomic<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonIo.Options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void WriteBytesAtomicCreateOnly(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: false);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void WriteTextAtomic(string path, string text)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough | FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    /// <summary>Contatore locale scritto da una versione precedente alla 6.7.1 sotto la chiave grezza della cartella (vedi
    /// <see cref="WorkspaceLocalPaths.CartellaLegacyDaLeggere"/>). Solo lettura: non viene mai riscritto ne' spostato. Assente
    /// oppure cartella legacy assente: 0. Illeggibile: errore esplicito, come per il contatore corrente (nessun riuso silenzioso).</summary>
    internal static long LeggiContatoreLegacy(string? legacyDir, string deviceId)
    {
        if (legacyDir == null) return 0;
        var path = Path.Combine(legacyDir, deviceId, "sequence.txt");
        if (!File.Exists(path)) return 0;
        var text = File.ReadAllText(path).Trim();
        if (!long.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidDataException("Contatore locale Phase D precedente corrotto.");
        return value;
    }
}
