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

    /// <summary>
    /// Come <see cref="RisolviAsync"/> ma sul RECORD REALE con la schermata grafica di confronto (due versioni affiancate, scelta
    /// campo per campo o modifica manuale, anteprima) e, se il confronto non e' possibile, scelta della versione intera in sola
    /// lettura. Le versioni originali (corrente e rami) vanno nel recovery-bin PRIMA del salvataggio; annullare non scrive nulla.
    /// Restituisce la versione salvata, oppure null se l'utente ha annullato.
    /// </summary>
    public static async Task<TestCase?> RisolviConEditorAsync(
        TestCaseRepository repository,
        DataPaths paths,
        JournalForkConflict fork,
        TestCase current,
        ForkRecoveryLoadResult recovery,
        string operatore,
        Action<TestCase>? dopoSalvataggio = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(fork);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(recovery);

        var currentHash = TestCaseFingerprint.Compute(current);
        var mine = JsonSerializer.SerializeToUtf8Bytes(current, JsonIo.Options);
        var rami = recovery.Available.OrderBy(b => b.Event.TimestampUtc)
            .Where(b => !TestCaseFingerprint.Compute(b.TestCase).Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            .Select(b => (Bytes: JsonSerializer.SerializeToUtf8Bytes(b.TestCase, JsonIo.Options),
                          Etichetta: "Versione di " + (string.IsNullOrWhiteSpace(b.TestCase.ModificatoDa) ? b.Event.DeviceId : b.TestCase.ModificatoDa),
                          Descrizione: $"Salvata il {b.Event.TimestampUtc.ToLocalTime():dd/MM/yyyy HH:mm} da {b.Event.DeviceId}"))
            .ToList();

        // Ogni altra versione divergente dello stesso Test (copie OneDrive visibili o in quarantena non coincidenti con un ramo
        // del journal) entra nel confronto: se restasse fuori, la guardia di scrittura bloccherebbe il salvataggio che risolve il
        // conflitto (deadlock osservato su T-BD-0012) e il suo contenuto non sarebbe mai deciso dall'utente.
        var percorsoCanonico = repository.FilePathOf(current);
        var hashNote = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentHash };
        foreach (var b in recovery.Available) hashNote.Add(TestCaseFingerprint.Compute(b.TestCase));
        var extra = new List<VisibleConflictWriteGuard.CopiaDivergente>();
        foreach (var c in VisibleConflictWriteGuard.ElencaCopieDivergenti(percorsoCanonico, current.Uid, paths.Root))
        {
            var leggibile = true;
            try
            {
                var tc = JsonSerializer.Deserialize<TestCase>(c.Bytes, JsonIo.Options);
                if (tc != null && !hashNote.Add(TestCaseFingerprint.Compute(tc))) { extra.Add(c); continue; } // gia' nota: solo da archiviare
            }
            catch (JsonException) { leggibile = false; } // entra comunque: l'utente la vede come versione non leggibile da scartare
            extra.Add(c);
            rami.Add((c.Bytes, leggibile ? "Copia lasciata dalla sincronizzazione" : "Copia danneggiata lasciata dalla sincronizzazione",
                c.Entry != null ? $"Copia rilevata il {c.Entry.Envelope.DetectedUtc.ToLocalTime():dd/MM/yyyy HH:mm} su {c.Entry.Envelope.Device}" : "Copia presente nella cartella condivisa"));
        }

        var esito = await ConflictResolutionService.EseguiPercorsoAsync(RecordMergeKind.TestCase, "Test " + current.Id, mine, rami, operatore);
        if (esito == null) return null;

        var percorso = percorsoCanonico;
        foreach (var r in rami)
            DomainRecoveryService.ArchivePayload(paths, percorso, DomainRecoveryCategory.TestCase, r.Bytes, operatore,
                esito.Changed ? "ramo del fork: scartato o incorporato nella risoluzione" : "ramo del fork scartato (tenuta la versione corrente)");
        foreach (var c in extra.Where(c => !rami.Any(r => ReferenceEquals(r.Bytes, c.Bytes))))
            DomainRecoveryService.ArchivePayload(paths, percorso, DomainRecoveryCategory.TestCase, c.Bytes, operatore,
                "copia di conflitto gia' coincidente con un ramo del fork");
        if (esito.Changed)
            DomainRecoveryService.ArchivePayload(paths, percorso, DomainRecoveryCategory.TestCase, mine, operatore,
                "versione corrente prima della risoluzione del fork");

        var working = esito.Model as TestCase ?? Clone(current);
        working.Uid = current.Uid;
        working.RevisioneDati = current.RevisioneDati;
        working.ModificatoDa = operatore;
        working.UltimaModifica = DateTime.Now;
        working.Changelog = TestCaseForkResolution.UnisciChangelog(
            new[] { current }.Concat(recovery.Available.Select(b => b.TestCase)).Append(working));

        var resolvedKeys = TestCaseForkResolution.GetResolvedKeys(current)
            .Concat(TestCaseForkResolution.GetResolvedKeys(working));
        TestCaseForkResolution.SetResolvedKeys(working, resolvedKeys);
        TestCaseForkResolution.MarkResolved(working, [fork]);
        working.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = operatore,
            Campo = "conflitto sincronizzazione OneDrive",
            ValorePrecedente = $"fork {TestCaseForkResolution.Key(fork)}",
            ValoreNuovo = "risolto da Master"
        });
        TestCaseFingerprint.SetStored(working, currentHash);
        var hashRami = new HashSet<string>(fork.Branches.Select(b => b.NewHash).Where(h => !string.IsNullOrWhiteSpace(h))!,
            StringComparer.OrdinalIgnoreCase);
        using (VisibleConflictWriteGuard.BeginResolution(extra.Select(e => e.Key)))
        using (esito.Changed ? TestRevisionSafety.AmmettiRisoluzioneConflitto() : null)
            repository.Save(working, isNew: false, revisioneAttesa: current.RevisioneDati, ramiGiaRisolti: hashRami);
        // Salvataggio riuscito: le copie decise (gia' archiviate sopra) escono dalla cartella / chiudono il conflitto.
        foreach (var c in extra)
        {
            try
            {
                if (c.Entry != null) ConflictCopyQuarantineService.Close(paths.Root, c.Entry);
                else if (c.Percorso != null && File.Exists(c.Percorso)) WorkflowTransactionService.DeleteFile(c.Percorso);
            }
            catch (Exception ex) { CrashLogService.Registra("Risoluzione fork con editor: chiusura copia", ex); }
        }
        try { repository.ArchiviaCopieDiRamiRisolti(working, hashRami, operatore); }
        catch (Exception ex) { CrashLogService.Registra("Risoluzione fork con editor: archiviazione copie", ex); }
        // Hook post-salvataggio (es. TestRevisionReferenceRemap.Riallinea(state, test)): un errore non annulla la risoluzione gia' salvata.
        try { dopoSalvataggio?.Invoke(working); }
        catch (Exception ex) { CrashLogService.Registra("Risoluzione fork con editor: riallineamento riferimenti REV", ex); }
        return working;
    }

    private static TestCase Clone(TestCase tc) =>
        JsonSerializer.Deserialize<TestCase>(JsonSerializer.Serialize(tc, JsonIo.Options), JsonIo.Options)!;
}
