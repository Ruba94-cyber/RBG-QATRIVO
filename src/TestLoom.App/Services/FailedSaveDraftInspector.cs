using System.Text.Json;
using System.Security.Cryptography;

namespace TestLoom.App.Services;

/// <summary>
/// Esplorazione strettamente READ-ONLY delle bozze non autorevoli salvate dopo un errore.
/// L'interfaccia non consente mai publish, delete o merge; un recupero vero richiede
/// separatamente confronto con la versione corrente e conferma dell'operatore.
/// </summary>
public static class FailedSaveDraftInspector
{
    public sealed record DraftRow(
        string FilePath,
        string FileName,
        string Domain,
        Guid EntityUid,
        long ExpectedRevision,
        string Operator,
        DateTime CapturedUtc,
        string Error,
        bool IsReadable,
        string ContentSha256)
    {
        public string Label => IsReadable
            ? $"{CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {Domain}  ·  {EntityUid:N}"
            : $"{FileName}  ·  NON LEGGIBILE";
        public string Details => IsReadable
            ? $"Dominio: {Domain}    UID: {EntityUid:N}\nRevisione attesa: {ExpectedRevision}    Operatore: {Operator}\nAcquisita: {CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nMotivo salvataggio fallito: {Error}"
            : $"File non leggibile o danneggiato: {FileName}. Il documento originale non è stato toccato.";
    }

    private const long MaxInspectBytes = 8 * 1024 * 1024;
    private const int MaxPreviewChars = 64 * 1024;

    /// <summary>
    /// Paginazione logica: la directory non viene limitata nel numero di bozze conservate.
    /// Il chiamante può sfogliare con skip/take; nessun file è cancellato o riscritto.
    /// </summary>
    public static IReadOnlyList<DraftRow> ListPage(
        string sharedRoot, int skip, int take = 50, string? localBaseDirectory = null,
        string? domain = null, Guid? entityUid = null)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var directory = FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory);
        if (!Directory.Exists(directory)) return Array.Empty<DraftRow>();

        // I nomi file usano timestamp crescente; scorre prima i più recenti.
        // Enumerazione soltanto della cartella di questo workspace, senza recursion.
        // Il nome è <dominio>_<yyyyMMddTHHmmssfff>_<guid>.json: ordinare per nome raggrupperebbe per dominio,
        // quindi si ordina per il timestamp contenuto nel nome (file estranei: data di modifica).
        var files = OrderedDraftFiles(directory);
        if (domain == null && entityUid == null)
            return files.Skip(skip).Take(take).Select(LoadSummary).ToArray();

        var matches = new List<DraftRow>(take);
        var matched = 0;
        foreach (var path in files)
        {
            var row = LoadSummary(path);
            var domainMatches = domain == null || string.Equals(row.Domain, domain, StringComparison.Ordinal) ||
                domain == "nonconformita" && row.Domain == "segnalazione" && IsLegacyNetworkNc(path);
            if (!domainMatches ||
                (entityUid.HasValue && row.EntityUid != entityUid.Value))
                continue;
            if (matched++ < skip) continue;
            matches.Add(row);
            if (matches.Count == take) break;
        }
        return matches;
    }

    /// <summary>
    /// Bozze di record MAI salvati (nuova CR o nuova NC di rete): senza fotografia iniziale e con UID non presente
    /// nel repository. Il chiamante fornisce <paramref name="uidGiaPresente"/> dal repository vivo (record attivi e
    /// archiviati): una bozza il cui UID esiste gia non viene mai proposta come nuovo record. Sola lettura.
    /// </summary>
    public static IReadOnlyList<DraftRow> ListNewRecordPage(
        string sharedRoot, string domain, Func<Guid, bool> uidGiaPresente, int skip, int take = 50,
        string? localBaseDirectory = null)
    {
        if (domain is not ("change-request" or "nonconformita")) throw new ArgumentOutOfRangeException(nameof(domain));
        ArgumentNullException.ThrowIfNull(uidGiaPresente);
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        var directory = FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory);
        if (!Directory.Exists(directory)) return Array.Empty<DraftRow>();

        var matches = new List<DraftRow>(take);
        var matched = 0;
        foreach (var path in OrderedDraftFiles(directory))
        {
            var row = LoadSummary(path);
            if (!row.IsReadable || !DomainMatches(row, path, domain) || uidGiaPresente(row.EntityUid) ||
                !FailedSaveDraftRecoveryPreparationService.NuovoRecordAmmissibile(sharedRoot, row, path, localBaseDirectory))
                continue;
            if (matched++ < skip) continue;
            matches.Add(row);
            if (matches.Count == take) break;
        }
        return matches;
    }

    private static IEnumerable<string> OrderedDraftFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Stamp: DraftTimestamp(path)))
            .OrderByDescending(x => x.Stamp)
            .ThenByDescending(x => Path.GetFileName(x.Path), StringComparer.Ordinal)
            .Select(x => x.Path);

    private static bool DomainMatches(DraftRow row, string path, string domain) =>
        string.Equals(row.Domain, domain, StringComparison.Ordinal) ||
        domain == "nonconformita" && row.Domain == "segnalazione" && IsLegacyNetworkNc(path);

    private static DateTime DraftTimestamp(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length >= 3 && DateTime.TryParseExact(parts[^2], "yyyyMMdd'T'HHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var stamp))
            return stamp;
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
    }

    private static DraftRow LoadSummary(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget != null || info.Length > MaxInspectBytes)
                throw new InvalidDataException("Link o documento troppo grande per l'anteprima.");
            var bytes = JsonIo.ReadAllBytesShared(path);
            var entry = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(bytes, out var error);
            if (entry == null || error != null || entry.Schema != 1 ||
                entry.Domain is not ("change-request" or "segnalazione" or "nonconformita") ||
                entry.EntityUid == Guid.Empty || string.IsNullOrWhiteSpace(entry.PayloadJson))
                throw new InvalidDataException(error ?? "Schema o metadati non validi.");
            // Convalidare anche il JSON che contiene la bozza: il file di emergenza potrebbe
            // essere stato scritto solo parzialmente o alterato da software esterno.
            using var _ = JsonDocument.Parse(entry.PayloadJson);
            return new DraftRow(path, name, entry.Domain, entry.EntityUid,
                entry.ExpectedRevision, entry.Operator, entry.CapturedUtc, entry.Error, true,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception)
        {
            // File corrotto: rappresentarlo nell'elenco per facilitarne il recupero
            // manuale, senza eliminare o tentare riparazioni silenziose.
            return new DraftRow(path, name, "", Guid.Empty, 0, "", DateTime.MinValue, "", false, "");
        }
    }

    private static bool IsLegacyNetworkNc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget != null || info.Length > MaxInspectBytes) return false;
            using var file = JsonDocument.Parse(JsonIo.ReadAllBytesShared(path));
            if (!file.RootElement.TryGetProperty(nameof(FailedSaveDraftService.DraftEnvelope.PayloadJson), out var payload) ||
                payload.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(payload.GetString()))
                return false;
            using var entity = JsonDocument.Parse(payload.GetString()!);
            return entity.RootElement.ValueKind == JsonValueKind.Object &&
                entity.RootElement.TryGetProperty("Id", out var id) &&
                id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString());
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Restituisce soltanto una preview limitata; non usa JsonIo.WriteAtomic e non
    /// modifica lo snapshot o la cartella condivisa. Il path deve appartenere
    /// esattamente alla directory locale del workspace selezionato.
    /// </summary>
    public static string Preview(string sharedRoot, DraftRow row, string? localBaseDirectory = null)
    {
        if (!row.IsReadable) return "Bozza danneggiata o non leggibile. Aprire la cartella locale per un recupero manuale.";
        var dir = Path.GetFullPath(FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory));
        var file = Path.GetFullPath(row.FilePath);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(file), dir, cmp) || !file.EndsWith(".json", cmp))
            throw new UnauthorizedAccessException("La bozza non appartiene al workspace corrente.");

        var fresh = LoadSummary(file);
        if (!fresh.IsReadable || fresh.EntityUid != row.EntityUid || fresh.Domain != row.Domain ||
            !string.Equals(fresh.ContentSha256, row.ContentSha256, StringComparison.Ordinal))
            return "La bozza è cambiata o non è più leggibile. Aggiornare l'elenco prima di procedere.";
        var bytes = JsonIo.ReadAllBytesShared(file);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(row.ContentSha256, StringComparison.Ordinal))
            return "La bozza è cambiata dopo la selezione. Aggiornare l'elenco.";
        var entry = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(bytes, out var error);
        if (entry == null || error != null) return "Bozza non leggibile.";
        var payload = entry.PayloadJson;
        if (payload.Length > MaxPreviewChars)
            return payload[..MaxPreviewChars] + "\n\n[Anteprima troncata: l'originale resta integro sul disco.]";
        return payload;
    }
}
