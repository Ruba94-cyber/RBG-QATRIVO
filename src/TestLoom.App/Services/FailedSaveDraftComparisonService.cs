using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Confronto puramente consultivo tra una bozza di emergenza e il documento canonico
/// attualmente VISIBILE NEL FILESYSTEM LOCALE. Non riavvia OneDrive, non usa API cloud,
/// non sceglie versioni e soprattutto NON scrive file condivisi.
/// Il confronto NON può provare l'assenza di repliche OneDrive non ancora sincronizzate.
/// </summary>
public static class FailedSaveDraftComparisonService
{
    public enum ComparisonState
    {
        IdenticalLocal,
        DifferentLocal,
        MissingLocal,
        DifferentUid,
        DifferentId,
        StaleLocal,
        DraftChanged,
        InvalidDraft,
        InvalidLocal,
        TombstoneLocal
    }

    public sealed record Comparison(
        ComparisonState State,
        string Explanation,
        string DraftPreview,
        string LocalPreview,
        long? LocalRevision,
        JsonStructuralDiffService.Report? Differences = null,
        string? ThreeWaySummary = null,
        IReadOnlyList<string>? RecoverableFields = null,
        IReadOnlyList<string>? ConflictingFields = null)
    {
        public string StateLabel => State switch
        {
            ComparisonState.IdenticalLocal => "Payload uguale al documento locale",
            ComparisonState.DifferentLocal => "Payload diverso dal documento locale",
            ComparisonState.MissingLocal => "Documento canonico non presente localmente",
            ComparisonState.DifferentUid => "UID non corrispondente: potenziale conflitto",
            ComparisonState.DifferentId => "ID canonico diverso dal documento in bozza",
            ComparisonState.StaleLocal => "Revisione locale più vecchia di quella attesa",
            ComparisonState.DraftChanged => "Bozza modificata dopo l'elenco: confronto interrotto",
            ComparisonState.InvalidDraft => "Bozza non valida: confronto interrotto",
            ComparisonState.InvalidLocal => "Documento locale non leggibile o non valido",
            ComparisonState.TombstoneLocal => "Documento locale eliminato logicamente",
            _ => "Confronto non disponibile"
        };
    }

    private const long MaxCompareBytes = 8 * 1024 * 1024;
    private const int MaxPreviewChars = 32 * 1024;
    private const string Caveat =
        "Questo confronto legge SOLO la copia locale. Non certifica la sincronizzazione cloud/OneDrive " +
        "né l'assenza di copie di conflitto. Non è stato modificato alcun dato.";

    public static Comparison Compare(string sharedRoot, FailedSaveDraftInspector.DraftRow row,
        string? localBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        var directory = Path.GetFullPath(FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory));
        var path = Path.GetFullPath(row.FilePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), directory, comparison) ||
            !path.EndsWith(".json", comparison))
            throw new UnauthorizedAccessException("La bozza selezionata non appartiene al workspace TestLoom.");

        if (!row.IsReadable || row.EntityUid == Guid.Empty)
            return new(ComparisonState.InvalidDraft, Caveat, "", "", null);

        try
        {
            // Prima leggere una fotografia verificata della bozza; se è cambiata rispetto
            // alla selezione, non confrontare accidentalmente una versione diversa.
            var draftBytes = ReadBounded(path);
            if (!string.Equals(Sha(draftBytes), row.ContentSha256, StringComparison.Ordinal))
                return new(ComparisonState.DraftChanged, Caveat, "", "", null);

            var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(draftBytes, out var err);
            if (envelope == null || err != null || envelope.Schema != 1 ||
                envelope.EntityUid != row.EntityUid || envelope.Domain != row.Domain)
                return new(ComparisonState.InvalidDraft, Caveat, "", "", null);

            using var draftDocument = JsonDocument.Parse(envelope.PayloadJson);
            var draftRoot = draftDocument.RootElement;
            var uid = ReadUid(draftRoot);
            var id = ReadId(draftRoot);
            // SegnalazioneDaClassificare has NO string Id: its canonical
            // pathname is derived from Uid. Older SegnalazioneReteEditor drafts were
            // mislabeled "segnalazione" but contain a NonConformita Id.
            var isNetworkNc = row.Domain == "nonconformita" ||
                row.Domain == "segnalazione" && id != null;
            var requiresId = row.Domain == "change-request" || isNetworkNc;
            if (uid == null || uid.Value != row.EntityUid ||
                (requiresId && !ValidId(id)))
                return new(ComparisonState.InvalidDraft,
                    "La bozza non contiene un UID/ID canonico valido. " + Caveat, "", "", null);

            var paths = new DataPaths(sharedRoot);
            string canonical = row.Domain switch
            {
                "change-request" => paths.ChangeRequestFile(id!),
                "nonconformita" => paths.NonConformitaFile(id!),
                "segnalazione" => isNetworkNc
                    ? paths.NonConformitaFile(id!) : paths.SegnalazioneFile(row.EntityUid),
                _ => ""
            };
            if (canonical.Length == 0)
                return new(ComparisonState.InvalidDraft, Caveat, "", "", null);

            var draftPreview = Shorten(envelope.PayloadJson);
            if (!File.Exists(canonical))
                return new(ComparisonState.MissingLocal,
                    "Il file canonico non è presente su questa postazione. Non dedurre che sia stato eliminato nel cloud. " +
                    Caveat, draftPreview, "", null);
            byte[] localBytes;
            try { localBytes = ReadBounded(canonical); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new(ComparisonState.InvalidLocal, "File locale non accessibile. " + Caveat,
                    draftPreview, "", null);
            }

            JsonDocument current;
            try { current = JsonDocument.Parse(localBytes); }
            catch (JsonException)
            {
                return new(ComparisonState.InvalidLocal,
                    "Il JSON canonico locale è illeggibile o incompleto. " + Caveat,
                    draftPreview, "", null);
            }
            using (current)
            {
            var localRoot = current.RootElement;
            var localRevision = ReadRevision(localRoot);
            if (localRoot.ValueKind != JsonValueKind.Object)
                return new(ComparisonState.InvalidLocal, Caveat, draftPreview, "", null);

            if (localRoot.TryGetProperty("_ditmDeleted", out var deleted) &&
                deleted.ValueKind == JsonValueKind.True)
                return new(ComparisonState.TombstoneLocal,
                    "Il documento canonico locale è un tombstone: non ripristinarlo automaticamente. " +
                    Caveat, draftPreview, Shorten(current.RootElement.GetRawText()), localRevision);

            if (ReadUid(localRoot) != row.EntityUid)
                return new(ComparisonState.DifferentUid,
                    "Il file canonico ha un UID differente; è vietato copiarvi sopra la bozza. " +
                    Caveat, draftPreview, Shorten(localRoot.GetRawText()), localRevision);

            if (requiresId &&
                !string.Equals(ReadId(localRoot), id, StringComparison.Ordinal))
                return new(ComparisonState.DifferentId,
                    "Il documento visibile ha un ID diverso: non è sicuro considerarlo la versione corrente. " +
                    Caveat, draftPreview, Shorten(localRoot.GetRawText()), localRevision);

            if (localRevision.HasValue && localRevision.Value < row.ExpectedRevision)
                return new(ComparisonState.StaleLocal,
                    "Il documento locale ha una revisione inferiore a quella attesa dalla bozza; potrebbe essere una replica vecchia. " +
                    Caveat, draftPreview, Shorten(localRoot.GetRawText()), localRevision);

            var threeWay = BuildThreeWayAnalysis(envelope, draftRoot, localRoot);
            var differences = JsonStructuralDiffService.Compare(draftRoot, localRoot);
            var equal = !differences.HasDifferences && !differences.Truncated;
            return new(equal ? ComparisonState.IdenticalLocal : ComparisonState.DifferentLocal,
                $"Bozza: revisione attesa {row.ExpectedRevision}. Documento locale: " +
                $"{(localRevision?.ToString() ?? "non indicata")}. " +
                (equal ? "I payload coincidono su questo PC. " :
                    "I payload sono differenti su questo PC; verificare prima del recupero manuale. ") +
                Caveat,
                draftPreview, Shorten(localRoot.GetRawText()), localRevision, differences,
                threeWay.Summary, threeWay.RecoverableFields, threeWay.ConflictingFields);
            } // Dispose JsonDocument current
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(ComparisonState.InvalidDraft,
                "Impossibile effettuare un confronto affidabile: " + ex.Message + ". " + Caveat,
                "", "", null);
        }
    }

    /// <summary>
    /// Diagnostic only, not an automatic 3-way merge. Fields touched by both sides
    /// (including ancestor/child paths) are flagged conservatively as overlaps.
    /// Version-field overlaps also remain visible: no implicit precedence policy.
    /// </summary>
    private sealed record ThreeWayAnalysis(
        string Summary,
        IReadOnlyList<string> RecoverableFields,
        IReadOnlyList<string> ConflictingFields);

    private static ThreeWayAnalysis BuildThreeWayAnalysis(
        FailedSaveDraftService.DraftEnvelope envelope,
        JsonElement draft, JsonElement current)
    {
        if (string.IsNullOrWhiteSpace(envelope.BasePayloadJson))
            return new("Base iniziale non disponibile: i campi della bozza non sono selezionabili in sicurezza.",
                Array.Empty<string>(), Array.Empty<string>());

        var baseJson = envelope.BasePayloadJson;
        if (string.IsNullOrWhiteSpace(envelope.BaseSha256) ||
            !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseJson)))
                .Equals(envelope.BaseSha256, StringComparison.Ordinal))
            return new("Fotografia iniziale non integra: i campi della bozza non sono selezionabili in sicurezza.",
                Array.Empty<string>(), Array.Empty<string>());

        try
        {
            using var baseline = JsonDocument.Parse(baseJson);
            if (ReadUid(baseline.RootElement) != envelope.EntityUid ||
                ReadId(baseline.RootElement) != ReadId(draft) ||
                baseline.RootElement.ValueKind != JsonValueKind.Object)
                return new("Identità della fotografia iniziale diversa; i campi non sono selezionabili in sicurezza.",
                    Array.Empty<string>(), Array.Empty<string>());

            var user = JsonStructuralDiffService.Compare(baseline.RootElement, draft);
            var disk = JsonStructuralDiffService.Compare(baseline.RootElement, current);
            if (user.Truncated || disk.Truncated)
                return new("Differenze troppo numerose o profonde: nessun campo è selezionabile in sicurezza.",
                    Array.Empty<string>(), Array.Empty<string>());

            var draftFields = RootFields(user.Items);
            var currentFields = RootFields(disk.Items);
            var conflicts = draftFields.Intersect(currentFields, StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var safeFields = draftFields.Except(currentFields, StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();

            return new(
                $"Confronto informativo a tre versioni: modifiche in bozza {user.Items.Count}, " +
                $"modifiche nella copia locale {disk.Items.Count}. " +
                (conflicts.Length == 0
                    ? "Nessun campo sovrapposto rilevato; sono selezionabili solo i campi modificati nella bozza e invariati localmente."
                    : $"Campi sovrapposti non selezionabili: {string.Join(", ", conflicts.Select(x => "/" + x))}. " +
                      "Sono selezionabili solo i campi modificati nella bozza e invariati localmente.") +
                " I campi modificati solo nel documento locale restano non selezionabili dalla bozza." +
                " Non è un merge automatico; stato cloud OneDrive non verificato.",
                safeFields, conflicts);
        }
        catch (JsonException)
        {
            return new("La fotografia iniziale contiene JSON non valido; i campi non sono selezionabili in sicurezza.",
                Array.Empty<string>(), Array.Empty<string>());
        }
    }

    private static HashSet<string> RootFields(IEnumerable<JsonStructuralDiffService.Difference> differences)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var difference in differences)
        {
            var segments = difference.Path.Split('/', StringSplitOptions.None);
            if (segments.Length < 2 || segments[1].Length == 0) continue;
            fields.Add(segments[1].Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal));
        }
        return fields;
    }

    private static Guid? ReadUid(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty("Uid", out var x) &&
        x.ValueKind == JsonValueKind.String &&
        Guid.TryParse(x.GetString(), out var uid) ? uid : null;

    private static string? ReadId(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty("Id", out var x) && x.ValueKind == JsonValueKind.String
            ? x.GetString() : null;

    private static long? ReadRevision(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty("RevisioneDati", out var x) &&
        x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out var rev)
            ? rev : null;

    private static bool ValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id != "." && id != ".." &&
        id.Length <= 180 &&
        id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    private static byte[] ReadBounded(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null || info.Length > MaxCompareBytes)
            throw new InvalidDataException("Il documento è un collegamento o supera il limite di anteprima.");
        return JsonIo.ReadAllBytesShared(path);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Shorten(string json) => json.Length > MaxPreviewChars
        ? json[..MaxPreviewChars] + "\n[Anteprima troncata; nessun dato originale è stato modificato]"
        : json;
}
