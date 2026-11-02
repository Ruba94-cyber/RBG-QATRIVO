using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestLoom.App.Services;

/// <summary>
/// Decisione 6.4.2 (docs/PIANO_6.4.2.md §1): un JSON di dominio sintatticamente valido è SEMPRE leggibile.
/// È stato eliminato il gate cross-file all-or-nothing basato sulle receipt del ledger (VerifyReceiptSettled e
/// le sue eccezioni caso per caso): ogni JSON scritto dentro una WorkflowTransaction poteva restare bloccato
/// "in attesa di sincronizzazione" per sempre non appena un ALTRO file della stessa transazione veniva
/// legittimamente riscritto da un salvataggio successivo, anche a PC singolo.
///
/// Un tombstone (<see cref="DeletedProperty"/> = true) è ora un'eliminazione logica locale, senza bisogno di
/// receipt: <see cref="IsCommittedTombstone"/> legge solo il file stesso. Con più PC il costo accettato è che,
/// per il tempo di consegna di OneDrive, un record aggiornato possa comparire qualche secondo prima di un
/// altro file della stessa operazione; il refresh successivo riallinea.
///
/// Restano invariati: la Unit of Work locale con before-image e rollback (<see cref="WorkflowTransactionService"/>,
/// protezione reale contro i salvataggi a metà) e <see cref="WorkflowCommitLedgerService"/>, che pubblica ancora
/// la receipt del commit — serve a <c>VerificaRicevutaCommit</c> per la crash-recovery
/// (<c>WorkflowTransactionService.RecoverPending</c>), non più per decidere se un JSON è leggibile.
/// </summary>
public static class WorkflowTransactionVisibilityService
{
    public const string TransactionProperty = "_ditmTx";
    public const string TransactionDeviceProperty = "_ditmTxDevice";
    public const string DeletedProperty = "_ditmDeleted";
    public const string OriginalUidProperty = "_ditmOriginalUid";
    public const string OriginalIdProperty = "_ditmOriginalId";

    public static byte[] StampJson(ReadOnlySpan<byte> utf8Json, Guid transactionId, string deviceId, bool deleted = false)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(utf8Json); }
        catch (Exception ex) { throw new InvalidDataException("Il payload transazionale non contiene JSON valido.", ex); }

        if (parsed is not JsonObject root)
            throw new InvalidDataException("I record di dominio transazionali TestLoom devono essere oggetti JSON.");
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new InvalidDataException("DeviceId transazionale non disponibile.");

        root[TransactionProperty] = transactionId.ToString("N");
        root[TransactionDeviceProperty] = deviceId;
        if (deleted) root[DeletedProperty] = true;
        else root.Remove(DeletedProperty);
        return JsonSerializer.SerializeToUtf8Bytes(root, JsonIo.Options);
    }

    public static byte[] CreateTombstone(
        Guid transactionId,
        string deviceId,
        string? originalFileName = null,
        ReadOnlySpan<byte> originalJson = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new InvalidDataException("DeviceId transazionale non disponibile.");

        var root = new JsonObject
        {
            [TransactionProperty] = transactionId.ToString("N"),
            [TransactionDeviceProperty] = deviceId,
            [DeletedProperty] = true,
            ["_ditmDeletedUtc"] = DateTime.UtcNow.ToString("O"),
            ["_ditmDevice"] = Environment.MachineName
        };
        if (!string.IsNullOrWhiteSpace(originalFileName)) root["_ditmOriginalFile"] = originalFileName;

        if (!originalJson.IsEmpty)
        {
            try
            {
                var reader = new Utf8JsonReader(originalJson);
                using var document = JsonDocument.ParseValue(ref reader);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyCaseInsensitive(document.RootElement, "Uid", out var uid) && uid.ValueKind == JsonValueKind.String)
                        root[OriginalUidProperty] = uid.GetString();
                    if (TryGetPropertyCaseInsensitive(document.RootElement, "Id", out var id) && id.ValueKind == JsonValueKind.String)
                        root[OriginalIdProperty] = id.GetString();
                }
            }
            catch { /* identity è diagnostica; la before-image resta la fonte del rollback */ }
        }

        return JsonSerializer.SerializeToUtf8Bytes(root, JsonIo.Options);
    }

    /// <summary>
    /// Distingue un vero JSON legacy da un documento che dichiara il protocollo transazionale ma ha metadati
    /// corrotti. La presenza di _ditmTx non deve mai degradare a LegacyVisible soltanto perché il valore non è
    /// un Guid valido: in quel caso il reader deve fallire chiuso e conservare la last-known-good.
    /// </summary>
    public static bool HasTransactionMarker(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
                utf8Json = utf8Json[3..];
            var reader = new Utf8JsonReader(utf8Json);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(TransactionProperty, out _);
        }
        catch { return false; }
    }

    public static bool TryReadMetadata(ReadOnlySpan<byte> utf8Json, out WorkflowJsonMetadata metadata)
    {
        metadata = default;
        try
        {
            if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
                utf8Json = utf8Json[3..];
            var reader = new Utf8JsonReader(utf8Json);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty(TransactionProperty, out var txElement) ||
                txElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(txElement.GetString(), out var txId) || txId == Guid.Empty)
                return false;

            var deviceId = document.RootElement.TryGetProperty(TransactionDeviceProperty, out var deviceElement) &&
                           deviceElement.ValueKind == JsonValueKind.String
                ? deviceElement.GetString() ?? ""
                : "";
            var deleted = document.RootElement.TryGetProperty(DeletedProperty, out var deletedElement) &&
                          deletedElement.ValueKind is JsonValueKind.True;
            metadata = new WorkflowJsonMetadata(txId, deviceId, deleted);
            return true;
        }
        catch { return false; }
    }

    public static bool TryReadTombstoneIdentity(string path, out Guid uid, out string? id)
    {
        uid = Guid.Empty;
        id = null;
        try
        {
            // Lettura condivisa: con FileShare.Read un handle del client di sincronizzazione bastava a far
            // fallire il riconoscimento del tombstone.
            var bytes = JsonIo.ReadAllBytesShared(path);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(DeletedProperty, out var deleted) || deleted.ValueKind != JsonValueKind.True)
                return false;
            if (document.RootElement.TryGetProperty(OriginalUidProperty, out var uidElement) && uidElement.ValueKind == JsonValueKind.String)
                Guid.TryParse(uidElement.GetString(), out uid);
            if (document.RootElement.TryGetProperty(OriginalIdProperty, out var idElement) && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetString();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Un tombstone locale: <see cref="DeletedProperty"/> = true nel file stesso, nessun controllo su ledger o
    /// receipt (decisione 6.4.2, docs/PIANO_6.4.2.md §1). <paramref name="paths"/> non è più necessario alla
    /// verifica ma resta nella firma per non rompere i chiamanti esistenti.
    /// </summary>
    public static bool IsCommittedTombstone(DataPaths paths, string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            // Lettura condivisa (FileShare.ReadWrite | Delete) come JsonIo.ReadAllBytesShared: un handle
            // aperto da OneDrive/SharePoint non deve rendere il record "temporaneamente non leggibile".
            var bytes = JsonIo.ReadAllBytesShared(path);
            return IsTombstoneJson(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsCommittedTombstoneFor(DataPaths paths, string path, Guid expectedUid, string? expectedId = null)
    {
        if (!IsCommittedTombstone(paths, path) || !TryReadTombstoneIdentity(path, out var uid, out var id)) return false;
        if (expectedUid != Guid.Empty && uid != expectedUid) return false;
        return string.IsNullOrWhiteSpace(expectedId) || string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vero se il JSON è un oggetto con <see cref="DeletedProperty"/> = true. Stesso criterio usato da
    /// <see cref="JsonIo.TryParse{T}(ReadOnlySpan{byte}, out string?)"/>: un tombstone è tale indipendentemente
    /// dalla validità/presenza di _ditmTx, che sui tombstone resta solo diagnostica.</summary>
    private static bool IsTombstoneJson(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
                utf8Json = utf8Json[3..];
            var reader = new Utf8JsonReader(utf8Json);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(DeletedProperty, out var deleted) &&
                   deleted.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string NormalizeRelativePath(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(fullPath);
        var rootWithSeparator = rootFull + Path.DirectorySeparatorChar;
        if (!full.Equals(rootFull, PathComparison) && !full.StartsWith(rootWithSeparator, PathComparison))
            throw new InvalidOperationException("Il path non appartiene al workspace TestLoom.");
        return Path.GetRelativePath(rootFull, full);
    }

    public static string ResolveRelativePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Un percorso di commit non può essere assoluto.");
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootWithSeparator = rootFull + Path.DirectorySeparatorChar;
        if (!full.Equals(rootFull, PathComparison) && !full.StartsWith(rootWithSeparator, PathComparison))
            throw new InvalidDataException("Un percorso di commit tenta di uscire dal workspace.");
        return full;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public readonly record struct WorkflowJsonMetadata(Guid TransactionId, string DeviceId, bool Deleted);

public sealed class WorkflowTransactionCommitReceipt
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public string DeviceId { get; set; } = "";
    public string Device { get; set; } = "";
    public string Description { get; set; } = "";
    public List<WorkflowTransactionCommitEntry> Entries { get; set; } = new();
}

public sealed class WorkflowTransactionCommitEntry
{
    public string RelativePath { get; set; } = "";
    public string FinalSha256 { get; set; } = "";
    public bool LogicallyDeleted { get; set; }
}
