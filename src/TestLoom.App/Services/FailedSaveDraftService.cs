using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Conserva una copia NON AUTOREVOLE quando un salvataggio di dominio fallisce.
/// I file restano sulla postazione corrente in %LOCALAPPDATA%/TestLoom/recovery-drafts
/// (FUORI dal workspace OneDrive e FUORI dagli indici ricostruibili).
/// Non li ricarica né pubblica automaticamente: il ripristino richiede revisione
/// di UID, revisione base, snapshot remoto e consenso esplicito.
/// </summary>
public static class FailedSaveDraftService
{
    public sealed class DraftEnvelope
    {
        public int Schema { get; set; } = 1;
        public string Domain { get; set; } = "";
        public Guid EntityUid { get; set; }
        public long ExpectedRevision { get; set; }
        public string Operator { get; set; } = "";
        public DateTime CapturedUtc { get; set; }
        public string Error { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        // Schema 1 optional fields: legacy drafts still deserialize unchanged.
        public string? BasePayloadJson { get; set; }
        public string? BaseSha256 { get; set; }
    }

    public static string LocalDirectory(string sharedRoot, string? localBaseDirectory = null)
    {
        var baseDir = localBaseDirectory ?? Path.Combine(
            WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "recovery-drafts");
        return Path.Combine(baseDir, WorkspaceLocalPaths.ComputeWorkspaceKey(sharedRoot));
    }

    /// <summary>Il dominio deve essere un valore controllato dal codice, mai un nome file fornito dall'utente.</summary>
    public static string Save<T>(string sharedRoot, string domain, Guid entityUid, long expectedRevision,
        string operatorName, T draft, string reason, string? localBaseDirectory = null,
        object? baseSnapshot = null)
    {
        if (domain is not ("change-request" or "nonconformita" or "segnalazione"))
            throw new ArgumentOutOfRangeException(nameof(domain));
        if (draft == null) throw new ArgumentNullException(nameof(draft));
        var directory = LocalDirectory(sharedRoot, localBaseDirectory);
        Directory.CreateDirectory(directory);
        var envelope = new DraftEnvelope
        {
            Domain = domain,
            EntityUid = entityUid,
            ExpectedRevision = expectedRevision,
            Operator = operatorName,
            CapturedUtc = DateTime.UtcNow,
            Error = reason,
            PayloadJson = JsonSerializer.Serialize(draft, JsonIo.Options),
            BasePayloadJson = baseSnapshot == null ? null : JsonSerializer.Serialize(baseSnapshot, JsonIo.Options)
        };
        if (envelope.BasePayloadJson != null)
            envelope.BaseSha256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(envelope.BasePayloadJson)));
        // Create-only semantics: un salvataggio fallito non deve mai sovrascrivere
        // una bozza precedente (anche se la stessa entita fallisce più volte).
        var path = Path.Combine(directory, domain + "_" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") +
            "_" + Guid.NewGuid().ToString("N") + ".json");
        JsonIo.WriteAtomic(path, envelope);
        return path;
    }
}
