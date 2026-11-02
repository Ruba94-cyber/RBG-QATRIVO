using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestLoom.App.Services;

/// <summary>
/// Cestino tecnico condiviso per le cancellazioni di dominio realmente distruttive.
///
/// SharePoint/OneDrive offre cronologia/versioning, ma TestLoom non deve dipendere da una configurazione esterna
/// per recuperare un record cancellato. Prima di togliere fisicamente Test/NC/CR/Segnalazioni, il payload UTF-8
/// originale viene quindi incorporato byte-per-byte in un envelope JSON sotto recovery-bin. L'envelope contiene
/// percorso originale, hash, categoria, autore e timestamp ed entra automaticamente nei normali backup TestLoom.
///
/// REGOLA DI SAFETY: se la copia di recovery non può essere scritta e flushata, la cancellazione NON avviene.
/// Non esiste retention automatica: una purge deve essere un'azione Master esplicita e separata.
/// </summary>
public static class DomainRecoveryService
{
    /// <summary>
    /// Crea e verifica una copia recovery senza toccare il source. È il primitive usato dal journal Test:
    /// Prepare(Delete) deve rendere durevole il payload condiviso PRIMA che il repository esegua la delete.
    /// </summary>
    public static DomainRecoveryRecord ArchiveOnly(
        DataPaths paths,
        string sourcePath,
        DomainRecoveryCategory category,
        string? operatore = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Creazione recovery di dominio");
        var fullSource = ValidateRecoverableSource(paths, sourcePath, category);
        if (!File.Exists(fullSource))
            throw new FileNotFoundException("Il record da archiviare non esiste più. Recovery non creata.", fullSource);

        byte[] payload;
        try { payload = File.ReadAllBytes(fullSource); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Il record da archiviare non è leggibile: operazione bloccata per preservare i dati.", ex);
        }

        // Un tombstone non è un nuovo record archiviabile: significa che la delete è già stata committed.
        if (WorkflowTransactionVisibilityService.TryReadMetadata(payload, out var metadata) && metadata.Deleted)
            throw new InvalidOperationException("Il record richiesto è già un tombstone TestLoom committed/pending e non può essere archiviato di nuovo.");

        // Il payload deve essere un JSON TestLoom leggibile almeno sintatticamente. Il repository effettua poi le
        // verifiche di modello/UID/revisione/hash specifiche; qui impediamo di certificare un file troncato.
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(payload);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Il record da archiviare non contiene JSON valido: operazione bloccata.", ex);
        }

        var record = new DomainRecoveryRecord
        {
            RecoveryId = Guid.NewGuid(),
            Category = category,
            OriginalRelativePath = Path.GetRelativePath(Path.GetFullPath(paths.Root), fullSource),
            OriginalFileName = Path.GetFileName(fullSource),
            ArchivedUtc = DateTime.UtcNow,
            ArchivedBy = operatore ?? "",
            Device = Environment.MachineName,
            Sha256 = Hash(payload),
            PayloadBase64 = Convert.ToBase64String(payload)
        };

        var recoveryPath = RecoveryPath(paths, record);
        JsonIo.WriteAtomic(recoveryPath, record, overwrite: false);

        // Verifica immediata del record appena pubblicato. Dentro la transazione proprietaria JsonIo consente
        // di rileggere il proprio payload prima della receipt; fuori da essa vale la normale barriera cross-PC.
        var persisted = JsonIo.TryRead<DomainRecoveryRecord>(recoveryPath, out var error)
            ?? throw new IOException($"La copia di recovery non è rileggibile; operazione bloccata: {error}");
        ValidateEnvelope(paths, persisted, recoveryPath);
        if (!persisted.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("La copia di recovery non corrisponde al record originale; operazione bloccata.");

        return record;
    }

    /// <summary>
    /// Archive + delete è sempre una singola Unit of Work. Se il chiamante è già nel salvataggio Test più
    /// ampio viene riusata quella transazione; altrimenti ne viene creata una locale. In questo modo non può
    /// esistere una delete senza recovery envelope committed insieme al tombstone.
    /// </summary>
    public static DomainRecoveryRecord ArchiveAndDelete(
        DataPaths paths,
        string sourcePath,
        DomainRecoveryCategory category,
        string? operatore = null)
    {
        if (!WorkflowTransactionService.IsActive)
        {
            return WorkflowTransactionService.Execute(
                paths,
                $"delete-domain:{category}:{Path.GetFileName(sourcePath)}",
                () => ArchiveAndDeleteCore(paths, sourcePath, category, operatore));
        }

        return ArchiveAndDeleteCore(paths, sourcePath, category, operatore);
    }

    private static DomainRecoveryRecord ArchiveAndDeleteCore(
        DataPaths paths,
        string sourcePath,
        DomainRecoveryCategory category,
        string? operatore)
    {
        var record = ArchiveOnly(paths, sourcePath, category, operatore);
        var fullSource = ValidateRecoverableSource(paths, sourcePath, category);

        try
        {
            WorkflowTransactionService.DeleteFile(fullSource);
        }
        catch
        {
            // La copia di recovery resta intenzionalmente disponibile se siamo dentro una transazione più ampia;
            // il rollback la rimuove automaticamente tramite la before-image. Avere una copia in più è comunque
            // più sicuro che nascondere una delete fallita.
            throw;
        }

        return record;
    }

    public static IReadOnlyList<DomainRecoveryRecord> GetAll(DataPaths paths)
    {
        if (!Directory.Exists(paths.RecoveryBinDir)) return Array.Empty<DomainRecoveryRecord>();
        var result = new List<DomainRecoveryRecord>();
        foreach (var file in Directory.EnumerateFiles(paths.RecoveryBinDir, "recovery_*.json", SearchOption.AllDirectories))
        {
            var record = JsonIo.TryRead<DomainRecoveryRecord>(file, out _);
            if (record == null) continue;
            try
            {
                ValidateEnvelope(paths, record, file);
                result.Add(record);
            }
            catch (Exception ex)
            {
                CrashLogService.Registra($"Recovery-bin: record non valido {file}", ex);
            }
        }
        return result.OrderByDescending(x => x.ArchivedUtc).ToList();
    }

    /// <summary>
    /// Elimina definitivamente e SENZA alcuna copia successiva l'envelope stesso dal recovery-bin (feedback
    /// utente 2026-09-18: "non capisco perché elimina definitivamente mi manda la roba in recovery... ho
    /// praticamente due archiviazioni e nulla si elimina mai definitivamente"). Come documentato in testa a
    /// questa classe, la retention del recovery-bin non è mai automatica di proposito: questa è esattamente
    /// l'azione Master esplicita e separata che completa il ciclo, l'unica che rende una cancellazione
    /// realmente irreversibile. L'envelope non è uno dei quattro record di dominio con visibilità cross-PC
    /// gestita (nessun altro punto dell'app legge un recovery-bin file tramite quel protocollo — vedi
    /// MarkRestored sopra, che scrive senza transazione per lo stesso motivo): una delete atomica diretta
    /// basta, non serve la stessa Unit of Work usata per Test/NC/CR/Segnalazioni.
    /// </summary>
    public static void Purge(DataPaths paths, Guid recoveryId, string? operatore = null)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Eliminazione definitiva dal recovery-bin");
        var recoveryPath = FindRecoveryPath(paths, recoveryId);
        if (recoveryPath == null || !File.Exists(recoveryPath)) return; // già rimossa/mai esistita: idempotente
        File.Delete(recoveryPath);
    }

    /// <summary>
    /// Ripristina il contenuto di dominio nel percorso originale. L'envelope conserva sempre il payload
    /// originale byte-per-byte; il file ripristinato può invece avere i metadati tecnici _ditmTx necessari alla
    /// visibilità cross-PC. L'equivalenza post-write ignora esclusivamente tali proprietà tecniche.
    /// </summary>
    public static DomainRecoveryRestoreResult Restore(DataPaths paths, Guid recoveryId, string? operatore = null)
    {
        if (!WorkflowTransactionService.IsActive)
        {
            return WorkflowTransactionService.Execute(
                paths,
                $"restore-domain:{recoveryId:N}",
                () => RestoreCore(paths, recoveryId, operatore));
        }

        return RestoreCore(paths, recoveryId, operatore);
    }

    private static DomainRecoveryRestoreResult RestoreCore(DataPaths paths, Guid recoveryId, string? operatore)
    {
        WorkspaceWritePolicy.EnsureCanWrite(paths, "Ripristino dal recovery-bin");
        var recoveryPath = FindRecoveryPath(paths, recoveryId)
            ?? throw new FileNotFoundException($"Recovery {recoveryId:N} non trovata.");
        var record = JsonIo.TryRead<DomainRecoveryRecord>(recoveryPath, out var error)
            ?? throw new InvalidDataException($"Recovery non leggibile: {error}");
        var target = ValidateEnvelope(paths, record, recoveryPath);
        var payload = DecodeAndVerify(record);

        var overwriteTombstone = false;
        if (File.Exists(target))
        {
            byte[] current;
            try { current = File.ReadAllBytes(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Il percorso originale esiste ma non è leggibile. Ripristino bloccato per evitare sovrascritture ambigue.", ex);
            }

            if (EquivalentDomainPayload(current, payload))
            {
                // Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): un JSON di dominio sintatticamente valido è sempre
                // leggibile, quindi payload equivalente = stato già presente. Basta escludere il caso in cui il
                // file corrente sia in realtà un tombstone (payload testuale coincidente per puro caso non è
                // possibile qui perché EquivalentDomainPayload confronta i byte di dominio, ma il controllo
                // resta a scopo diagnostico/difensivo).
                MarkRestored(recoveryPath, record, operatore, alreadyPresent: true);
                return new DomainRecoveryRestoreResult(record, target, AlreadyPresent: true);
            }

            // Un tombstone committed sullo STESSO path è la rappresentazione tecnica della cancellazione che
            // questa recovery deve annullare. Qualunque altro contenuto resta un conflitto e non viene toccato.
            overwriteTombstone = WorkflowTransactionVisibilityService.IsCommittedTombstone(paths, target);
            if (!overwriteTombstone)
                throw new IOException("Il percorso originale contiene già un record diverso. Ripristino bloccato: risolvi prima il conflitto.");
        }

        JsonIo.WriteBytesAtomic(target, payload, overwrite: overwriteTombstone);
        var verify = File.ReadAllBytes(target);
        if (!EquivalentDomainPayload(verify, payload))
            throw new IOException("Verifica post-ripristino fallita. Il recovery-bin è stato conservato.");

        MarkRestored(recoveryPath, record, operatore, alreadyPresent: false);
        return new DomainRecoveryRestoreResult(record, target, AlreadyPresent: false);
    }

    /// <summary>
    /// Registra nel journal per-device il Create di un record NC/CR/Segnalazione appena ripristinato dal recovery-bin.
    /// Best effort: il ripristino è già committed e il journal non deve mai farlo fallire, quindi ogni errore viene solo
    /// registrato. Nessun evento se il record era già presente con lo stesso contenuto (nessuno stato è cambiato) e per le
    /// categorie che non usano il journal dei record (i Test hanno un protocollo proprio).
    /// </summary>
    public static void TryRecordRestoreInJournal(DomainRecordJournalService? journal, DomainRecoveryRestoreResult restored, string? operatore)
    {
        ArgumentNullException.ThrowIfNull(restored);
        if (journal == null || restored.AlreadyPresent) return;
        try
        {
            var descrizione = DescribeRestoredRecord(restored.Record);
            if (descrizione == null) return;
            var d = descrizione.Value;
            journal.RecordExternalCreate(d.Domain, d.Uid, d.Id, d.Version, d.Revision, operatore);
        }
        catch (Exception ex)
        {
            CrashLogService.Registra("Record journal: ripristino recovery-bin", ex);
        }
    }

    /// <summary>
    /// Ricostruisce identità e versione dal payload byte-per-byte con la stessa deserializzazione che il journal usa per
    /// verificare il canonico (CommitWasApplied): così l'hash dell'evento coincide con quello del file ripristinato.
    /// </summary>
    private static (string Domain, Guid Uid, string Id, object Version, long? Revision)? DescribeRestoredRecord(DomainRecoveryRecord record)
    {
        var payload = Convert.FromBase64String(record.PayloadBase64);
        switch (record.Category)
        {
            case DomainRecoveryCategory.NonConformita:
            {
                var nc = JsonSerializer.Deserialize<Models.NonConformita>(payload, JsonIo.Options)
                    ?? throw new InvalidDataException("Record NC ripristinato vuoto.");
                return (DomainRecordJournalService.NonConformitaDomain, nc.Uid, nc.Id, nc, nc.RevisioneDati);
            }
            case DomainRecoveryCategory.ChangeRequest:
            {
                var cr = JsonSerializer.Deserialize<Models.ChangeRequest>(payload, JsonIo.Options)
                    ?? throw new InvalidDataException("Record CR ripristinato vuoto.");
                return (DomainRecordJournalService.ChangeRequestDomain, cr.Uid, cr.Id, cr, cr.RevisioneDati);
            }
            case DomainRecoveryCategory.Segnalazione:
            {
                var seg = JsonSerializer.Deserialize<Models.SegnalazioneDaClassificare>(payload, JsonIo.Options)
                    ?? throw new InvalidDataException("Record segnalazione ripristinato vuoto.");
                // Come SegnalazioneRepository.Save: la segnalazione usa l'UID senza trattini come identificatore.
                return (DomainRecordJournalService.SegnalazioneDomain, seg.Uid, seg.Uid.ToString("N"), seg, seg.RevisioneDati);
            }
            default:
                return null;
        }
    }

    private static void MarkRestored(string recoveryPath, DomainRecoveryRecord record, string? operatore, bool alreadyPresent)
    {
        record.RestoredUtc = DateTime.UtcNow;
        record.RestoredBy = operatore ?? "";
        record.RestoreAlreadyPresent = alreadyPresent;
        JsonIo.WriteAtomic(recoveryPath, record, overwrite: true);
    }

    private static bool EquivalentDomainPayload(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        try
        {
            var a = JsonNode.Parse(left);
            var b = JsonNode.Parse(right);
            RemoveTechnicalMetadata(a);
            RemoveTechnicalMetadata(b);
            return JsonNode.DeepEquals(a, b);
        }
        catch
        {
            return left.SequenceEqual(right);
        }
    }

    private static void RemoveTechnicalMetadata(JsonNode? node)
    {
        if (node is not JsonObject obj) return;
        obj.Remove(WorkflowTransactionVisibilityService.TransactionProperty);
        obj.Remove(WorkflowTransactionVisibilityService.TransactionDeviceProperty);
        obj.Remove(WorkflowTransactionVisibilityService.DeletedProperty);
        obj.Remove(WorkflowTransactionVisibilityService.OriginalUidProperty);
        obj.Remove(WorkflowTransactionVisibilityService.OriginalIdProperty);
        obj.Remove("_ditmDeletedUtc");
        obj.Remove("_ditmDevice");
        obj.Remove("_ditmOriginalFile");
    }

    private static string ValidateRecoverableSource(DataPaths paths, string sourcePath, DomainRecoveryCategory category)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!TestLoomStorageOwnership.IsRecoverableDomainJsonPath(paths.Root, full, category))
            throw new InvalidOperationException("Il percorso richiesto non appartiene a un record di dominio TestLoom recuperabile.");
        return full;
    }

    private static string ValidateEnvelope(DataPaths paths, DomainRecoveryRecord record, string recoveryPath)
    {
        if (record.RecoveryId == Guid.Empty || string.IsNullOrWhiteSpace(record.OriginalRelativePath) ||
            string.IsNullOrWhiteSpace(record.Sha256) || string.IsNullOrWhiteSpace(record.PayloadBase64))
            throw new InvalidDataException("Envelope recovery incompleto.");

        var expectedRecoveryPath = RecoveryPath(paths, record);
        if (!Path.GetFullPath(expectedRecoveryPath).Equals(Path.GetFullPath(recoveryPath), PathComparison))
            throw new InvalidDataException("Envelope recovery collocato in una categoria/percorso non coerente.");

        var root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, record.OriginalRelativePath));
        if (!IsUnderOrEqual(target, root))
            throw new InvalidDataException("Envelope recovery tenta di uscire dal workspace TestLoom.");
        if (!TestLoomStorageOwnership.IsRecoverableDomainJsonPath(paths.Root, target, record.Category))
            throw new InvalidDataException("Il target dell'envelope non è un record di dominio TestLoom consentito.");

        _ = DecodeAndVerify(record);
        return target;
    }

    private static byte[] DecodeAndVerify(DomainRecoveryRecord record)
    {
        byte[] payload;
        try { payload = Convert.FromBase64String(record.PayloadBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Payload recovery Base64 non valido.", ex); }
        var hash = Hash(payload);
        if (!hash.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hash del payload recovery non valido.");
        return payload;
    }

    private static string RecoveryPath(DataPaths paths, DomainRecoveryRecord record)
    {
        var category = CategoryFolder(record.Category);
        var day = record.ArchivedUtc == default ? "unknown" : record.ArchivedUtc.ToString("yyyyMMdd");
        return Path.Combine(paths.RecoveryBinDir, category, day, $"recovery_{record.RecoveryId:N}.json");
    }

    private static string? FindRecoveryPath(DataPaths paths, Guid recoveryId)
    {
        if (!Directory.Exists(paths.RecoveryBinDir)) return null;
        var name = $"recovery_{recoveryId:N}.json";
        return Directory.EnumerateFiles(paths.RecoveryBinDir, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string CategoryFolder(DomainRecoveryCategory category) => category switch
    {
        DomainRecoveryCategory.TestCase => "testcase",
        DomainRecoveryCategory.NonConformita => "nonconformita",
        DomainRecoveryCategory.ChangeRequest => "change-request",
        DomainRecoveryCategory.Segnalazione => "segnalazione",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsUnderOrEqual(string path, string root)
    {
        var comparison = PathComparison;
        if (path.Equals(root, comparison)) return true;
        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public enum DomainRecoveryCategory
{
    TestCase,
    NonConformita,
    ChangeRequest,
    Segnalazione
}

public sealed class DomainRecoveryRecord
{
    public Guid RecoveryId { get; set; }
    public DomainRecoveryCategory Category { get; set; }
    public string OriginalRelativePath { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public DateTime ArchivedUtc { get; set; }
    public string ArchivedBy { get; set; } = "";
    public string Device { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string PayloadBase64 { get; set; } = "";
    public DateTime? RestoredUtc { get; set; }
    public string RestoredBy { get; set; } = "";
    public bool RestoreAlreadyPresent { get; set; }
}

public sealed record DomainRecoveryRestoreResult(DomainRecoveryRecord Record, string TargetPath, bool AlreadyPresent);