using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Conservative last-moment guard against VISIBLE OneDrive conflict copies for
/// a single logical JSON record. The guard never edits/renames any file. It
/// cannot detect unsynchronized remote replicas: those require the separate
/// physical multi-PC validation and immutable per-device transport.
/// </summary>
public static class VisibleConflictWriteGuard
{
    /// <summary>Regola di chiusura fornita dal chiamante (journal del dominio): true se la copia visibile e' una versione
    /// GIA' CHIUSA di questo record (rami di un fork risolto, o revisione nota e superata). Una copia chiusa non blocca;
    /// ogni altra copia divergente blocca come prima. Riceve il canonico solo se e' leggibile.</summary>
    public delegate bool CopiaChiusaPredicate(JsonElement copia, JsonElement canonica);

    /// <param name="ramiGiaRisolti">Solo per il merge automatico di un fork (TestCaseAutoForkMergeService): hash dei rami del
    /// fork che il chiamante sta incorporando nella versione che scrive. Una copia con uno di questi hash non e' una
    /// versione divergente sconosciuta (il suo contenuto e' gia' nel merge). Ogni altra copia divergente blocca comunque.</param>
    /// <param name="copiaChiusa">Regola di chiusura del dominio (vedi <see cref="CopiaChiusaPredicate"/>): una copia di un fork
    /// gia' risolto non blocca i salvataggi successivi, anche se OneDrive l'ha consegnata dopo la risoluzione.</param>
    public static void EnsureNoVisibleFork(string canonicalPath, Guid entityUid, IReadOnlySet<string>? ramiGiaRisolti = null,
        CopiaChiusaPredicate? copiaChiusa = null)
    {
        if (entityUid == Guid.Empty)
            throw new IOException("Salvataggio bloccato: UID non valido.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(canonicalPath))
            ?? throw new IOException("Directory del record non disponibile.");
        if (!Directory.Exists(dir)) return; // new workspace; existing folders untouched

        var canonicalName = Path.GetFileNameWithoutExtension(canonicalPath);
        var canonicalFile = Path.GetFileName(canonicalPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        JsonDocument? expected = null;
        try
        {
            // Nessuna ToArray/LINQ e nessun array di candidati: su migliaia di
            // ID con prefisso condiviso la scansione resta streaming, a memoria O(1).
            // Non cache-are il risultato: le copie OneDrive possono arrivare fra
            // due salvataggi senza cambiare l'indice del repository.
            foreach (var path in Directory.EnumerateFiles(dir, canonicalName + "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                var file = Path.GetFileName(path);
                if (file.Equals(canonicalFile, comparison)) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(canonicalName, StringComparison.OrdinalIgnoreCase)) continue;
                var kind = ClassifySuffix(name[canonicalName.Length..]);
                if (kind == CandidateKind.None) continue;

                JsonDocument other;
                try { other = ReadJson(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    if (kind == CandidateKind.AmbiguousIdExtension) continue;
                    throw new IOException($"Salvataggio bloccato: copia di conflitto non leggibile per {canonicalName}. " +
                        "Conservare entrambe le versioni per il Master.", ex);
                }
                using (other)
                {
                    // Una copia gia' archiviata nel recovery-bin e' ora un tombstone TestLoom (_ditmDeleted) sul proprio percorso:
                    // non e' piu' una copia viva e non ha piu' l'UID nel documento. Non blocca.
                    if (EsTombstone(other.RootElement)) continue;
                    var candidateUid = ReadUid(other.RootElement);
                    if (!candidateUid.HasValue)
                    {
                        if (kind == CandidateKind.AmbiguousIdExtension) continue;
                        throw new IOException($"Salvataggio bloccato: copia potenzialmente concorrente {canonicalName} " +
                            "con UID assente o non valido. Conservarla per la verifica manuale.");
                    }
                    if (candidateUid.Value != entityUid) continue;
                    if (ramiGiaRisolti is { Count: > 0 } && HashDiCopia(other.RootElement) is { } hashCopia &&
                        ramiGiaRisolti.Contains(hashCopia)) continue;
                    // Nessuna lettura JSON canonica fino alla prima copia che
                    // rivendica l'UID del record corrente.
                    expected ??= File.Exists(canonicalPath) ? ReadJson(canonicalPath) : null;
                    if (expected == null)
                        throw new IOException($"Salvataggio bloccato: copia di conflitto per {canonicalName} senza documento canonico. " +
                            "Recupero manuale Master richiesto.");
                    if (JsonElement.DeepEquals(expected.RootElement, other.RootElement)) continue;
                    if (copiaChiusa != null && copiaChiusa(other.RootElement, expected.RootElement)) continue;
                    throw new IOException($"Salvataggio bloccato: versioni OneDrive divergenti del record {canonicalName}. " +
                        "Nessuna copia sovrascritta; richiedere la risoluzione Master.");
                }
            }
        }
        finally { expected?.Dispose(); }
    }

    /// <summary>
    /// Archivia nel recovery-bin le copie di conflitto VISIBILI dello stesso UID il cui contenuto soddisfa
    /// <paramref name="isRisolta"/> (versione incorporata nella risoluzione del fork). Nessuna cancellazione definitiva:
    /// ogni copia passa da <see cref="DomainRecoveryService.ArchiveAndDelete"/>, che scrive e verifica l'envelope prima di
    /// rimuovere il file. Le copie illeggibili, di un altro UID o di contenuto diverso restano dove sono. Un errore su una
    /// singola copia viene registrato e non annulla le altre (la risoluzione e' gia' salvata a questo punto).
    /// </summary>
    public static IReadOnlyList<DomainRecoveryRecord> ArchiviaCopieRisolte(
        DataPaths paths,
        string canonicalPath,
        Guid entityUid,
        Func<JsonElement, bool> isRisolta,
        DomainRecoveryCategory categoria,
        string? operatore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(isRisolta);
        var archiviate = new List<DomainRecoveryRecord>();
        if (entityUid == Guid.Empty) return archiviate;
        var dir = Path.GetDirectoryName(Path.GetFullPath(canonicalPath));
        if (dir == null || !Directory.Exists(dir)) return archiviate;

        var canonicalName = Path.GetFileNameWithoutExtension(canonicalPath);
        var canonicalFile = Path.GetFileName(canonicalPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // ToList: l'archiviazione rimuove file dalla stessa cartella che si sta enumerando.
        foreach (var path in Directory.EnumerateFiles(dir, canonicalName + "*.json", SearchOption.TopDirectoryOnly).ToList())
        {
            var file = Path.GetFileName(path);
            if (file.Equals(canonicalFile, comparison)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(canonicalName, StringComparison.OrdinalIgnoreCase)) continue;
            if (ClassifySuffix(name[canonicalName.Length..]) == CandidateKind.None) continue;

            bool risolta;
            try
            {
                using var json = ReadJson(path);
                if (ReadUid(json.RootElement) != entityUid) continue;
                risolta = isRisolta(json.RootElement);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue; // illeggibile: resta sul disco, nessuna archiviazione
            }
            if (!risolta) continue;

            try
            {
                archiviate.Add(DomainRecoveryService.ArchiveAndDelete(paths, path, categoria, operatore));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                CrashLogService.Registra($"Copia di conflitto {file}: archiviazione dopo risoluzione", ex);
            }
        }
        return archiviate;
    }

    private static bool EsTombstone(JsonElement root) =>
        WorkflowTransactionVisibilityService.TryReadMetadata(System.Text.Encoding.UTF8.GetBytes(root.GetRawText()), out var metadata)
        && metadata.Deleted;

    /// <summary>Hash di contenuto (stesso di TestCaseFingerprint.Compute, usato anche per i rami del fork). Fail-closed:
    /// se la copia non si puo' leggere come Test, null = nessuna esenzione e la guardia blocca come prima.</summary>
    private static string? HashDiCopia(JsonElement root)
    {
        try
        {
            var copia = root.Deserialize<TestCase>(JsonIo.Options);
            return copia == null ? null : TestCaseFingerprint.Compute(copia);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private enum CandidateKind { None, ExplicitConflictCopy, AmbiguousIdExtension }

    /// <summary>
    /// Stessa regola di riconoscimento della guardia di scrittura, esposta al monitor di salute della cartella:
    /// true solo per i suffissi di copia esplicita (" (Laptop)", " -...", "-PC2"), mai per gli ID ambigui.
    /// </summary>
    internal static bool EsSuffissoConflittoEsplicito(string suffix) =>
        ClassifySuffix(suffix) == CandidateKind.ExplicitConflictCopy;

    private static readonly string[] ExplicitDevicePrefixes =
    [
        "PC", "LAPTOP", "DESKTOP", "ALTROPC", "CONFLICT",
        "CONFLITTO", "COPY", "COPIA"
    ];

    private static CandidateKind ClassifySuffix(string suffix)
    {
        if (suffix.Length == 0) return CandidateKind.None;
        // OneDrive suffixes like " (Laptop)" are unambiguous regardless of
        // UID; a corrupt copy must block writes rather than be ignored.
        if (suffix.StartsWith(" (", StringComparison.Ordinal) ||
            suffix.StartsWith(" -", StringComparison.Ordinal))
            return CandidateKind.ExplicitConflictCopy;

        if (suffix[0] is not ('-' or '_'))
            return CandidateKind.None;

        // Some historical clients used "-PC2.json" and "-ALTROPC.json".
        // Preserve protection for these, even when the JSON is damaged.
        var label = suffix[1..].TrimStart(' ', '-', '_');
        if (ExplicitDevicePrefixes.Any(prefix =>
                label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return CandidateKind.ExplicitConflictCopy;

        // "cr_CR-2-TEST.json" is likely a DIFFERENT business record from
        // "cr_CR-2.json": only classify it as a fork if its UID matches.
        return CandidateKind.AmbiguousIdExtension;
    }

    private static JsonDocument ReadJson(string path)
    {
        var info = new FileInfo(path);
        // Una copia OneDrive online-only non deve essere idratata da ogni Save.
        // Sul nome di conflitto esplicito il chiamante tratta l'IOException come
        // BLOCCO conservativo, senza perdere alcun file.
        if (info.LinkTarget != null || (info.Attributes & FileAttributes.Offline) != 0 ||
            info.Length > 32 * 1024 * 1024)
            throw new IOException("Copia di conflitto non disponibile localmente o non sicura da leggere.");
        return JsonDocument.Parse(JsonIo.ReadAllBytesShared(path));
    }

    private static Guid? ReadUid(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("Uid", out var id) &&
        id.ValueKind == JsonValueKind.String &&
        Guid.TryParse(id.GetString(), out var parsed)
            ? parsed : null;
}
