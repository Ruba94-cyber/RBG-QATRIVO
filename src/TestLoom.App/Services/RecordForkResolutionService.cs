using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Risoluzione Master di un fork NC/CR/Segnalazione rilevato dal <see cref="DomainRecordJournalService"/>.
/// Nessun merge automatico: il Master sceglie una versione completa. La scelta passa sempre dai normali
/// controlli di revisione del repository (dentro uno scope di risoluzione che sblocca solo quel record) e
/// produce un nuovo commit; le altre versioni restano pubblicate in <c>_ditm/conflicts/records</c>.
/// </summary>
public static class RecordForkResolutionService
{
    public sealed record Outcome(string Message, string? ChosenHash);

    /// <summary>Il JSON autorevole attuale è quello corretto: chiude il fork senza modificare il record.</summary>
    public static Outcome KeepCurrent(AppState state, RecordFork fork, string operatorName)
    {
        var journal = Journal(state);
        var hash = CurrentHash(state, fork);
        journal.MarkResolved(fork, hash, operatorName);
        state.RegistraAudit("RisoluzioneConflittoRecord", fork.EntityId,
            $"domain={fork.Domain}; uid={fork.EntityUid:N}; scelta=corrente; hash={hash ?? "assente"}");
        return new Outcome("Conflitto chiuso mantenendo la versione corrente. Le altre versioni restano consultabili.", hash);
    }

    /// <summary>Applica come nuova revisione la versione completa di un ramo (o l'eliminazione, se il ramo è un Delete).</summary>
    public static Outcome ApplyBranch(AppState state, RecordFork fork, RecordForkBranch branch, string operatorName)
    {
        var journal = Journal(state);
        if (!fork.Branches.Any(b => b.EventId == branch.EventId))
            throw new InvalidOperationException("Il ramo scelto non appartiene a questo conflitto.");

        using var scope = journal.BeginResolution(fork.Domain, fork.EntityUid,
            fork.Branches.Where(b => !b.IsDelete).Select(b => b.NewHash).OfType<string>());
        string? chosenHash;
        if (branch.IsDelete)
        {
            DeleteCurrent(state, fork, operatorName);
            chosenHash = null;
        }
        else
        {
            var json = journal.TryReadBranchPayload(fork, branch)
                ?? throw new InvalidOperationException(
                    "La versione di questo ramo non è ancora disponibile: la postazione che l'ha prodotta deve aprire TestLoom " +
                    "dopo la sincronizzazione per pubblicarla. Nessuna modifica è stata fatta.");
            chosenHash = SaveBranch(state, fork, json, operatorName);
            // La versione scelta e' ora il canonico: la sua copia OneDrive visibile va archiviata (recovery-bin), altrimenti
            // bloccherebbe ogni salvataggio successivo. Le altre copie di ramo restano dove sono e non bloccano (ramo chiuso).
            if (branch.NewHash is { } hashScelto)
                ArchiviaCopiaScelta(state, fork, hashScelto, operatorName);
        }
        journal.MarkResolved(fork, chosenHash, operatorName);
        state.RegistraAudit("RisoluzioneConflittoRecord", fork.EntityId,
            $"domain={fork.Domain}; uid={fork.EntityUid:N}; scelta={branch.EventId}; device={branch.DeviceId}; delete={branch.IsDelete}");
        return new Outcome(branch.IsDelete
            ? "Conflitto chiuso applicando l'eliminazione scelta (copia completa nel recovery-bin)."
            : "Conflitto chiuso: la versione scelta è stata salvata come nuova revisione.", chosenHash);
    }

    /// <summary>Archivia nel recovery-bin le copie OneDrive visibili il cui contenuto e' il ramo scelto (mai cancellazione
    /// definitiva). Un errore non annulla la risoluzione gia' salvata: la copia resta e, essendo un ramo chiuso, non blocca.</summary>
    private static void ArchiviaCopiaScelta(AppState state, RecordFork fork, string hashScelto, string operatorName)
    {
        var (path, categoria) = fork.Domain switch
        {
            DomainRecordJournalService.NonConformitaDomain => (state.Paths.NonConformitaFile(fork.EntityId), DomainRecoveryCategory.NonConformita),
            DomainRecordJournalService.ChangeRequestDomain => (state.Paths.ChangeRequestFile(fork.EntityId), DomainRecoveryCategory.ChangeRequest),
            DomainRecordJournalService.SegnalazioneDomain => (state.Paths.SegnalazioneFile(fork.EntityUid), DomainRecoveryCategory.Segnalazione),
            _ => throw new InvalidOperationException($"Dominio non supportato: {fork.Domain}")
        };
        try
        {
            VisibleConflictWriteGuard.ArchiviaCopieRisolte(state.Paths, path, fork.EntityUid,
                copia => string.Equals(DomainRecordJournalService.HashDiCopia(fork.Domain, copia), hashScelto, StringComparison.OrdinalIgnoreCase),
                categoria, operatorName);
        }
        catch (Exception ex) { CrashLogService.Registra("Risoluzione fork: archiviazione copia scelta", ex); }
    }

    private static DomainRecordJournalService Journal(AppState state)
    {
        if (!state.EMaster) throw new UnauthorizedAccessException("Solo un Master può risolvere un conflitto fra postazioni.");
        return state.RecordJournal ?? throw new InvalidOperationException("Journal dei record non disponibile.");
    }

    private static string? CurrentHash(AppState state, RecordFork fork) => fork.Domain switch
    {
        DomainRecordJournalService.NonConformitaDomain => Hash(state.NonConformita.GetByUid(fork.EntityUid)),
        DomainRecordJournalService.ChangeRequestDomain => Hash(state.ChangeRequests.GetAll(includeArchiviate: true)
            .FirstOrDefault(c => c.Uid == fork.EntityUid)),
        DomainRecordJournalService.SegnalazioneDomain => Hash(state.Segnalazioni.Get(fork.EntityUid)),
        _ => throw new InvalidOperationException($"Dominio non supportato: {fork.Domain}")
    };

    private static string? Hash(object? value) => value == null ? null : EntityStateFingerprint.Compute(value);

    private static void DeleteCurrent(AppState state, RecordFork fork, string operatorName)
    {
        switch (fork.Domain)
        {
            case DomainRecordJournalService.NonConformitaDomain:
                if (state.NonConformita.GetByUid(fork.EntityUid) is { } nc) state.NonConformita.Elimina(nc, operatorName);
                break;
            case DomainRecordJournalService.ChangeRequestDomain:
                if (state.ChangeRequests.GetAll(includeArchiviate: true).FirstOrDefault(c => c.Uid == fork.EntityUid) is { } cr)
                    state.ChangeRequests.EliminaDefinitivamente(cr, operatorName);
                break;
            default:
                throw new InvalidOperationException("L'eliminazione guidata non è prevista per questo tipo di record.");
        }
    }

    private static string SaveBranch(AppState state, RecordFork fork, string json, string operatorName)
    {
        var entry = new ChangelogEntry
        {
            Data = DateTime.Now, Operatore = operatorName, Campo = "risoluzione conflitto fra postazioni",
            ValoreNuovo = "versione scelta dal Master"
        };
        switch (fork.Domain)
        {
            case DomainRecordJournalService.NonConformitaDomain:
            {
                var chosen = Parse<NonConformita>(json, fork);
                var current = state.NonConformita.GetByUid(fork.EntityUid);
                chosen.RevisioneDati = current?.RevisioneDati ?? chosen.RevisioneDati;
                chosen.Changelog.Add(entry);
                state.NonConformita.Save(chosen, current?.RevisioneDati);
                return EntityStateFingerprint.Compute(chosen);
            }
            case DomainRecordJournalService.ChangeRequestDomain:
            {
                var chosen = Parse<ChangeRequest>(json, fork);
                var current = state.ChangeRequests.GetAll(includeArchiviate: true).FirstOrDefault(c => c.Uid == fork.EntityUid);
                chosen.RevisioneDati = current?.RevisioneDati ?? chosen.RevisioneDati;
                chosen.Changelog.Add(entry);
                state.ChangeRequests.Save(chosen, current?.RevisioneDati);
                return EntityStateFingerprint.Compute(chosen);
            }
            case DomainRecordJournalService.SegnalazioneDomain:
            {
                var chosen = Parse<SegnalazioneDaClassificare>(json, fork);
                var current = state.Segnalazioni.Get(fork.EntityUid);
                chosen.RevisioneDati = current?.RevisioneDati ?? chosen.RevisioneDati;
                state.Segnalazioni.Save(chosen, current?.RevisioneDati);
                return EntityStateFingerprint.Compute(chosen);
            }
            default:
                throw new InvalidOperationException($"Dominio non supportato: {fork.Domain}");
        }
    }

    private static T Parse<T>(string json, RecordFork fork) where T : class
    {
        var value = JsonIo.TryParse<T>(json, out var error)
            ?? throw new InvalidDataException($"Versione del ramo non leggibile: {error}");
        var uid = (Guid?)typeof(T).GetProperty("Uid")?.GetValue(value);
        if (uid != fork.EntityUid) throw new InvalidDataException("La versione del ramo appartiene a un record diverso.");
        return value;
    }
}
