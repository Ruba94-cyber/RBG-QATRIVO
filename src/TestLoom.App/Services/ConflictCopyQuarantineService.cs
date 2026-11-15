using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestLoom.App.Services;

/// <summary>Una copia di conflitto OneDrive/SharePoint ritirata dalle cartelle dei record e custodita in modo durevole
/// finche' un Master non la risolve nella sezione Conflitti. Il payload e' il file ORIGINALE byte-per-byte.</summary>
public sealed class ConflictCopyEnvelope
{
    public int Schema { get; set; } = 1;
    /// <summary>Hash di (UID + contenuto): due PC che ritirano la stessa copia producono lo stesso Id.</summary>
    public string Id { get; set; } = "";
    public DomainRecoveryCategory Category { get; set; }
    public Guid EntityUid { get; set; }
    public string OriginalRelativePath { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string PayloadBase64 { get; set; } = "";
    public DateTime DetectedUtc { get; set; }
    public string DetectedBy { get; set; } = "";
    public string Device { get; set; } = "";
    /// <summary>La copia era byte-equivalente al canonico al momento del ritiro: nessun campo da decidere.</summary>
    public bool IdenticalToCanonical { get; set; }
}

public sealed record ConflictCopyEntry(ConflictCopyEnvelope Envelope, IReadOnlyList<string> EnvelopePaths)
{
    public string Id => Envelope.Id;
    public byte[] Payload => Convert.FromBase64String(Envelope.PayloadBase64);
}

public sealed record ConflictQuarantineReport(int Ritirate, int GiaChiuse, int NonLeggibili, int Saltate, IReadOnlyList<string> Errori);

/// <summary>
/// Quarantena delle copie di conflitto OneDrive/SharePoint (requisito: i conflitti compaiono SOLO nella sezione
/// Conflitti, non nelle cartelle condivise dei record).
///
/// Per ogni copia ESPLICITA (canonico presente accanto, stesso UID, JSON leggibile e stabile) il file viene prima
/// scritto in un envelope durevole sotto <c>recovery-bin/conflitti-aperti/&lt;categoria&gt;</c> (stesso perimetro del
/// recovery-bin: incluso nei backup, nessuna retention), riletto e verificato per hash, e SOLO POI rimosso dalla cartella
/// del record. Se una qualunque verifica fallisce il file resta dove e'. Mai sovrascrittura: l'envelope e' create-only.
/// Una copia illeggibile o non ancora stabile resta in loco (e continua a bloccare i salvataggi come prima) ed e' elencata
/// dalla sezione Conflitti con la schermata "scegli la versione intera".
///
/// Multi-PC: l'Id e' deterministico (UID + contenuto) e il nome file include il dispositivo, quindi due PC che ritirano
/// la stessa copia non si sovrascrivono mai; l'elenco deduplica per Id. La chiusura lascia un marcatore
/// <c>conflict_closed_&lt;Id&gt;.json</c> append-only: una consegna tardiva della stessa copia non riapre il conflitto.
/// </summary>
public static class ConflictCopyQuarantineService
{
    private const long MaxCopyBytes = 32L * 1024 * 1024;

    public static string OpenDir(string root) => Path.Combine(root, "recovery-bin", "conflitti-aperti");

    private static string CategoryDir(string root, DomainRecoveryCategory c) => Path.Combine(OpenDir(root), c switch
    {
        DomainRecoveryCategory.TestCase => "testcase",
        DomainRecoveryCategory.NonConformita => "nonconformita",
        DomainRecoveryCategory.ChangeRequest => "change-request",
        DomainRecoveryCategory.Segnalazione => "segnalazione",
        _ => throw new ArgumentOutOfRangeException(nameof(c))
    });

    private static readonly DomainRecoveryCategory[] Categories =
    [
        DomainRecoveryCategory.TestCase, DomainRecoveryCategory.NonConformita,
        DomainRecoveryCategory.ChangeRequest, DomainRecoveryCategory.Segnalazione
    ];

    private static string DeviceTag()
    {
        var chars = Environment.MachineName.Where(char.IsLetterOrDigit).Take(24).ToArray();
        return chars.Length == 0 ? "pc" : new string(chars);
    }

    public static string ComputeId(Guid uid, ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid.ToString("N") + "|" +
            Convert.ToHexString(SHA256.HashData(payload))))).ToLowerInvariant()[..32];

    private static string FileNameOf(Guid uid, string id) => $"conflict_{uid:N}_{id}_{DeviceTag()}.json";
    private static string ClosedNameOf(string id) => $"conflict_closed_{id}.json";

    /// <summary>True se esiste (in qualunque categoria) almeno un conflitto aperto per l'UID: lettura di soli nomi file.</summary>
    public static bool HasOpen(string root, Guid uid)
    {
        if (string.IsNullOrWhiteSpace(root) || uid == Guid.Empty) return false;
        foreach (var c in Categories)
        {
            var dir = CategoryDir(root, c);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, $"conflict_{uid:N}_*.json"))
                    if (!IsClosed(dir, ParseId(f))) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; /* fail-closed */ }
        }
        return false;
    }

    /// <summary>True se esiste almeno un conflitto aperto per la categoria (solo nomi file). Fail-closed su errori di I/O.
    /// Serve ai controlli che prima guardavano le copie nella cartella (es. propagazione Audit): ritirare la copia non deve
    /// far sembrare "pulito" un workspace con un conflitto ancora da risolvere.</summary>
    public static bool HasAnyOpen(string root, DomainRecoveryCategory category)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var dir = CategoryDir(root, category);
        if (!Directory.Exists(dir)) return false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "conflict_*_*.json"))
            {
                if (Path.GetFileName(f).StartsWith("conflict_closed_", StringComparison.Ordinal)) continue;
                if (!IsClosed(dir, ParseId(f))) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string ParseId(string file)
    {
        var parts = Path.GetFileNameWithoutExtension(file).Split('_');
        return parts.Length >= 4 ? parts[2] : "";
    }

    private static bool IsClosed(string dir, string id) =>
        id.Length > 0 && File.Exists(Path.Combine(dir, ClosedNameOf(id)));

    /// <summary>Conflitti aperti (deduplicati per Id), dal piu' recente.</summary>
    public static IReadOnlyList<ConflictCopyEntry> List(string root, Guid? uid = null)
    {
        var result = new Dictionary<string, (ConflictCopyEnvelope Env, List<string> Paths)>(StringComparer.Ordinal);
        foreach (var c in Categories)
        {
            var dir = CategoryDir(root, c);
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, uid.HasValue ? $"conflict_{uid.Value:N}_*.json" : "conflict_*_*.json"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                if (Path.GetFileName(f).StartsWith("conflict_closed_", StringComparison.Ordinal)) continue;
                var env = JsonIo.TryRead<ConflictCopyEnvelope>(f, out _);
                if (env == null || env.Id.Length == 0 || IsClosed(dir, env.Id)) continue;
                try { VerifyEnvelope(env); }
                catch (InvalidDataException ex) { CrashLogService.Registra($"Quarantena conflitti: envelope non valido {f}", ex); continue; }
                if (result.TryGetValue(env.Id, out var existing)) existing.Paths.Add(f);
                else result[env.Id] = (env, new List<string> { f });
            }
        }
        return result.Values.OrderByDescending(v => v.Env.DetectedUtc)
            .Select(v => new ConflictCopyEntry(v.Env, v.Paths)).ToList();
    }

    private static void VerifyEnvelope(ConflictCopyEnvelope env)
    {
        byte[] payload;
        try { payload = Convert.FromBase64String(env.PayloadBase64); }
        catch (FormatException ex) { throw new InvalidDataException("Payload della copia di conflitto non valido.", ex); }
        if (!Convert.ToHexString(SHA256.HashData(payload)).Equals(env.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hash della copia di conflitto non valido.");
    }

    /// <summary>Copie di conflitto ancora nelle cartelle dei record (illeggibili o non ancora stabili): restano in loco.</summary>
    public static IReadOnlyList<string> ListInPlace(string root, CancellationToken ct = default) =>
        WorkspaceOperationalFileEnumerator.EnumerateConflictCopies(root, ct).ToList();

    /// <summary>Ritira le copie di conflitto dalle cartelle dei record. Non lancia: gli errori sono nel report.</summary>
    /// <param name="candidati">Percorsi gia' noti (es. dai repository): evita la scansione completa dell'albero. Null = scansione.</param>
    /// <param name="etaMinima">Una copia piu' recente di questa soglia (data del file) non viene ritirata: potrebbe essere ancora
    /// in sincronizzazione o in un merge automatico in corso. Default zero (nessuna soglia).</param>
    public static ConflictQuarantineReport Run(DataPaths paths, string? operatore, IEnumerable<string>? candidati = null,
        TimeSpan etaMinima = default, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        int moved = 0, alreadyClosed = 0, unreadable = 0, skipped = 0;
        var errors = new List<string>();
        try
        {
            // Mai mutare mentre non si puo' scrivere o mentre una Unit of Work e' attiva: il giro successivo riprova.
            if (WorkflowTransactionService.IsActiveAnywhere || !WorkspaceWritePolicy.CanWrite(paths))
                return new(0, 0, 0, 1, errors);

            var adesso = FileProblemToleranceTracker.NowUtcOverrideForTests?.Invoke() ?? DateTime.UtcNow;
            var elenco = (candidati ?? WorkspaceOperationalFileEnumerator.EnumerateConflictCopies(paths.Root, ct)).Distinct(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
            foreach (var copy in elenco)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(copy)) continue;
                    if (etaMinima > TimeSpan.Zero && adesso - File.GetLastWriteTimeUtc(copy) < etaMinima) { skipped++; continue; }
                    switch (QuarantineOne(paths, copy, operatore))
                    {
                        case Outcome.Moved: moved++; break;
                        case Outcome.AlreadyClosed: alreadyClosed++; break;
                        case Outcome.Unreadable: unreadable++; break;
                        default: skipped++; break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    skipped++;
                    errors.Add($"{Path.GetFileName(copy)}: {ex.Message}");
                    CrashLogService.Registra($"Quarantena conflitti: {Path.GetFileName(copy)}", ex);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            CrashLogService.Registra("Quarantena conflitti", ex);
        }
        return new(moved, alreadyClosed, unreadable, skipped, errors);
    }

    private enum Outcome { Moved, AlreadyClosed, Unreadable, Skipped }

    private static Outcome QuarantineOne(DataPaths paths, string copy, string? operatore)
    {
        var root = paths.Root;
        DomainRecoveryCategory? category = null;
        foreach (var c in Categories)
            if (TestLoomStorageOwnership.IsRecoverableDomainJsonPath(root, copy, c)) { category = c; break; }
        if (category == null) return Outcome.Skipped;
        if (!ConflictCopyClassifier.TryGetCanonicalPath(copy, out var canonical)) return Outcome.Skipped;

        var info = new FileInfo(copy);
        if (!info.Exists || info.LinkTarget != null || (info.Attributes & FileAttributes.Offline) != 0 ||
            info.Length == 0 || info.Length > MaxCopyBytes) return Outcome.Unreadable;
        var stamp = (info.Length, info.LastWriteTimeUtc);

        byte[] bytes, canonicalBytes;
        try
        {
            bytes = JsonIo.ReadAllBytesShared(copy);
            canonicalBytes = JsonIo.ReadAllBytesShared(canonical);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Outcome.Unreadable; }

        // Stabilita': il file non deve cambiare durante la lettura (sync OneDrive in corso).
        info.Refresh();
        if (!info.Exists || (info.Length, info.LastWriteTimeUtc) != stamp || bytes.Length != stamp.Length)
            return Outcome.Unreadable;

        Guid copyUid, canonicalUid;
        bool identical;
        try
        {
            using var copyDoc = JsonDocument.Parse(bytes);
            using var canonicalDoc = JsonDocument.Parse(canonicalBytes);
            if (IsTombstone(copyDoc.RootElement) || IsTombstone(canonicalDoc.RootElement)) return Outcome.Skipped;
            if (!TryReadUid(copyDoc.RootElement, out copyUid) || !TryReadUid(canonicalDoc.RootElement, out canonicalUid))
                return Outcome.Unreadable;
            if (copyUid != canonicalUid) return Outcome.Skipped; // record distinto con nome simile: mai toccato
            identical = JsonElement.DeepEquals(copyDoc.RootElement, canonicalDoc.RootElement);
        }
        catch (JsonException) { return Outcome.Unreadable; }

        var id = ComputeId(copyUid, bytes);
        var dir = CategoryDir(root, category.Value);
        if (IsClosed(dir, id))
        {
            // Consegna tardiva di una copia gia' risolta e archiviata nel recovery-bin: contenuto gia' al sicuro.
            WorkflowTransactionService.DeleteFile(copy);
            return Outcome.AlreadyClosed;
        }

        var env = new ConflictCopyEnvelope
        {
            Id = id,
            Category = category.Value,
            EntityUid = copyUid,
            OriginalRelativePath = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(copy)),
            OriginalFileName = Path.GetFileName(copy),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            PayloadBase64 = Convert.ToBase64String(bytes),
            DetectedUtc = DateTime.UtcNow,
            DetectedBy = operatore ?? "",
            Device = Environment.MachineName,
            IdenticalToCanonical = identical
        };
        var envPath = Path.Combine(dir, FileNameOf(copyUid, id));
        if (!File.Exists(envPath))
        {
            try { JsonIo.WriteAtomic(envPath, env, overwrite: false); }
            catch (IOException) when (File.Exists(envPath)) { /* stesso envelope creato in parallelo */ }
        }

        // Verifica durevole PRIMA di toccare la copia: rilettura + hash + payload identico ai byte letti.
        var persisted = JsonIo.TryRead<ConflictCopyEnvelope>(envPath, out var error)
            ?? throw new IOException($"Envelope della copia di conflitto non rileggibile: {error}");
        VerifyEnvelope(persisted);
        if (!persisted.Sha256.Equals(env.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Envelope della copia di conflitto non corrisponde al file originale; copia lasciata dov'e'.");

        // Ultimo controllo: il file non e' cambiato mentre scrivevamo l'envelope.
        if (!File.Exists(copy)) return Outcome.Moved;
        var now = JsonIo.ReadAllBytesShared(copy);
        if (!now.AsSpan().SequenceEqual(bytes))
            throw new IOException("La copia di conflitto e' cambiata durante il ritiro; lasciata dov'e'.");
        WorkflowTransactionService.DeleteFile(copy);
        return Outcome.Moved;
    }

    /// <summary>Chiude il conflitto: marcatore append-only + rimozione degli envelope. Chiamare SOLO dopo che il contenuto della
    /// copia e' stato archiviato nel recovery-bin (o incorporato e archiviato).</summary>
    public static void Close(string root, ConflictCopyEntry entry)
    {
        var dir = CategoryDir(root, entry.Envelope.Category);
        var marker = Path.Combine(dir, ClosedNameOf(entry.Id));
        if (!File.Exists(marker))
        {
            try
            {
                JsonIo.WriteAtomic(marker, new { entry.Id, ClosedUtc = DateTime.UtcNow, Device = Environment.MachineName }, overwrite: false);
            }
            catch (IOException) when (File.Exists(marker)) { }
        }
        foreach (var p in entry.EnvelopePaths)
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLogService.Registra("Quarantena conflitti: envelope non rimosso", ex);
            }
        }
    }

    private static bool IsTombstone(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(WorkflowTransactionVisibilityService.DeletedProperty, out var d) && d.ValueKind == JsonValueKind.True;

    private static bool TryReadUid(JsonElement root, out Guid uid)
    {
        uid = Guid.Empty;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Uid", out var u) &&
               u.ValueKind == JsonValueKind.String && Guid.TryParse(u.GetString(), out uid) && uid != Guid.Empty;
    }
}
