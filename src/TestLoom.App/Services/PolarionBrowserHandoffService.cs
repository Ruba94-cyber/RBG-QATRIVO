using System.Diagnostics;
using Avalonia.Threading;
using System.Text;
using System.Text.RegularExpressions;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Browser-first TICKET creation. TestLoom prepares a unique, searchable draft, but does NOT
/// create an TICKET by opening a URL. The user saves in the real Ticketing UI. Only a
/// verified Ticketing GET (correct ID/type/unique marker/creation time) may link TestLoom.
/// </summary>
public static class PolarionBrowserHandoffService
{
    private static string L(string it, string en, string fr) => LocalizationService.Instance.Scegli(it, en, fr);

    private static readonly Regex Marker = new(@"^TestLoomREF[A-F0-9]{32}$", RegexOptions.CultureInvariant);
    public sealed record Prepared(PolarionCreationReceiptStore.Receipt Receipt, string ClipboardText,
        Uri BrowserUrl, bool FullPrefillAttempted = false, string? PrefillNotice = null);
    public sealed record CheckResult(bool Linked, bool Pending, string? TicketId, string Message);

    /// <summary>
    /// Ticketing 2512 browser form deep link: form_mode=create and form_field_*
    /// can prefill type and string fields. Browser GET only; no ticket is persisted
    /// until the operator explicitly saves in the authenticated Ticketing UI.
    /// Only title is placed in the URL: descriptions/evidence may be confidential,
    /// and rich-text and enum custom fields do not have a verified URL contract.
    /// </summary>
    public static Uri BuildCreateFormUrl(Uri apiRoot, string projectId, string title)
    {
        ArgumentNullException.ThrowIfNull(apiRoot);
        if (!apiRoot.IsAbsoluteUri || apiRoot.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(projectId) || !Regex.IsMatch(projectId, @"^[A-Za-z][A-Za-z0-9_]*$",
                RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(title) || title.Length > 512)
            throw new ArgumentException("Valid HTTPS Ticketing host, project ID and title are required.");
        var form = "/ticketing/#/project/" + Uri.EscapeDataString(projectId) +
            "/workitem?form_mode=create&form_field_type=TICKET_Ticket" +
            "&form_field_title=" + Uri.EscapeDataString(title.Trim());
        return new Uri(new Uri(apiRoot.GetLeftPart(UriPartial.Authority)), form);
    }

    // This is a best-effort, local browser-fragment transport, not a guaranteed
    // Ticketing field-edit API. Stay below a conservative browser history URL size.
    // The fragment is not sent in the HTTP request but can remain in local history.
    private const int MaxFormUrlLength = 16000;

    /// <summary>
    /// Experimental full form prefill. Ticketing documentation guarantees only type
    /// and string-field URL defaults; enum, rich-text and multi-user fields may be
    /// ignored by the corporate form. No REST writes or synthetic browser clicks.
    /// </summary>
    public static Uri BuildExperimentalCreateFormUrl(Uri apiRoot, string projectId,
        string title, string descriptionWithMarker, string ticketSubtype,
        string? priority, string? severity, string? assigneeId)
    {
        if (!PolarionTicketClassification.MatchesNc(ticketSubtype) &&
            !PolarionTicketClassification.MatchesCr(ticketSubtype))
            throw new ArgumentException("Unsupported TICKET subtype.", nameof(ticketSubtype));
        if (string.IsNullOrWhiteSpace(descriptionWithMarker) ||
            descriptionWithMarker.Length > 250000)
            throw new ArgumentException("A complete TICKET description is required.", nameof(descriptionWithMarker));
        var basic = BuildCreateFormUrl(apiRoot, projectId, title).AbsoluteUri;
        var sb = new StringBuilder(basic);
        static void Append(StringBuilder sb, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
        // MUST include the TestLoomREF marker for read-only post-save reconciliation.
        Append(sb, "form_field_description", descriptionWithMarker);
        Append(sb, "form_field_TICKET_tck_type", ticketSubtype);
        // Ticketing priority enum identifiers observed in the corporate REST audit.
        Append(sb, "form_field_priority", ToPolarionPriority(priority));
        Append(sb, "form_field_severity", severity?.Trim().ToLowerInvariant() ?? "");
        // The project-specific multi-assignee field may not be URL-prefillable.
        Append(sb, "form_field_TICKET_tkc_assign_to", assigneeId?.Trim() ?? "");
        var uri = sb.ToString();
        if (uri.Length > MaxFormUrlLength)
            throw new ArgumentOutOfRangeException(nameof(descriptionWithMarker),
                "Full browser prefill exceeds the safe URL length; fallback is required.");
        return new Uri(uri, UriKind.Absolute);
    }

    private static string ToPolarionPriority(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "P1" or "50" or "50.0" => "50.0",
            "P2" or "33" or "33.0" => "33.0",
            "P3" or "25" or "25.0" => "25.0",
            "P4" or "20" or "20.0" => "20.0",
            "P5" or "16" or "16.0" => "16.0",
            _ => ""
        };

    /// <summary>Exactly the text that must be saved in the Ticketing description for verification.</summary>
    public static string DescriptionWithMarker(string description,
        PolarionCreationReceiptStore.Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (string.IsNullOrWhiteSpace(description) || receipt.BrowserCorrelationMarker is null ||
            !Marker.IsMatch(receipt.BrowserCorrelationMarker))
            throw new ArgumentException("Invalid TICKET description or browser correlation receipt.");
        return description.Trim() + "\n\nDITM correlation: " + receipt.BrowserCorrelationMarker +
            "\nDITM source: " + receipt.SourceId;
    }

    public static Prepared Prepare(
        AppState state, bool isCr, string sourceId, Guid sourceUid,
        string title, string description, string? priority, string? severity,
        string? assigneeId, TestCase? sourceTest = null)
    {
        if (!state.PuoScrivere || state.OperatoreCorrente is null)
            throw new UnauthorizedAccessException("TestLoom workspace is read-only.");
        var settings = PolarionApiSettingsService.Load(state.Paths);
        if (settings.ErroreCaricamento is not null || !Uri.TryCreate(settings.ApiRoot, UriKind.Absolute, out var api) ||
            api.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(api.UserInfo) ||
            !Regex.IsMatch(settings.ProjectId, @"^[A-Za-z][A-Za-z0-9_]*$"))
            throw new InvalidOperationException("Configure a valid HTTPS Ticketing host and project before browser handoff.");
        if (!string.IsNullOrWhiteSpace(assigneeId) &&
            !settings.AssegnatariEsta.Contains(assigneeId, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Assignee is not one of the Master-configured Ticketing IDs.");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("TICKET title and description are required.");
        // A browser handoff is still an external creation: reject deleted or
        // replaced NC/CR records before reserving a unique correlation marker.
        if (isCr)
        {
            var cr = state.ChangeRequests.GetById(sourceId);
            if (cr is null || cr.Uid != sourceUid || cr.Archiviata)
                throw new InvalidOperationException("The originating CR was removed, replaced or archived.");
        }
        else
        {
            var nc = state.NonConformita.GetById(sourceId);
            if (nc is null || nc.Uid != sourceUid)
                throw new InvalidOperationException("The originating NC was removed or replaced.");
        }
        if (sourceTest is not null)
        {
            var persisted = state.TestCases.GetByUid(sourceTest.Uid);
            var current = persisted is null ? null : state.TestCases.LoadCurrent(persisted);
            if (persisted is null || current is null || persisted.Eliminato || current.Eliminato ||
                !string.Equals(current.Id, sourceTest.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Save the originating Test before creating its TICKET: no persisted Test with this UID was found.");
            if (current.RevisioneDati != sourceTest.RevisioneDati ||
                !string.Equals(TestCaseFingerprint.Compute(current),
                    TestCaseFingerprint.Compute(sourceTest), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The Test has unsaved or changed data. Save/reload the Test before preparing TICKET.");
            var matches = PolarionTestSourceResolver.Find(persisted,
                state.NonConformita.PeekAllReadOnly(), state.ChangeRequests.PeekAllReadOnly());
            if (!matches.Any(source => source.IsCr == isCr && source.Id.Equals(sourceId,
                    StringComparison.OrdinalIgnoreCase) && (isCr
                    ? source.Cr?.Uid == sourceUid : source.Nc?.Uid == sourceUid)))
                throw new InvalidOperationException(
                    "The selected Test is no longer related to this NC/CR. Reopen the Test editor.");
        }
        var ticketType = isCr ? PolarionTicketClassification.ChangeRequest : PolarionTicketClassification.NonConformita;
        if (!string.IsNullOrWhiteSpace(settings.BrowserCreateUrl) &&
            (!Uri.TryCreate(settings.BrowserCreateUrl, UriKind.Absolute, out var preferred) ||
             preferred.Scheme != Uri.UriSchemeHttps || preferred.Host != api.Host ||
             preferred.Port != api.Port || !preferred.AbsolutePath.StartsWith("/ticketing/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The browser create URL no longer matches the Ticketing host.");
        // Validate every configurable field BEFORE writing a crash-safe handoff receipt.
        if (string.IsNullOrWhiteSpace(sourceId) || sourceUid == Guid.Empty ||
            title.Length > 512 || description.Length > 250000)
            throw new ArgumentException("Invalid TICKET source or oversized browser draft.");
        // Generate the marker before the receipt, but create the receipt only
        // after the full target URL has been validated. A failed prefill must not
        // strand a source in a false pending-creation state.
        var marker = "TestLoomREF" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var descriptionForPolarion = DescriptionWithMarker(description,
            new PolarionCreationReceiptStore.Receipt
            {
                SourceId = sourceId, BrowserCorrelationMarker = marker
            });
        var customUrl = !string.IsNullOrWhiteSpace(settings.BrowserCreateUrl);
        Uri uri;
        var fullPrefillAttempted = false;
        string? prefillNotice = null;
        if (customUrl)
        {
            uri = new Uri(settings.BrowserCreateUrl, UriKind.Absolute);
            prefillNotice = L("Collegamento Ticketing personalizzato: la precompilazione dei campi non è garantita.",
                "Custom Ticketing link: field prefill is not guaranteed.",
                "Lien Ticketing personnalisé : le préremplissage des champs n’est pas garanti.");
        }
        else
        {
            try
            {
                uri = BuildExperimentalCreateFormUrl(api, settings.ProjectId, title,
                    descriptionForPolarion, ticketType, priority, severity, assigneeId);
                fullPrefillAttempted = true;
            }
            catch (ArgumentOutOfRangeException)
            {
                // A huge selected Test package may exceed a safe URL length.
                // Never truncate description/TestLoomREF without telling the user.
                uri = BuildCreateFormUrl(api, settings.ProjectId, title);
                prefillNotice = L(
                    "La descrizione supera la lunghezza sicura dell'URL. Incolla la descrizione preparata con TestLoomREF e verifica gli altri campi.",
                    "Description exceeds the safe URL length. Paste the prepared description with TestLoomREF and review other fields.",
                    "La description dépasse la longueur sûre de l'URL. Collez la description préparée avec TestLoomREF et vérifiez les autres champs.");
            }
        }
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != api.Host || uri.Port != api.Port)
            throw new InvalidOperationException("Browser creation URL must belong to the Ticketing host.");
        var store = new PolarionCreationReceiptStore(state.Paths);
        var receipt = store.BeginBrowser(isCr, sourceId, sourceUid, marker, ticketType, assigneeId,
            state.Config.OperatoreAttivo, sourceTest?.Uid, sourceTest?.Id);
        var clipboard = new StringBuilder()
            .AppendLine(L("Bozza di creazione TICKET preparata da TestLoom", "TICKET creation draft prepared by TestLoom",
                "Brouillon de création TICKET préparé par TestLoom"))
            .AppendLine(L("Tipo di ticket: TICKET_Ticket", "Ticket type: TICKET_Ticket", "Type de ticket : TICKET_Ticket"))
            .AppendLine("TICKET_tck_type: " + ticketType)
            .AppendLine(L("Titolo: ", "Title: ", "Titre : ") + title.Trim())
            .AppendLine(L("Assegnatario (ID Ticketing): ", "Assignee (Ticketing user ID): ",
                "Responsable (ID Ticketing) : ") + (assigneeId ?? L("(scegli in Ticketing)",
                "(choose in Ticketing)", "(choisir dans Ticketing)")))
            .AppendLine(L("Priorità: ", "Priority: ", "Priorité : ") + (priority ?? ""))
            .AppendLine(L("Severità: ", "Severity: ", "Gravité : ") + (severity ?? ""))
            .AppendLine()
            .AppendLine(L("Descrizione (incolla per intero in Ticketing):",
                "Description (paste the FULL section below into Ticketing):",
                "Description (collez la section ENTIÈRE dans Ticketing) :"))
            .AppendLine(DescriptionWithMarker(description, receipt))
            .AppendLine()
            .AppendLine(L("Conserva il codice TestLoomREF nella descrizione salvata per permettere il collegamento automatico.",
                "Keep the TestLoomREF marker in the saved description for automatic linking.",
                "Conservez le code TestLoomREF dans la description enregistrée pour permettre l'association automatique."))
            .ToString();

        // Direct form URL documented in Ticketing 2512. A Master-verified
        // custom URL can override it for a customized corporate form.
        // Rich text and custom enum fields stay out of the URL.
        return new Prepared(receipt, clipboard, uri, fullPrefillAttempted, prefillNotice);
    }

    internal enum CandidateVerification { Match, TypeMarkerOrDateMismatch, AssigneeMismatch }

    /// <summary>Pure verification gate for a ticket created in the real Ticketing UI.</summary>
    internal static CandidateVerification VerifyCandidate(
        PolarionCreationReceiptStore.Receipt receipt, PolarionTicketSnapshot snapshot,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (receipt.BrowserCorrelationMarker is null || !Marker.IsMatch(receipt.BrowserCorrelationMarker) ||
            !string.Equals(snapshot.TicketType, receipt.ExpectedTicketType, StringComparison.OrdinalIgnoreCase) ||
            !(snapshot.Description ?? "").Contains(receipt.BrowserCorrelationMarker, StringComparison.Ordinal) ||
            snapshot.Created is null ||
            snapshot.Created < receipt.StartedUtc.AddMinutes(-5) ||
            snapshot.Created > nowUtc.AddMinutes(2))
            return CandidateVerification.TypeMarkerOrDateMismatch;
        if (!string.IsNullOrWhiteSpace(receipt.RequestedAssignee) &&
            !snapshot.Assignees.Concat(snapshot.CustomAssignees)
                .Contains(receipt.RequestedAssignee, StringComparer.OrdinalIgnoreCase))
            return CandidateVerification.AssigneeMismatch;
        return CandidateVerification.Match;
    }

    /// <summary>One read-only, bounded reconciliation attempt. No automatic REST writes.</summary>
    public static async Task<CheckResult> TryReconcileAsync(
        AppState state, PolarionCreationReceiptStore.Receipt receipt, CancellationToken cancellationToken = default,
        Func<PolarionReadClient>? clientFactory = null, string? explicitlySavedId = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (receipt.BrowserCorrelationMarker is null || !Marker.IsMatch(receipt.BrowserCorrelationMarker) ||
            receipt.ExpectedTicketType is null)
            return new CheckResult(false, true, null, L(
                "Nessuna creazione TICKET valida in attesa.", "No valid pending browser-created ticket.",
                "Aucune création TICKET valide en attente."));
        if (!state.CartellaValida || !state.PuoScrivere || state.OperatoreCorrente is null ||
            !string.Equals(state.Config.OperatoreAttivo, receipt.OperatorAtHandoff, StringComparison.OrdinalIgnoreCase))
            return new CheckResult(false, true, null, L(
                "TestLoom è in sola lettura oppure l’operatore originale non è attivo.",
                "TestLoom is read-only or the originating operator is not active.",
                "TestLoom est en lecture seule ou l’opérateur d’origine n’est pas actif."));
        var settings = PolarionApiSettingsService.Load(state.Paths);
        if (!settings.LetturaAbilitata || settings.ErroreCaricamento is not null || !settings.HaToken)
            return new CheckResult(false, true, null, L(
                "La lettura REST di Ticketing è disabilitata.", "Ticketing read access is disabled.",
                "L’accès en lecture à Ticketing est désactivé."));

        using var http = clientFactory is null ? PolarionReadClient.CreateWindowsProxyHttpClient() : null;
        var client = clientFactory?.Invoke() ??
            new PolarionReadClient(http!, new Uri(settings.ApiRoot), settings.LeggiToken());
        // If a previous attempt linked the NC/CR (or Test) but cleanup failed,
        // resume from its durable, previously verified ID. Never depend on a
        // second index search or create another TICKET for this receipt.
        var resumeTicketId = receipt.CreatedTicketId;
        IReadOnlyList<string> candidates = !string.IsNullOrWhiteSpace(resumeTicketId)
            ? new[] { resumeTicketId }
            : explicitlySavedId is not null
                ? new[] { explicitlySavedId }
                : await client.SearchBrowserCorrelationAsync(settings.ProjectId, receipt.BrowserCorrelationMarker,
                    cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
            return new CheckResult(false, true, null, L(
                "TICKET non ancora salvato o indicizzato. Salva in Ticketing e conserva TestLoomREF nella descrizione.",
                "TICKET not saved or indexed yet. Save in Ticketing and keep TestLoomREF in the description.",
                "TICKET pas encore enregistré ou indexé. Enregistrez dans Ticketing et conservez TestLoomREF dans la description."));
        if (candidates.Count > 1)
            return new CheckResult(false, true, null, L(
                "Trovati più TICKET possibili: serve una verifica manuale.",
                "Multiple TICKET candidates: manual reconciliation required.",
                "Plusieurs TICKET possibles : une vérification manuelle est nécessaire."));

        var ticketId = candidates[0];
        var snapshot = await client.GetTicketForVerificationAsync(settings.ProjectId, ticketId, cancellationToken)
            .ConfigureAwait(false);
        // Never trust a search hit alone, or identify by title, human assignee or mere time proximity.
        var match = VerifyCandidate(receipt, snapshot, DateTimeOffset.UtcNow);
        if (match == CandidateVerification.TypeMarkerOrDateMismatch)
            return new CheckResult(false, true, null, L(
                "Il ticket TICKET non corrisponde a TestLoomREF, classificazione o data di creazione.",
                "The TICKET does not match the expected TestLoomREF, classification or creation time.",
                "L’TICKET ne correspond pas au code TestLoomREF, à la classification ou à la date de création attendus."));
        if (match == CandidateVerification.AssigneeMismatch)
            return new CheckResult(false, true, ticketId, L(
                "TICKET trovato, ma l’assegnatario selezionato è diverso o non disponibile: verifica manualmente.",
                "TICKET found, but selected assignee differs or is unavailable: manual verification needed.",
                "TICKET trouvé, mais le responsable choisi est différent ou indisponible : vérification manuelle requise."));
        var verified = new PolarionWriteService.Created(ticketId,
            settings.ProjectId + "/" + ticketId,
            new Uri(new Uri(settings.ApiRoot), "/ticketing/redirect/project/" +
                Uri.EscapeDataString(settings.ProjectId) + "/workitem?id=" + Uri.EscapeDataString(ticketId)).AbsoluteUri);
        // Repositories can notify Avalonia-bound models when a linked NC/CR changes.
        // Network discovery stays on the worker; the actual domain save happens on UI.
        (PolarionCreatedTicketLinkService.Outcome Source, PolarionCreatedTicketLinkService.Outcome? Test)
            ApplyLinks() => ApplyVerifiedLinks(state, receipt, verified);
        var result = Dispatcher.UIThread.CheckAccess()
            ? ApplyLinks()
            : await Dispatcher.UIThread.InvokeAsync(ApplyLinks);
        if (!result.Source.Linked || result.Test is { Linked: false })
            return new CheckResult(false, true, ticketId, L(
                "TICKET verificato, ma il collegamento a NC/CR o Test è in attesa per un conflitto di salvataggio TestLoom. Il ticket non verrà creato di nuovo.",
                "TICKET verified, but its NC/CR or Test link is pending due to a TestLoom save conflict. The ticket will not be recreated.",
                "TICKET vérifié, mais l’association NC/CR ou Test est en attente à cause d’un conflit d’enregistrement TestLoom. Le ticket ne sera pas recréé."));
        DiagnosticTraceService.Registra("TICKET.BrowserLink", "verified-linked ticket=" + ticketId +
            " source-kind=" + (receipt.IsCr ? "CR" : "NC") + " source=" + receipt.SourceId +
            " test-linked=" + (result.Test?.Linked ?? false));
        return new CheckResult(true, false, ticketId,
            result.Test is null
                ? L("TICKET verificato e collegato alla NC/CR in TestLoom.",
                    "TICKET verified and linked to its TestLoom NC/CR.",
                    "TICKET vérifié et associé à la NC/CR dans TestLoom.")
                : L("TICKET verificato e collegato alla NC/CR e al Test in TestLoom.",
                    "TICKET verified and linked to its TestLoom NC/CR and Test.",
                    "TICKET vérifié et associé à la NC/CR et au Test dans TestLoom."));
    }
    internal static (PolarionCreatedTicketLinkService.Outcome Source, PolarionCreatedTicketLinkService.Outcome? Test)
        ApplyVerifiedLinks(AppState state, PolarionCreationReceiptStore.Receipt receipt,
            PolarionWriteService.Created verified)
    {
        // The verified identity must survive a crash or a partial save before either link is attempted.
        var receipts = new PolarionCreationReceiptStore(state.Paths);
        receipts.MarkCreated(receipt, verified);
        var source = receipt.IsCr
            ? PolarionCreatedTicketLinkService.LinkCr(state, receipt.SourceId, receipt.SourceUid,
                verified, browserVerified: true)
            : PolarionCreatedTicketLinkService.LinkNc(state, receipt.SourceId, receipt.SourceUid,
                verified, browserVerified: true);
        var test = source.Linked && receipt.OriginTestUid is { } testUid && testUid != Guid.Empty
            ? PolarionCreatedTicketLinkService.LinkTest(state, testUid, verified)
            : null;
        if (source.Linked && (test is null || test.Linked))
            receipts.Clear(receipt);
        return (source, test);
    }

}
