using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TestLoom.App.Services;

/// <summary>
/// Per-PC crash-safe marker for explicit TICKET creation attempts. A lost HTTP response must
/// not allow a second POST simply by reopening the same TestLoom quick-edit dialog.
/// This is NOT a cross-device distributed lock; remote OneDrive reconciliation is separate.
/// </summary>
public sealed class PolarionCreationReceiptStore(DataPaths paths)
{
    private static readonly ConcurrentDictionary<string, object> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    public sealed class Receipt
    {
        public Guid OperationId { get; set; }
        public Guid SourceUid { get; set; }
        public string SourceId { get; set; } = "";
        public bool IsCr { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public string? CreatedTicketId { get; set; }
        public string? CreatedPortalUrl { get; set; }
        public string? BrowserCorrelationMarker { get; set; }
        public string? ExpectedTicketType { get; set; }
        public string? RequestedAssignee { get; set; }
        public string? OperatorAtHandoff { get; set; }
        /// <summary>Optional Test that initiated the NC/CR browser handoff.</summary>
        public Guid? OriginTestUid { get; set; }
        public string? OriginTestId { get; set; }
    }

    public Receipt? Read(bool isCr, string sourceId, Guid sourceUid)
    {
        var file = FilePath(isCr, sourceId, sourceUid);
        lock (FileGates.GetOrAdd(file, _ => new object()))
            return ReadFile(file);
    }

    /// <summary>Must succeed BEFORE a POST; an existing, even corrupt marker is fail-closed.</summary>
    public Receipt Begin(bool isCr, string sourceId, Guid sourceUid)
    {
        var file = FilePath(isCr, sourceId, sourceUid);
        lock (FileGates.GetOrAdd(file, _ => new object()))
        {
            var existing = ReadFile(file);
            if (existing is not null)
                throw new InvalidOperationException("An TICKET creation for this source is already pending reconciliation." +
                    (existing.CreatedTicketId is null ? "" : " Created ID: " + existing.CreatedTicketId));
            var receipt = new Receipt
            {
                OperationId = Guid.NewGuid(),
                SourceUid = sourceUid,
                SourceId = sourceId,
                IsCr = isCr,
                StartedUtc = DateTimeOffset.UtcNow
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            JsonIo.WriteAtomic(file, receipt, overwrite: false);
            return receipt;
        }
    }

    /// <summary>Atomically create a complete browser receipt before launching the browser.</summary>
    public Receipt BeginBrowser(bool isCr, string sourceId, Guid sourceUid, string marker,
        string ticketType, string? assignee, string operatorId, Guid? originTestUid = null,
        string? originTestId = null)
    {
        ValidateBrowserIdentity(marker, ticketType, originTestUid, originTestId);
        var file = FilePath(isCr, sourceId, sourceUid);
        lock (FileGates.GetOrAdd(file, _ => new object()))
        {
            if (ReadFile(file) is not null)
                throw new InvalidOperationException("An TICKET creation for this source is already pending reconciliation.");
            var receipt = new Receipt
            {
                OperationId = Guid.NewGuid(), SourceUid = sourceUid, SourceId = sourceId,
                IsCr = isCr, StartedUtc = DateTimeOffset.UtcNow,
                BrowserCorrelationMarker = marker, ExpectedTicketType = ticketType,
                RequestedAssignee = assignee, OperatorAtHandoff = operatorId,
                OriginTestUid = originTestUid, OriginTestId = originTestId
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            JsonIo.WriteAtomic(file, receipt, overwrite: false);
            return receipt;
        }
    }

    private static void ValidateBrowserIdentity(string marker, string ticketType, Guid? originTestUid, string? originTestId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(marker, @"^TestLoomREF[A-F0-9]{32}$"))
            throw new ArgumentException("Invalid browser correlation marker.", nameof(marker));
        if (ticketType is not (Models.PolarionTicketClassification.NonConformita or
            Models.PolarionTicketClassification.ChangeRequest))
            throw new ArgumentException("Invalid TICKET classification.", nameof(ticketType));
        if (originTestUid.HasValue != !string.IsNullOrWhiteSpace(originTestId) ||
            originTestUid == Guid.Empty || originTestId?.Length > 100)
            throw new ArgumentException("Origin Test identity must contain both UID and ID.");
    }

    /// <summary>Persist browser handoff identity BEFORE opening Ticketing; never launches a POST.</summary>
    public void MarkBrowserPrepared(Receipt receipt, string marker, string ticketType, string? assignee,
        string operatorId, Guid? originTestUid = null, string? originTestId = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateBrowserIdentity(marker, ticketType, originTestUid, originTestId);
        var path = FilePath(receipt.IsCr, receipt.SourceId, receipt.SourceUid);
        lock (FileGates.GetOrAdd(path, _ => new object()))
        {
            var current = ReadFile(path);
            if (current is null || current.OperationId != receipt.OperationId || current.BrowserCorrelationMarker is not null)
                throw new InvalidDataException("Browser creation receipt changed; no handoff permitted.");
            current.BrowserCorrelationMarker = marker;
            current.ExpectedTicketType = ticketType;
            current.RequestedAssignee = assignee;
            current.OperatorAtHandoff = operatorId;
            current.OriginTestUid = originTestUid;
            current.OriginTestId = originTestId;
            JsonIo.WriteAtomic(path, current);
            // Keep the in-memory receipt consistent with the durable record:
            // the UI uses this exact receipt to copy the marker-bearing description.
            receipt.BrowserCorrelationMarker = marker;
            receipt.ExpectedTicketType = ticketType;
            receipt.RequestedAssignee = assignee;
            receipt.OperatorAtHandoff = operatorId;
            receipt.OriginTestUid = originTestUid;
            receipt.OriginTestId = originTestId;
        }
    }

    /// <summary>
    /// Reads browser handoffs requiring reconciliation, including a verified-created
    /// receipt whose cleanup was interrupted after some TestLoom links were saved.
    /// These receipts must remain discoverable after app restart.
    /// </summary>
    public IReadOnlyList<Receipt> PendingBrowserReceipts() => PendingBrowserReceipts(out _);

    /// <summary>
    /// Read-only receipt scan with a completeness flag for UI presentation.
    /// A corrupt receipt stays on disk and blocks its source, but callers must
    /// not mistake a partial scan for zero pending handoffs.
    /// </summary>
    public IReadOnlyList<Receipt> PendingBrowserReceipts(out bool hasUnreadable)
    {
        hasUnreadable = false;
        var dir = Path.GetDirectoryName(FilePath(false, "placeholder", Guid.NewGuid()))!;
        if (!Directory.Exists(dir)) return Array.Empty<Receipt>();
        var all = Directory.EnumerateFiles(dir, "*.json").Take(129).ToArray();
        if (all.Length > 128)
            throw new InvalidDataException("Too many pending TICKET handoffs; manual review required.");
        var result = new List<Receipt>();
        foreach (var file in all)
        {
            try
            {
                var item = ReadFile(file);
                if (item is not null && item.BrowserCorrelationMarker is not null)
                    result.Add(item);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                hasUnreadable = true;
                // Keep the unreadable receipt untouched and fail closed for its
                // NC/CR (Read/Begin still reject it), but do not starve unrelated
                // browser handoffs. Never log paths, exception messages, or data.
                DiagnosticTraceService.Registra("TICKET.Receipt",
                    "unreadable-local-receipt kind=" + ex.GetType().Name);
            }
        }
        return result.OrderBy(x => x.StartedUtc).ToList();
    }

    /// <summary>Called immediately after the validated successful POST and before TestLoom linking.</summary>
    public void MarkCreated(Receipt receipt, PolarionWriteService.Created created)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(created);
        var file = FilePath(receipt.IsCr, receipt.SourceId, receipt.SourceUid);
        lock (FileGates.GetOrAdd(file, _ => new object()))
        {
            var current = ReadFile(file);
            if (current is null || current.OperationId != receipt.OperationId)
                throw new IOException("TICKET creation receipt is missing or was replaced; cannot reconcile.");
            if (current.CreatedTicketId is not null &&
                !current.CreatedTicketId.Equals(created.TicketId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("TICKET receipt already points to a different created ticket.");
            current.CreatedTicketId = created.TicketId;
            current.CreatedPortalUrl = created.PortalUrl;
            JsonIo.WriteAtomic(file, current);
            receipt.CreatedTicketId = created.TicketId;
            receipt.CreatedPortalUrl = created.PortalUrl;
        }
    }

    /// <summary>
    /// Only call after a definite HTTP rejection or when TestLoom link was confirmed persisted.
    /// Never clear for timeout, 5xx, ambiguous response or created-but-unlinked tickets.
    /// </summary>
    public void Clear(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var file = FilePath(receipt.IsCr, receipt.SourceId, receipt.SourceUid);
        lock (FileGates.GetOrAdd(file, _ => new object()))
        {
            var current = ReadFile(file);
            if (current?.OperationId != receipt.OperationId)
                throw new IOException("Cannot clear an TICKET receipt owned by another operation.");
            File.Delete(file);
        }
    }

    private static Receipt? ReadFile(string file)
    {
        if (!File.Exists(file)) return null;
        return JsonIo.TryRead<Receipt>(file, out var error) ??
            throw new InvalidDataException("An existing TICKET creation receipt is corrupt. Manual reconciliation is required.",
                error is null ? null : new InvalidDataException(error));
    }

    private string FilePath(bool isCr, string sourceId, Guid sourceUid)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 100 || sourceUid == Guid.Empty)
            throw new ArgumentException("Invalid TestLoom source identity for TICKET receipt.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (isCr ? "CR:" : "NC:") + sourceUid.ToString("N"))))[..32];
        return Path.Combine(WorkspaceLocalPaths.LocalApplicationDataRoot, "TestLoom", "workspaces",
            WorkspaceLocalPaths.ComputeWorkspaceKey(paths.Root), "ticketing-create-receipts", hash + ".json");
    }
}
