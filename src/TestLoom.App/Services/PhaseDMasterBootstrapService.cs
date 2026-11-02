using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Bootstrap una-tantum verso Phase D. TestLoom non e' ancora deployata, quindi non mantiene dual-write: i JSON
/// monolitici eventualmente presenti vengono importati come fotografia iniziale e poi restano solo materiale
/// legacy/import. ready.json rende il journal Phase D l'unica source of truth dei master.
///
/// La correttezza non dipende dal lock condiviso: su due repliche OneDrive disconnesse entrambi i PC potrebbero
/// creare il proprio lock. Per questo gli Uid mancanti del legacy sono deterministici catalogo+chiave business
/// e due import della stessa fotografia producono teste concorrenti con payload identico, che il materializer
/// coalesca senza last-writer-wins. Il lock serve comunque a evitare lavoro duplicato quando i client si vedono.
/// Nessun bootstrap cancella mai l'area di un altro device.
/// </summary>
public static class PhaseDMasterBootstrapService
{
    private const int ImportBatchSize = 250;
    private static readonly TimeSpan BootstrapLeaseStaleAfter = TimeSpan.FromHours(2);

    public static string ReadyPath(string root) => Path.Combine(PhaseDMasterJournalService.PhaseDRoot(root), "ready.json");
    public static string BootstrapLeasePath(string root) => Path.Combine(root, "_ditm", "master-v2-bootstrap.lock.json");
    public static string PendingPath(string root, string deviceId) =>
        Path.Combine(PhaseDMasterJournalService.PhaseDRoot(root), "bootstrap", deviceId + ".pending.json");

    public static bool IsReady(string root)
    {
        if (!File.Exists(PhaseDMasterJournalService.ManifestPath(root)) || !File.Exists(ReadyPath(root))) return false;
        try
        {
            var ready = JsonIo.TryRead<PhaseDMasterReadyMarker>(ReadyPath(root), out _);
            return ready is
            {
                SchemaVersion: 1,
                StorageMode: "phase-d-master-v1",
                Complete: true
            } && !string.IsNullOrWhiteSpace(ready.LegacySourceFingerprint);
        }
        catch { return false; }
    }

    public static void EnsureReady(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsReady(paths.Root)) return;
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Bootstrap master Phase D");

        var deviceId = DeviceIdentityService.GetOrCreate();
        var lease = AcquireLease(paths.Root, deviceId);
        try
        {
            // Un altro device puo aver completato mentre aspettavamo/creavamo il lease.
            if (IsReady(paths.Root)) return;

            var sourceFingerprintBefore = CaptureLegacySourceFingerprint(paths);
            PhaseDMasterJournalService.EnsureInitialized(paths);
            WriteMarkerAtomic(PendingPath(paths.Root, deviceId), new PhaseDBootstrapPendingMarker
            {
                SchemaVersion = 1,
                LeaseId = lease.LeaseId,
                DeviceId = deviceId,
                StartedUtc = lease.StartedUtc,
                LegacySourceFingerprint = sourceFingerprintBefore
            });

            ImportIfPresent<Requisito>(paths, paths.RequisitiFile, "requisiti");
            ImportIfPresent<Funzionalita>(paths, paths.FunzionalitaFile, "funzionalita");
            ImportIfPresent<Componente>(paths, paths.EcuFile, "componente");
            ImportIfPresent<Gamma>(paths, paths.GammaFile, "gamma");
            ImportIfPresent<Categoria>(paths, paths.CategorieFile, "categorie");
            ImportIfPresent<Veicolo>(paths, paths.VeicoliFile, "veicoli");
            ImportIfPresent<StrumentoDiagnostico>(paths, paths.StrumentiFile, "strumenti");
            ImportIfPresent<Operatore>(paths, paths.OperatoriFile, "operatori");
            ImportIfPresent<TestTemplate>(paths, paths.TestTemplateFile, "test-template");
            ImportIfPresent<CampoSistemaDefinizione>(paths, paths.CampiSistemaPianificazioneFile, "campi-sistema-pianificazione");
            ImportIfPresent<CampoSistemaDefinizione>(paths, paths.CampiSistemaDettagliFile, "campi-sistema-dettagli");
            ImportIfPresent<RisoluzioneConflittoAudit>(paths, paths.RisoluzioniConflittoAuditFile, "audit-risoluzioni-conflitto");
            ImportIfPresent<RevisioneUdt>(paths, paths.RevisioniUdtFile, "revisioni-unitlogger");
            ImportIfPresent<ValutazioneRequisitoEcuVeicolo>(paths, paths.ValutazioniRequisitoEcuVeicoloFile, "audit-valutazioni-requisito-componente-veicolo");
            ImportIfPresent<StatoFunzionalitaEcuVeicolo>(paths, paths.StatiFunzionalitaEcuVeicoloFile, "report-stato-funzionalita-componente-veicolo");

            var sourceFingerprintAfter = CaptureLegacySourceFingerprint(paths);
            if (!sourceFingerprintBefore.Equals(sourceFingerprintAfter, StringComparison.Ordinal))
                throw new IOException("Bootstrap Phase D annullato: uno o piu master legacy sono cambiati durante l'import. ready.json non e stato pubblicato.");

            // Contenuto volutamente canonico: due bootstrap realmente concorrenti della stessa fotografia
            // possono creare ready.json in modo idempotente anche se OneDrive non offre un CAS distribuito.
            PublishReadyCreateOnly(paths.Root, new PhaseDMasterReadyMarker
            {
                SchemaVersion = 1,
                StorageMode = "phase-d-master-v1",
                Complete = true,
                CompletedUtc = DateTime.UnixEpoch,
                LegacySourceFingerprint = sourceFingerprintBefore
            });

            try { if (File.Exists(PendingPath(paths.Root, deviceId))) File.Delete(PendingPath(paths.Root, deviceId)); } catch { }
        }
        finally
        {
            ReleaseLeaseIfOwned(paths.Root, lease);
        }
    }

    private static void ImportIfPresent<T>(DataPaths paths, string legacyPath, string catalog) where T : MasterEntity
    {
        if (!File.Exists(legacyPath)) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(legacyPath); }
        catch (Exception ex) { throw new MasterDataReadException(legacyPath, "Bootstrap Phase D: master legacy non leggibile.", ex); }

        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (Exception ex) { throw new MasterDataReadException(legacyPath, "Bootstrap Phase D: JSON legacy non valido.", ex); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new MasterDataReadException(legacyPath, "Bootstrap Phase D: il master legacy non e un array JSON.");

            var items = new List<T>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var item = element.Deserialize<T>(JsonIo.Options)
                    ?? throw new MasterDataReadException(legacyPath, "Bootstrap Phase D: riga master legacy nulla/non deserializzabile.");
                var businessKey = PhaseDMasterBusinessKeyService.Get(item);
                if (string.IsNullOrWhiteSpace(businessKey))
                    throw new MasterDataReadException(legacyPath, "Bootstrap Phase D: riga master senza chiave business deterministica.");

                var hadPersistedIdentity = HasPersistedIdentity(element, item.GetType());
                MasterEntityIdentityService.GetOrCreateForBootstrap(item, catalog, businessKey, hadPersistedIdentity);
                items.Add(item);
            }

            MasterEntityIdentityService.EnsureUnique(items, assignMissing: false);
            ValidateUniqueBusinessKeys(items, legacyPath);
            if (items.Count == 0) return;

            var deviceId = DeviceIdentityService.GetOrCreate();
            for (var offset = 0; offset < items.Count; offset += ImportBatchSize)
            {
                var mutations = new List<PhaseDMasterMutation>();
                foreach (var item in items.Skip(offset).Take(ImportBatchSize))
                {
                    var payload = JsonSerializer.Serialize(item, JsonIo.CompactOptions);
                    var payloadSha = PhaseDMasterMutation.HashPayload(payload);
                    mutations.Add(new PhaseDMasterMutation
                    {
                        MutationId = DeterministicGuid("bootstrap-mutation|" + deviceId + "|" + catalog + "|" +
                                                       MasterEntityIdentityService.GetRequired(item).ToString("N") + "|" + payloadSha),
                        Catalog = catalog,
                        EntityUid = MasterEntityIdentityService.GetRequired(item),
                        Operation = PhaseDMasterMutationOperation.Upsert,
                        VersionVector = PhaseDVersionVector.Increment(null, deviceId),
                        PayloadJson = payload,
                        PayloadSha256 = payloadSha,
                        BusinessKey = PhaseDMasterBusinessKeyService.Get(item),
                        TimestampUtc = DateTime.UnixEpoch
                    });
                }

                var firstUid = mutations[0].EntityUid.ToString("N");
                var lastUid = mutations[^1].EntityUid.ToString("N");
                PhaseDMasterJournalService.Append(paths, new PhaseDMasterMutationBatch
                {
                    BatchId = DeterministicGuid("bootstrap-batch|" + deviceId + "|" + catalog + "|" + firstUid + "|" + lastUid),
                    DeviceId = deviceId,
                    DeviceSequence = PhaseDMasterJournalService.NextDeviceSequence(paths.Root),
                    CreatedUtc = DateTime.UnixEpoch,
                    Mutations = mutations
                });
            }
        }
    }

    private static bool HasPersistedIdentity(JsonElement element, Type entityType)
    {
        var native = entityType.GetProperty("Uid");
        if (native?.PropertyType == typeof(Guid))
        {
            if (!element.TryGetProperty("Uid", out var uidElement)) return false;
            if (uidElement.ValueKind != JsonValueKind.String || !Guid.TryParse(uidElement.GetString(), out var uid) || uid == Guid.Empty)
                throw new InvalidDataException($"Uid legacy malformato per {entityType.Name}.");
            return true;
        }

        if (!element.TryGetProperty(MasterEntityIdentityService.ExtensionKey, out var extension)) return false;
        if (extension.ValueKind != JsonValueKind.String || !Guid.TryParse(extension.GetString(), out var extensionUid) || extensionUid == Guid.Empty)
            throw new InvalidDataException($"{MasterEntityIdentityService.ExtensionKey} legacy malformato per {entityType.Name}.");
        return true;
    }

    private static void ValidateUniqueBusinessKeys<T>(IEnumerable<T> items, string path) where T : MasterEntity
    {
        var seen = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = PhaseDMasterBusinessKeyService.Get(item);
            var uid = MasterEntityIdentityService.GetRequired(item);
            if (seen.TryGetValue(key, out var other) && other != uid)
                throw new MasterDataReadException(path, $"Bootstrap Phase D: chiave business duplicata '{key}'.");
            seen[key] = uid;
        }
    }

    private static string CaptureLegacySourceFingerprint(DataPaths paths)
    {
        var files = new[]
        {
            paths.RequisitiFile, paths.FunzionalitaFile, paths.EcuFile, paths.GammaFile, paths.CategorieFile,
            paths.VeicoliFile, paths.StrumentiFile, paths.OperatoriFile, paths.TestTemplateFile,
            paths.CampiSistemaPianificazioneFile, paths.CampiSistemaDettagliFile,
            paths.RisoluzioniConflittoAuditFile, paths.RevisioniUdtFile, paths.ValutazioniRequisitoEcuVeicoloFile
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            if (!File.Exists(path))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("<absent>\n"));
                continue;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            var fileHash = SHA256.HashData(stream);
            hash.AppendData(fileHash);
            hash.AppendData([(byte)'\n']);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static PhaseDBootstrapLease AcquireLease(string root, string deviceId)
    {
        var path = BootstrapLeasePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lease = new PhaseDBootstrapLease
        {
            SchemaVersion = 1,
            LeaseId = Guid.NewGuid(),
            DeviceId = deviceId,
            Machine = Environment.MachineName,
            StartedUtc = DateTime.UtcNow
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(lease, JsonIo.Options);
        try
        {
            WriteBytesCreateOnly(path, bytes);
            return lease;
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = JsonIo.TryRead<PhaseDBootstrapLease>(path, out var error);
            if (existing == null)
                throw new IOException($"Bootstrap Phase D bloccato: lease esistente non leggibile ({error}). Non verra sovrascritto automaticamente.");
            var age = DateTime.UtcNow - existing.StartedUtc;
            var stale = age > BootstrapLeaseStaleAfter;
            throw new IOException(stale
                ? $"Bootstrap Phase D bloccato da lease scaduto {existing.LeaseId:N} ({existing.DeviceId}/{existing.Machine}). Rimuoverlo esplicitamente solo dopo aver verificato che nessun bootstrap sia attivo."
                : $"Bootstrap Phase D gia in corso su {existing.DeviceId}/{existing.Machine} da {existing.StartedUtc:O}. Nessun file verra cancellato o sovrascritto.");
        }
    }

    private static void ReleaseLeaseIfOwned(string root, PhaseDBootstrapLease lease)
    {
        var path = BootstrapLeasePath(root);
        try
        {
            if (!File.Exists(path)) return;
            var current = JsonIo.TryRead<PhaseDBootstrapLease>(path, out _);
            if (current?.LeaseId == lease.LeaseId && current.DeviceId == lease.DeviceId) File.Delete(path);
        }
        catch { }
    }

    private static void PublishReadyCreateOnly(string root, PhaseDMasterReadyMarker marker)
    {
        var path = ReadyPath(root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options);
        try { WriteBytesCreateOnly(path, bytes); }
        catch (IOException) when (File.Exists(path))
        {
            var existing = JsonIo.TryRead<PhaseDMasterReadyMarker>(path, out var error)
                ?? throw new InvalidDataException($"ready.json Phase D esistente ma non leggibile: {error}");
            if (existing.SchemaVersion != marker.SchemaVersion || existing.StorageMode != marker.StorageMode ||
                !existing.Complete || !existing.LegacySourceFingerprint.Equals(marker.LegacySourceFingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("ready.json Phase D appartiene a una fotografia bootstrap diversa. Cutover bloccato.");
        }
    }

    private static void WriteMarkerAtomic<T>(string path, T marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonIo.Options);
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

    private static void WriteBytesCreateOnly(string path, ReadOnlySpan<byte> bytes)
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

    private static Guid DeterministicGuid(string source)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

public sealed class PhaseDBootstrapLease
{
    public int SchemaVersion { get; set; } = 1;
    public Guid LeaseId { get; set; }
    public string DeviceId { get; set; } = "";
    public string Machine { get; set; } = "";
    public DateTime StartedUtc { get; set; }
}

public sealed class PhaseDBootstrapPendingMarker
{
    public int SchemaVersion { get; set; } = 1;
    public Guid LeaseId { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public string LegacySourceFingerprint { get; set; } = "";
}

public sealed class PhaseDMasterReadyMarker
{
    public int SchemaVersion { get; set; } = 1;
    public string StorageMode { get; set; } = "phase-d-master-v1";
    public bool Complete { get; set; }
    public DateTime CompletedUtc { get; set; }
    public string LegacySourceFingerprint { get; set; } = "";
}
