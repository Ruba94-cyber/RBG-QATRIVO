using System.Text.Json;
using TestLoom.App.Models;

namespace TestLoom.App.Services;

/// <summary>
/// Risoluzione manuale di un fork TestCase da parte del Master (Impostazioni → Conflitti fra postazioni).
/// Fonde in ordine di tempo i rami recuperati sopra la versione corrente, chiede al Master le scelte sui campi in
/// conflitto (<paramref name="sceltaConflitti"/>, null = annullato) e salva il risultato come nuova revisione.
/// La parte UI (ricerca del fork, messaggi, audit) resta nel view model; qui non c'è nessuna dipendenza da WPF/Avalonia.
/// </summary>
public static class TestCaseManualForkResolution
{
    /// <summary>Restituisce la versione salvata, oppure null se il Master ha annullato la scelta dei campi in conflitto.
    /// Nessun file viene scritto se l'annullamento avviene prima del salvataggio.</summary>
    public static async Task<TestCase?> RisolviAsync(
        TestCaseRepository repository,
        JournalForkConflict fork,
        TestCase current,
        ForkRecoveryLoadResult recovery,
        string operatore,
        Func<IReadOnlyList<CampoMergeTestCase>, Task<IReadOnlyDictionary<string, SceltaMerge>?>> sceltaConflitti)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(sceltaConflitti);

        var currentHash = TestCaseFingerprint.Compute(current);
        var working = Clone(current);
        var auditSources = new List<TestCase> { Clone(current) };
        foreach (var branch in recovery.Available.OrderBy(b => b.Event.TimestampUtc))
        {
            var branchHash = TestCaseFingerprint.Compute(branch.TestCase);
            if (branchHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase)) continue;

            var merge = TestCaseThreeWayMerge.PreparaFork(working, branch.TestCase);
            IReadOnlyDictionary<string, SceltaMerge>? scelte = merge.Conflitti.Count == 0
                ? new Dictionary<string, SceltaMerge>()
                : await sceltaConflitti(merge.Conflitti);
            if (scelte == null) return null;

            working = merge.CreaVersioneRisolta(scelte, operatore);
            auditSources.Add(Clone(branch.TestCase));
            auditSources.Add(Clone(working));
        }

        working.Changelog = TestCaseForkResolution.UnisciChangelog(auditSources.Append(working));
        working.Uid = current.Uid;
        working.RevisioneDati = current.RevisioneDati;
        working.ModificatoDa = operatore;
        working.UltimaModifica = DateTime.Now;

        var resolvedKeys = TestCaseForkResolution.GetResolvedKeys(current)
            .Concat(TestCaseForkResolution.GetResolvedKeys(working));
        TestCaseForkResolution.SetResolvedKeys(working, resolvedKeys);
        TestCaseForkResolution.MarkResolved(working, [fork]);
        var forkKey = TestCaseForkResolution.Key(fork);
        working.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = operatore,
            Campo = "conflitto sincronizzazione OneDrive",
            ValorePrecedente = $"fork {forkKey}",
            ValoreNuovo = "risolto da Master"
        });

        TestCaseFingerprint.SetStored(working, currentHash);
        // Rami del fork incorporati in questa risoluzione: le loro copie OneDrive visibili non bloccano il salvataggio che le
        // risolve; ogni altra copia divergente blocca come prima.
        var hashRami = new HashSet<string>(fork.Branches.Select(b => b.NewHash).Where(h => !string.IsNullOrWhiteSpace(h))!,
            StringComparer.OrdinalIgnoreCase);
        repository.Save(working, isNew: false, revisioneAttesa: current.RevisioneDati, ramiGiaRisolti: hashRami);
        try { repository.ArchiviaCopieDiRamiRisolti(working, hashRami, operatore); }
        catch (Exception ex) { CrashLogService.Registra("Risoluzione manuale fork: archiviazione copie", ex); }
        return working;
    }

    private static TestCase Clone(TestCase tc) =>
        JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;
}
