using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// PREPARES an explicitly confirmed local, immutable recovery candidate.
/// Never restores to the authoritative TestLoom workspace. Refuses base missing,
/// stale revisions, changed/removed canonical document and observed forks.
/// Prepare remains non-authoritative; separate CR/NC adapters can stage selected fields in their editors.
/// </summary>
public static class FailedSaveDraftRecoveryPreparationService
{
    public sealed record Preparation(string LocalCandidatePath, Guid EntityUid, string Domain);

    /// <summary>Only these user-editable CR fields may be copied from a recovery draft. Identity,
    /// revision history, workflow state and audit fields always remain from the canonical base.</summary>
    public static IReadOnlyDictionary<string, string> EditableChangeRequestFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(ChangeRequest.TemplateUid)] = "Template",
            [nameof(ChangeRequest.Titolo)] = "Titolo",
            [nameof(ChangeRequest.DescrizioneBreve)] = "Descrizione breve",
            [nameof(ChangeRequest.Descrizione)] = "Descrizione",
            [nameof(ChangeRequest.MotivazioneDuplicato)] = "Motivazione duplicato",
            [nameof(ChangeRequest.Responsabile)] = "Responsabile",
            [nameof(ChangeRequest.VeicoliImpattati)] = "Veicoli impattati",
            [nameof(ChangeRequest.EcuImpattate)] = "Componente impattate",
            [nameof(ChangeRequest.StrumentiImpattati)] = "Strumenti impattati",
            [nameof(ChangeRequest.CampiCustom)] = "Campi personalizzati",
            [nameof(ChangeRequest.TicketCollegati)] = "Ticket collegati",
            [nameof(ChangeRequest.Allegati)] = "Allegati",
            [nameof(ChangeRequest.CrCorrelate)] = "CR correlate",
            [nameof(ChangeRequest.TestValidazione)] = "Test di validazione"
        };

    public static IReadOnlyDictionary<string, string> EditableNetworkNcFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(NonConformita.Descrizione)] = "Descrizione dettagliata",
            [nameof(NonConformita.DescrizioneBreveSegnalata)] = "Descrizione breve",
            [nameof(NonConformita.ConcessionariaOfficina)] = "Concessionaria / officina",
            [nameof(NonConformita.Owner)] = "Responsabile",
            [nameof(NonConformita.Severita)] = "Severità",
            [nameof(NonConformita.Priorita)] = "Priorità",
            [nameof(NonConformita.StatoWorkflow)] = "Stato",
            [nameof(NonConformita.DataChiusura)] = "Data di chiusura",
            [nameof(NonConformita.EcuSegnalate)] = "Componente segnalate",
            [nameof(NonConformita.VeicoliSegnalati)] = "Veicoli segnalati",
            [nameof(NonConformita.FunzionalitaSegnalate)] = "Funzionalità segnalate",
            [nameof(NonConformita.StrumentoSegnalato)] = "Strumento",
            [nameof(NonConformita.ValoriStrumentoSegnalato)] = "Campi strumento",
            [nameof(NonConformita.OperazioniSegnalate)] = "Operazioni segnalate",
            [nameof(NonConformita.NumeriPraticaRete)] = "Pratiche rete",
            [nameof(NonConformita.VinCliente)] = "Matricola cliente",
            [nameof(NonConformita.Licenze)] = "Licenze",
            [nameof(NonConformita.Allegati)] = "Allegati",
            [nameof(NonConformita.CollegamentiPolarion)] = "Ticket collegati"
        };

    public static IReadOnlyDictionary<string, string> EditableAnomaliaDetailFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(NonConformita.Severita)] = "Severità",
            [nameof(NonConformita.Priorita)] = "Priorità",
            [nameof(NonConformita.Owner)] = "Responsabile",
            [nameof(NonConformita.Destinatario)] = "Destinatario",
            [nameof(NonConformita.CausaProbabile)] = "Causa probabile",
            [nameof(NonConformita.CausaProbabileAltro)] = "Altra causa",
            [nameof(NonConformita.StatoDettaglio)] = "Stato di dettaglio",
            [nameof(NonConformita.MotivoChiusura)] = "Motivo di chiusura",
            [nameof(NonConformita.DataChiusura)] = "Data di chiusura",
            [nameof(NonConformita.CollegamentiPolarion)] = "Ticket collegati"
        };

    /// <summary>
    /// Reads a Change Request draft for explicit staging in its existing editor. This never writes to the
    /// shared workspace. A stale captured base is allowed only when the current canonical file still matches
    /// the exact editor snapshot; the selection dialog shows the three-way diff before staging.
    /// </summary>
    public static ChangeRequest LoadChangeRequestForEditor(string sharedRoot,
        FailedSaveDraftInspector.DraftRow row, ChangeRequest expectedSnapshot,
        long expectedRevision, string? localBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        var expectedUid = expectedSnapshot.Uid;
        var expectedId = expectedSnapshot.Id;
        if (!row.IsReadable || row.Domain != "change-request" || row.EntityUid != expectedUid ||
            expectedUid == Guid.Empty || !ValidId(expectedId))
            throw new InvalidDataException("La bozza non corrisponde alla Change Request aperta.");

        var folder = Path.GetFullPath(FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory));
        var source = Path.GetFullPath(row.FilePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(source), folder, comparison) ||
            !source.EndsWith(".json", comparison))
            throw new UnauthorizedAccessException("Bozza estranea al workspace corrente.");

        var info = new FileInfo(source);
        if (info.LinkTarget != null || info.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("La bozza è un collegamento o supera il limite consentito.");
        var draftBytes = JsonIo.ReadAllBytesShared(source);
        if (draftBytes.Length > 8 * 1024 * 1024 ||
            !Convert.ToHexString(SHA256.HashData(draftBytes))
            .Equals(row.ContentSha256, StringComparison.Ordinal))
            throw new IOException("La bozza è cambiata dopo la selezione.");

        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(draftBytes, out var error)
            ?? throw new InvalidDataException(error ?? "Bozza illeggibile.");
        if (error != null || envelope.Schema != 1 || envelope.Domain != "change-request" ||
            envelope.EntityUid != expectedUid || envelope.ExpectedRevision != row.ExpectedRevision ||
            string.IsNullOrWhiteSpace(envelope.PayloadJson) ||
            string.IsNullOrWhiteSpace(envelope.BasePayloadJson) ||
            string.IsNullOrWhiteSpace(envelope.BaseSha256))
            throw new InvalidDataException("La bozza non contiene una base verificabile per questa revisione.");

        var baseBytes = Encoding.UTF8.GetBytes(envelope.BasePayloadJson);
        if (!Convert.ToHexString(SHA256.HashData(baseBytes))
            .Equals(envelope.BaseSha256, StringComparison.Ordinal))
            throw new InvalidDataException("La fotografia iniziale della bozza non è integra.");

        using var baseline = JsonDocument.Parse(envelope.BasePayloadJson);
        using var draftDocument = JsonDocument.Parse(envelope.PayloadJson);
        using var editorSnapshot = JsonDocument.Parse(JsonSerializer.Serialize(expectedSnapshot, JsonIo.Options));
        var baseRoot = baseline.RootElement;
        var draftRoot = draftDocument.RootElement;
        if (ReadUid(baseRoot) != expectedUid || ReadId(baseRoot) != expectedId ||
            ReadUid(draftRoot) != expectedUid || ReadId(draftRoot) != expectedId ||
            ReadRevision(baseRoot) != envelope.ExpectedRevision ||
            ReadRevision(draftRoot) != envelope.ExpectedRevision)
            throw new InvalidDataException("UID, ID o revisione della bozza non corrispondono alla sua fotografia iniziale.");

        var canonical = new DataPaths(sharedRoot).ChangeRequestFile(expectedId);
        if (!File.Exists(canonical))
            throw new IOException("La Change Request canonica non è presente localmente.");
        VisibleConflictWriteGuard.EnsureNoVisibleFork(canonical, expectedUid);
        var canonicalInfo = new FileInfo(canonical);
        if (canonicalInfo.LinkTarget != null || canonicalInfo.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Il documento canonico è un collegamento o supera il limite consentito.");
        var canonicalBytes = JsonIo.ReadAllBytesShared(canonical);
        if (canonicalBytes.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Il documento canonico è cresciuto oltre il limite consentito durante la lettura.");
        using var current = JsonDocument.Parse(canonicalBytes);
        var currentRoot = current.RootElement;
        if (!JsonElement.DeepEquals(editorSnapshot.RootElement, currentRoot))
            throw new IOException("Il documento locale è cambiato dopo l'apertura dell'editor. Nessun campo importato.");
        if (ReadUid(currentRoot) != expectedUid || ReadId(currentRoot) != expectedId ||
            ReadRevision(currentRoot) != expectedRevision ||
            currentRoot.TryGetProperty("_ditmDeleted", out var tombstone) &&
            tombstone.ValueKind == JsonValueKind.True)
            throw new IOException("La versione locale non è più quella attesa o è stata eliminata.");

        var draft = JsonIo.TryParse<ChangeRequest>(Encoding.UTF8.GetBytes(envelope.PayloadJson), out var payloadError);
        if (draft == null || payloadError != null || draft.Uid != expectedUid || draft.Id != expectedId ||
            draft.RevisioneDati != envelope.ExpectedRevision || draft.VeicoliImpattati == null ||
            draft.EcuImpattate == null || draft.StrumentiImpattati == null || draft.CampiCustom == null ||
            draft.TicketCollegati == null || draft.Allegati == null || draft.CrCorrelate == null ||
            draft.TestValidazione == null)
            throw new InvalidDataException(payloadError ?? "Payload Change Request non valido.");
        return draft;
    }

    public static NonConformita LoadNetworkNcForEditor(string sharedRoot,
        FailedSaveDraftInspector.DraftRow row, NonConformita expectedSnapshot,
        long expectedRevision, string? localBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        if (!row.IsReadable || row.Domain is not ("nonconformita" or "segnalazione") ||
            row.EntityUid != expectedSnapshot.Uid || expectedSnapshot.Uid == Guid.Empty ||
            !ValidId(expectedSnapshot.Id))
            throw new InvalidDataException("La bozza non corrisponde alla Non Conformità aperta.");

        var folder = Path.GetFullPath(FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory));
        var source = Path.GetFullPath(row.FilePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(source), folder, comparison) ||
            !source.EndsWith(".json", comparison))
            throw new UnauthorizedAccessException("Bozza estranea al workspace corrente.");
        var info = new FileInfo(source);
        if (info.LinkTarget != null || info.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("La bozza è un collegamento o supera il limite consentito.");
        var draftBytes = JsonIo.ReadAllBytesShared(source);
        if (draftBytes.Length > 8 * 1024 * 1024 ||
            !Convert.ToHexString(SHA256.HashData(draftBytes)).Equals(row.ContentSha256, StringComparison.Ordinal))
            throw new IOException("La bozza è cambiata dopo la selezione.");

        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(draftBytes, out var error)
            ?? throw new InvalidDataException(error ?? "Bozza illeggibile.");
        if (error != null || envelope.Schema != 1 || envelope.Domain != row.Domain ||
            envelope.EntityUid != expectedSnapshot.Uid || envelope.ExpectedRevision != row.ExpectedRevision ||
            string.IsNullOrWhiteSpace(envelope.PayloadJson) ||
            string.IsNullOrWhiteSpace(envelope.BasePayloadJson) ||
            string.IsNullOrWhiteSpace(envelope.BaseSha256))
            throw new InvalidDataException("La bozza non contiene una base verificabile per questa revisione.");
        if (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.BasePayloadJson)))
            .Equals(envelope.BaseSha256, StringComparison.Ordinal))
            throw new InvalidDataException("La fotografia iniziale della bozza non è integra.");

        using var baseline = JsonDocument.Parse(envelope.BasePayloadJson);
        using var draftDocument = JsonDocument.Parse(envelope.PayloadJson);
        using var editorSnapshot = JsonDocument.Parse(JsonSerializer.Serialize(expectedSnapshot, JsonIo.Options));
        var baseRoot = baseline.RootElement;
        var draftRoot = draftDocument.RootElement;
        if (ReadUid(baseRoot) != expectedSnapshot.Uid || ReadId(baseRoot) != expectedSnapshot.Id ||
            ReadRevision(baseRoot) != envelope.ExpectedRevision || ReadUid(draftRoot) != expectedSnapshot.Uid ||
            ReadId(draftRoot) != expectedSnapshot.Id || ReadRevision(draftRoot) != envelope.ExpectedRevision)
            throw new InvalidDataException("UID, ID o revisione della bozza non corrispondono alla sua fotografia iniziale.");

        var paths = new DataPaths(sharedRoot);
        var canonical = paths.NonConformitaFile(expectedSnapshot.Id);
        if (!File.Exists(canonical)) throw new IOException("La Non Conformità canonica non è presente localmente.");
        VisibleConflictWriteGuard.EnsureNoVisibleFork(canonical, expectedSnapshot.Uid);
        var canonicalInfo = new FileInfo(canonical);
        if (canonicalInfo.LinkTarget != null || canonicalInfo.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Il documento canonico è un collegamento o supera il limite consentito.");
        var canonicalBytes = JsonIo.ReadAllBytesShared(canonical);
        if (canonicalBytes.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Il documento canonico è cresciuto oltre il limite consentito durante la lettura.");
        using var current = JsonDocument.Parse(canonicalBytes);
        var currentRoot = current.RootElement;
        if (!JsonElement.DeepEquals(editorSnapshot.RootElement, currentRoot) ||
            ReadUid(currentRoot) != expectedSnapshot.Uid || ReadId(currentRoot) != expectedSnapshot.Id ||
            ReadRevision(currentRoot) != expectedRevision ||
            currentRoot.TryGetProperty("_ditmDeleted", out var tombstone) && tombstone.ValueKind == JsonValueKind.True)
            throw new IOException("La versione locale è cambiata dopo l'apertura dell'editor o è stata eliminata.");

        var draft = JsonIo.TryParse<NonConformita>(Encoding.UTF8.GetBytes(envelope.PayloadJson), out var payloadError);
        if (draft == null || payloadError != null || draft.Uid != expectedSnapshot.Uid ||
            draft.Id != expectedSnapshot.Id || draft.RevisioneDati != envelope.ExpectedRevision ||
            draft.EcuSegnalate == null || draft.VeicoliSegnalati == null ||
            draft.FunzionalitaSegnalate == null || draft.ValoriStrumentoSegnalato == null ||
            draft.OperazioniSegnalate == null || draft.NumeriPraticaRete == null ||
            draft.VinCliente == null || draft.Licenze == null || draft.Allegati == null ||
            draft.CollegamentiPolarion == null)
            throw new InvalidDataException(payloadError ?? "Payload Non Conformità non valido.");
        return draft;
    }

    /// <summary>
    /// Criterio di elenco "nuovo record" (sola lettura, senza eccezioni): nessuna fotografia iniziale, payload coerente
    /// con dominio e UID, per una NC di rete la bozza deve venire da un singolo salvataggio (non dallo split per
    /// funzionalita, che puo' aver gia creato NC sorelle), e nessun tombstone deve riportare lo stesso UID.
    /// </summary>
    public static bool NuovoRecordAmmissibile(string sharedRoot, FailedSaveDraftInspector.DraftRow row, string path,
        string? localBaseDirectory = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget != null || info.Length > 8 * 1024 * 1024) return false;
            var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(JsonIo.ReadAllBytesShared(path), out var error);
            if (envelope == null || error != null || envelope.Schema != 1 || envelope.Domain != row.Domain ||
                envelope.EntityUid != row.EntityUid || string.IsNullOrWhiteSpace(envelope.PayloadJson) ||
                !string.IsNullOrEmpty(envelope.BasePayloadJson) || !string.IsNullOrEmpty(envelope.BaseSha256))
                return false;
            using var payload = JsonDocument.Parse(envelope.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadUid(root) != row.EntityUid) return false;
            if (row.Domain != "change-request")
            {
                if (!root.TryGetProperty("DaSegnalazioneRete", out var rete) || rete.ValueKind != JsonValueKind.True)
                    return false;
                if (root.TryGetProperty("FunzionalitaSegnalate", out var funzionalita) &&
                    funzionalita.ValueKind == JsonValueKind.Array && funzionalita.GetArrayLength() > 1)
                    return false;
            }
            return !UidUsatoDaTombstone(sharedRoot, row.Domain, ReadId(root), row.EntityUid) &&
                   !UidNelRecoveryBin(sharedRoot, row.Domain, row.EntityUid);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Un record eliminato (anche con un altro ID) finisce nel recovery-bin con il payload completo: una
    /// bozza "nuovo record" con lo stesso UID lo resusciterebbe sotto un nuovo ID. Va ripristinato dal recovery-bin.</summary>
    private static bool UidNelRecoveryBin(string sharedRoot, string domain, Guid uid)
    {
        var categoria = domain == "change-request" ? DomainRecoveryCategory.ChangeRequest : DomainRecoveryCategory.NonConformita;
        foreach (var record in DomainRecoveryService.GetAll(new DataPaths(sharedRoot)).Where(r => r.Category == categoria))
        {
            try
            {
                using var doc = JsonDocument.Parse(Convert.FromBase64String(record.PayloadBase64));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && ReadUid(doc.RootElement) == uid) return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return true; // record illeggibile: per prudenza la bozza non è proposta come nuovo record
            }
        }
        return false;
    }

    /// <summary>
    /// Carica in un editor NUOVO una bozza di CR mai salvata: solo campi modificabili, nessuna fotografia iniziale,
    /// nessuna scrittura. L'ID della bozza non viene mai riutilizzato (il Save assegna NextId()); l'UID resta quello
    /// della bozza solo se ancora libero, altrimenti ne viene generato uno nuovo.
    /// </summary>
    public static ChangeRequest LoadNewChangeRequestForEditor(string sharedRoot,
        FailedSaveDraftInspector.DraftRow row, Func<Guid, bool> uidGiaPresente, string? localBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(uidGiaPresente);
        var envelope = LeggiBozzaNuovoRecord(sharedRoot, row, "change-request", localBaseDirectory);
        var draft = JsonIo.TryParse<ChangeRequest>(Encoding.UTF8.GetBytes(envelope.PayloadJson), out var payloadError);
        if (draft == null || payloadError != null || draft.Uid != row.EntityUid ||
            draft.VeicoliImpattati == null || draft.EcuImpattate == null || draft.StrumentiImpattati == null ||
            draft.CampiCustom == null || draft.TicketCollegati == null || draft.Allegati == null ||
            draft.CrCorrelate == null || draft.TestValidazione == null)
            throw new InvalidDataException(payloadError ?? "Payload Change Request non valido.");
        draft.Id = "";
        draft.RevisioneDati = 0;
        draft.Uid = uidGiaPresente(row.EntityUid) ? Guid.NewGuid() : row.EntityUid;
        return draft;
    }

    /// <summary>
    /// Carica in un editor NUOVO una bozza di NC di rete mai salvata (stesse garanzie di
    /// <see cref="LoadNewChangeRequestForEditor"/>). Accetta anche le bozze storiche etichettate "segnalazione" con ID.
    /// </summary>
    public static NonConformita LoadNewNetworkNcForEditor(string sharedRoot,
        FailedSaveDraftInspector.DraftRow row, Func<Guid, bool> uidGiaPresente, string? localBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(uidGiaPresente);
        if (row.Domain is not ("nonconformita" or "segnalazione"))
            throw new InvalidDataException("La bozza non è una Non Conformità di rete.");
        var envelope = LeggiBozzaNuovoRecord(sharedRoot, row, row.Domain, localBaseDirectory);
        var draft = JsonIo.TryParse<NonConformita>(Encoding.UTF8.GetBytes(envelope.PayloadJson), out var payloadError);
        if (draft == null || payloadError != null || draft.Uid != row.EntityUid || !draft.DaSegnalazioneRete ||
            draft.EcuSegnalate == null || draft.VeicoliSegnalati == null || draft.FunzionalitaSegnalate == null ||
            draft.ValoriStrumentoSegnalato == null || draft.OperazioniSegnalate == null || draft.NumeriPraticaRete == null ||
            draft.VinCliente == null || draft.Licenze == null || draft.Allegati == null || draft.CollegamentiPolarion == null)
            throw new InvalidDataException(payloadError ?? "Payload Non Conformità non valido.");
        var idTentato = draft.Id;
        draft.Id = "";
        draft.RevisioneDati = 0;
        var uidUsato = uidGiaPresente(row.EntityUid) || UidUsatoDaTombstone(sharedRoot, row.Domain, idTentato, row.EntityUid);
        draft.Uid = uidUsato ? Guid.NewGuid() : row.EntityUid;
        return draft;
    }

    private static FailedSaveDraftService.DraftEnvelope LeggiBozzaNuovoRecord(string sharedRoot,
        FailedSaveDraftInspector.DraftRow row, string domain, string? localBaseDirectory)
    {
        if (!row.IsReadable || row.Domain != domain || row.EntityUid == Guid.Empty)
            throw new InvalidDataException("La bozza non corrisponde al tipo di record nuovo richiesto.");
        var folder = Path.GetFullPath(FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory));
        var source = Path.GetFullPath(row.FilePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(source), folder, comparison) ||
            !source.EndsWith(".json", comparison))
            throw new UnauthorizedAccessException("Bozza estranea al workspace corrente.");
        var info = new FileInfo(source);
        if (info.LinkTarget != null || info.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("La bozza è un collegamento o supera il limite consentito.");
        var bytes = JsonIo.ReadAllBytesShared(source);
        if (bytes.Length > 8 * 1024 * 1024 ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(row.ContentSha256, StringComparison.Ordinal))
            throw new IOException("La bozza è cambiata dopo la selezione.");
        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(bytes, out var error)
            ?? throw new InvalidDataException(error ?? "Bozza illeggibile.");
        if (error != null || envelope.Schema != 1 || envelope.Domain != domain || envelope.EntityUid != row.EntityUid ||
            envelope.ExpectedRevision != row.ExpectedRevision || string.IsNullOrWhiteSpace(envelope.PayloadJson))
            throw new InvalidDataException("Metadati della bozza non validi per un record nuovo.");
        if (!string.IsNullOrEmpty(envelope.BasePayloadJson) || !string.IsNullOrEmpty(envelope.BaseSha256))
            throw new InvalidDataException("La bozza ha una fotografia iniziale: appartiene a un record esistente, non a uno nuovo.");
        return envelope;
    }

    /// <summary>True se l'ID riportato dalla bozza e' un tombstone che conserva lo stesso UID: l'UID e' gia stato usato.</summary>
    private static bool UidUsatoDaTombstone(string sharedRoot, string domain, string? id, Guid uid)
    {
        if (!ValidId(id)) return false;
        var paths = new DataPaths(sharedRoot);
        var path = domain == "change-request" ? paths.ChangeRequestFile(id!) : paths.NonConformitaFile(id!);
        return File.Exists(path) &&
            WorkflowTransactionVisibilityService.TryReadTombstoneIdentity(path, out var tombstoneUid, out _) &&
            tombstoneUid == uid;
    }

    public static Preparation Prepare(string sharedRoot,
        FailedSaveDraftInspector.DraftRow row, bool userConfirmed,
        string? localBaseDirectory = null)
    {
        if (!userConfirmed)
            throw new OperationCanceledException("Preparazione annullata dall'utente.");
        if (!row.IsReadable)
            throw new InvalidDataException("La bozza non è leggibile.");

        var folder = Path.GetFullPath(FailedSaveDraftService.LocalDirectory(sharedRoot, localBaseDirectory));
        var source = Path.GetFullPath(row.FilePath);
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(source), folder, cmp))
            throw new UnauthorizedAccessException("Bozza estranea al workspace corrente.");

        // Compare re-reads and checks SHA-256 of the selected draft.
        var comparison = FailedSaveDraftComparisonService.Compare(sharedRoot, row, localBaseDirectory);
        if (comparison.State != FailedSaveDraftComparisonService.ComparisonState.DifferentLocal)
            throw new IOException("Preparazione non consentita: " + comparison.StateLabel +
                ". Confrontare manualmente con il Master.");

        var draftBytes = JsonIo.ReadAllBytesShared(source);
        var envelope = JsonIo.TryParse<FailedSaveDraftService.DraftEnvelope>(draftBytes, out var error)
            ?? throw new InvalidDataException(error ?? "Bozza illeggibile.");
        if (envelope.EntityUid != row.EntityUid || envelope.Domain != row.Domain ||
            !Convert.ToHexString(SHA256.HashData(draftBytes))
                .Equals(row.ContentSha256, StringComparison.Ordinal))
            throw new IOException("La bozza è cambiata dopo la selezione.");
        if (envelope.BasePayloadJson == null || envelope.BaseSha256 == null)
            throw new IOException("Fotografia iniziale assente: recupero guidato non sicuro.");
        if (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.BasePayloadJson)))
            .Equals(envelope.BaseSha256, StringComparison.Ordinal))
            throw new IOException("Fotografia iniziale alterata.");

        using var baseline = JsonDocument.Parse(envelope.BasePayloadJson);
        using var draft = JsonDocument.Parse(envelope.PayloadJson);
        // Preview can be truncated to 32 kB, so never use that as the source
        // of truth when validating a recovery operation.
        var canonicalId = ReadId(draft.RootElement);
        var isNetworkNc = row.Domain == "nonconformita" ||
            row.Domain == "segnalazione" && canonicalId != null;
        var requiresId = row.Domain == "change-request" || isNetworkNc;
        if (requiresId && !ValidId(canonicalId))
            throw new InvalidDataException("Identificativo non valido.");

        var paths = new DataPaths(sharedRoot);
        var canonical = row.Domain switch
        {
            "change-request" => paths.ChangeRequestFile(canonicalId!),
            "nonconformita" => paths.NonConformitaFile(canonicalId!),
            "segnalazione" => isNetworkNc
                ? paths.NonConformitaFile(canonicalId!) : paths.SegnalazioneFile(row.EntityUid),
            _ => throw new InvalidDataException("Dominio non supportato.")
        };
        if (!File.Exists(canonical))
            throw new IOException("Il documento canonico non è più presente.");
        // A visible fork is enough to block this candidate; remote unsynced
        // changes remain unverifiable without the physical OneDrive pilot.
        VisibleConflictWriteGuard.EnsureNoVisibleFork(canonical, row.EntityUid);

        var original = JsonIo.ReadAllBytesShared(canonical);
        using var latest = JsonDocument.Parse(original);
        if (!JsonElement.DeepEquals(baseline.RootElement, latest.RootElement))
            throw new IOException("Il documento è cambiato rispetto alla base: nessun recupero preparato.");
        if (!ReadUid(latest.RootElement).Equals(row.EntityUid) ||
            (requiresId &&
             ReadId(latest.RootElement) != canonicalId) ||
            latest.RootElement.TryGetProperty("_ditmDeleted", out var tombstone) &&
            tombstone.ValueKind == JsonValueKind.True)
            throw new IOException("Documento canonico non corrispondente o eliminato.");
        if (!latest.RootElement.TryGetProperty("RevisioneDati", out var rev) ||
            !rev.TryGetInt64(out var version) || version != envelope.ExpectedRevision)
            throw new IOException("Revisione corrente non corrispondente alla fotografia attesa.");

        var outDir = Path.Combine(folder, "prepared");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, "NON_AUTOREVOLE_" +
            envelope.Domain + "_" + Guid.NewGuid().ToString("N") + ".json");
        // The source cannot be overwritten; this is a new file under local
        // application data. It is never indexed or copied into shared storage.
        JsonIo.WriteAtomic(path, new
        {
            Schema = 1,
            IsAuthoritative = false,
            RequiresMasterReview = true,
            Envelope = envelope,
            CanonicalSha256AtPreparation = Convert.ToHexString(SHA256.HashData(original)),
            PreparedUtc = DateTime.UtcNow,
            Warning = "Copia locale NON applicata. Sincronizzazione della cartella condivisa non verificata."
        });
        return new(path, row.EntityUid, row.Domain);
    }

    private static string? ReadId(JsonElement x) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty("Id", out var id) &&
        id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private static Guid ReadUid(JsonElement x) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty("Uid", out var id) &&
        id.ValueKind == JsonValueKind.String &&
        Guid.TryParse(id.GetString(), out var uid) ? uid : Guid.Empty;

    private static long? ReadRevision(JsonElement x) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty("RevisioneDati", out var rev) &&
        rev.ValueKind == JsonValueKind.Number && rev.TryGetInt64(out var value) ? value : null;

    private static bool ValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") && id.Length <= 180 &&
        id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
