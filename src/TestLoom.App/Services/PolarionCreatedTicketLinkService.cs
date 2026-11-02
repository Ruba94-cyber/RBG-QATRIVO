using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Links an already-created TICKET to its TestLoom source using the repository's existing optimistic
/// revision checks and journal. Never retries a Ticketing POST if a local link save conflicts.
/// </summary>
public static class PolarionCreatedTicketLinkService
{
    public sealed record Outcome(string TicketId, string PortalUrl, bool Linked, string? LinkingError);

    public static Outcome LinkNc(AppState state, string sourceId, Guid sourceUid,
        PolarionWriteService.Created created, bool browserVerified = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.PuoScrivere || state.OperatoreCorrente is null)
            return new Outcome(created.TicketId, created.PortalUrl ?? "", false, "Workspace is not writable or user is not authorized.");
        try
        {
            var source = state.NonConformita.GetById(sourceId);
            if (source is null || source.Uid != sourceUid)
                throw new InvalidOperationException("Source NC was removed or replaced.");
            if (source.CollegamentiPolarion.Any(x => x.Codice.Equals(created.TicketId, StringComparison.OrdinalIgnoreCase)))
                return new Outcome(created.TicketId, created.PortalUrl ?? "", true, null);
            var copy = source.Clona();
            copy.CollegamentiPolarion.Add(new CollegamentoPolarion
            {
                Codice = created.TicketId,
                Url = created.PortalUrl ?? "",
                CollegatoIl = DateTime.Now,
                CollegatoDa = state.Config.OperatoreAttivo,
                Note = browserVerified
                    ? "Verified browser-created TICKET by read-only Ticketing GET."
                    : "Created using Ticketing REST API after explicit confirmation."
            });
            copy.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = state.Config.OperatoreAttivo,
                Campo = browserVerified ? "CollegamentiPolarion / creazione browser" :
                    "CollegamentiPolarion / creazione API",
                ValorePrecedente = null,
                ValoreNuovo = created.TicketId
            });
            state.NonConformita.Save(copy, source.RevisioneDati);
            DiagnosticTraceService.Registra("TICKET.LinkNc", "linked ticket=" + created.TicketId +
                " nc=" + sourceId + " revision=" + source.RevisioneDati);
            return new Outcome(created.TicketId, created.PortalUrl ?? "", true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Outcome(created.TicketId, created.PortalUrl ?? "", false, ex.GetType().Name);
        }
    }

    public static Outcome LinkCr(AppState state, string sourceId, Guid sourceUid,
        PolarionWriteService.Created created, bool browserVerified = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.PuoScrivere || state.OperatoreCorrente is null)
            return new Outcome(created.TicketId, created.PortalUrl ?? "", false, "Workspace is not writable or user is not authorized.");
        try
        {
            var source = state.ChangeRequests.GetById(sourceId);
            if (source is null || source.Uid != sourceUid)
                throw new InvalidOperationException("Source CR was removed or replaced.");
            if (source.TicketCollegati.Any(x => x.Riferimento.Equals(created.TicketId, StringComparison.OrdinalIgnoreCase)))
                return new Outcome(created.TicketId, created.PortalUrl ?? "", true, null);
            var copy = EntityStateFingerprint.Clone(source);
            copy.TicketCollegati.Add(new TicketRif
            {
                Riferimento = created.TicketId,
                Url = created.PortalUrl ?? "",
                CollegatoIl = DateTime.Now,
                CollegatoDa = state.Config.OperatoreAttivo,
                Note = browserVerified
                    ? "Verified browser-created TICKET by read-only Ticketing GET."
                    : "Created using Ticketing REST API after explicit confirmation."
            });
            copy.UltimaModifica = DateTime.Now;
            copy.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = state.Config.OperatoreAttivo,
                Campo = browserVerified ? "TicketCollegati / creazione browser" :
                    "TicketCollegati / creazione API",
                ValorePrecedente = null,
                ValoreNuovo = created.TicketId
            });
            state.ChangeRequests.Save(copy, source.RevisioneDati);
            DiagnosticTraceService.Registra("TICKET.LinkCr", "linked ticket=" + created.TicketId +
                " cr=" + sourceId + " revision=" + source.RevisioneDati);
            return new Outcome(created.TicketId, created.PortalUrl ?? "", true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Outcome(created.TicketId, created.PortalUrl ?? "", false, ex.GetType().Name);
        }
    }
    /// <summary>
    /// Add the SAME read-only-verified TICKET reference to a persisted Test when
    /// the browser draft originated from its editor. Must never create another
    /// Ticketing work item or overwrite an unsaved/changed Test. A failed write
    /// leaves the browser receipt pending for the next reconciliation attempt.
    /// </summary>
    public static Outcome LinkTest(AppState state, Guid testUid,
        PolarionWriteService.Created created)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(created);
        if (!state.PuoScrivere || state.OperatoreCorrente is null)
            return new Outcome(created.TicketId, created.PortalUrl ?? "", false,
                "Workspace is not writable or user is not authorized.");
        try
        {
            var indexed = state.TestCases.GetByUid(testUid);
            if (indexed is null || indexed.Eliminato)
                throw new InvalidOperationException("Originating Test is not available.");
            // Read the authoritative disk version to avoid overwriting any other
            // operator's changes or uncommitted edits in an open Test editor.
            var current = state.TestCases.LoadCurrent(indexed);
            if (current is null || current.Uid != testUid || current.Eliminato)
                throw new InvalidOperationException("Originating Test changed or was removed.");
            if (current.TicketCollegati.Any(x =>
                x.Riferimento.Equals(created.TicketId, StringComparison.OrdinalIgnoreCase)))
                return new Outcome(created.TicketId, created.PortalUrl ?? "", true, null);
            var copy = EntityStateFingerprint.Clone(current);
            TestCaseFingerprint.Stamp(copy);
            copy.TicketCollegati.Add(new TicketRif
            {
                Riferimento = created.TicketId,
                Url = created.PortalUrl ?? "",
                CollegatoIl = DateTime.Now,
                CollegatoDa = state.Config.OperatoreAttivo,
                Note = "Linked after browser creation and verified Ticketing GET."
            });
            copy.UltimaModifica = DateTime.Now;
            copy.ModificatoDa = state.Config.OperatoreAttivo;
            copy.Changelog.Add(new ChangelogEntry
            {
                Data = DateTime.Now,
                Operatore = state.Config.OperatoreAttivo,
                Campo = "TicketCollegati / creazione browser",
                ValorePrecedente = null,
                ValoreNuovo = created.TicketId
            });
            state.TestCases.Save(copy, isNew: false, revisioneAttesa: current.RevisioneDati);
            DiagnosticTraceService.Registra("TICKET.LinkTest", "linked ticket=" + created.TicketId +
                " test=" + copy.Id + " revision=" + current.RevisioneDati);
            return new Outcome(created.TicketId, created.PortalUrl ?? "", true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticTraceService.Registra("TICKET.LinkTest", "pending ticket=" + created.TicketId +
                " error=" + ex.GetType().Name);
            return new Outcome(created.TicketId, created.PortalUrl ?? "", false, ex.GetType().Name);
        }
    }

}
