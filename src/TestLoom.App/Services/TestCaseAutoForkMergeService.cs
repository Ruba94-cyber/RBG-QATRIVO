using TestLoom.App.Models;

namespace TestLoom.App.Services;

public enum AutoForkMergeEsito { Applicato, GiaApplicatoAltrove, NonEleggibile, Fallito }

/// <summary>Contatori di un giro di <see cref="TestCaseAutoForkMergeService.EseguiSuForkNoti"/>, a scopo di
/// telemetria/log. Non lancia mai: ogni fork esaminato produce sempre uno dei quattro esiti.</summary>
public sealed class AutoForkMergeReport
{
    public int Applicati { get; private set; }
    public int GiaApplicatiAltrove { get; private set; }
    public int NonEleggibili { get; private set; }
    public int Falliti { get; private set; }
    public int TotaleForkEsaminati => Applicati + GiaApplicatiAltrove + NonEleggibili + Falliti;

    internal void Registra(AutoForkMergeEsito esito)
    {
        switch (esito)
        {
            case AutoForkMergeEsito.Applicato: Applicati++; break;
            case AutoForkMergeEsito.GiaApplicatoAltrove: GiaApplicatiAltrove++; break;
            case AutoForkMergeEsito.NonEleggibile: NonEleggibili++; break;
            default: Falliti++; break;
        }
    }
}

/// <summary>
/// Merge automatico dei fork OneDrive/SharePoint fra due postazioni (decisione utente 6.4.1 "Avviso + merge
/// automatico", punto B — decisione orchestratore sul formato dati: NESSUN cambio al formato dei file
/// condivisi).
///
/// PERCHÉ SERVE UNA BASE LOCALE: <see cref="TestCaseThreeWayMerge.PreparaFork"/> (usato dal flusso manuale
/// Master esistente) confronta i due rami SENZA una base comune, quindi marca come "in conflitto" ogni campo
/// che differisce fra i due — anche quando in realtà i due rami hanno toccato campi completamente diversi e la
/// base comune lo proverebbe. La base non è mai stata storicamente persistita da nessuna parte (il journal
/// condiviso è volutamente "bounded" e registra solo hash/revisione della base, mai il contenuto — vedi
/// <see cref="SharedTestJournalService"/>). Per un vero merge a tre vie automatico e sicuro serve quindi che
/// ALMENO UNO dei due PC coinvolti conservi ancora, in locale, il proprio "prima" di quel salvataggio: è
/// esattamente <see cref="SharedTestJournalService.PersistLocalBase"/>, scritta da
/// <see cref="TestCaseRepository"/> a ogni Save nel journal.db LOCALE del PC (mai nella cartella condivisa).
///
/// CHI APPLICA (decisione esplicita, vedi anche il commento di <see cref="ProvaMerge"/>): solo un PC che è
/// autore di uno dei due rami del fork E possiede ancora localmente la base col BaseHash del fork. Fra i due
/// autori possibili la scelta è deterministica sul DeviceId ordinale minore dei DUE AUTORI (non su chi è online
/// ora): due postazioni che osservano lo stesso fork calcolano sempre la stessa risposta e non tentano mai
/// entrambe, quindi non può nascere un nuovo fork "a catena". Se il PC con id minore non è mai online o ha
/// perso la base (retention scaduta), il fork resta aperto per il Master esattamente come oggi — nessun
/// fallback automatico sull'altro autore.
///
/// SICUREZZA: eseguito solo se il workspace è scrivibile (mai in sola lettura), mai su Test eliminati/cestino,
/// mai su REV storiche (TestRevisionSafety, invariato, applicato da TestCaseRepository.Save come per ogni altro
/// salvataggio), zero merge se anche un solo campo è in vero conflitto (fork lasciato al Master, comportamento
/// 6.4.0 invariato), idempotente: la Save finale usa lo stesso controllo di revisione ottimistica
/// (<c>revisioneAttesa</c>) di ogni altro salvataggio, quindi un secondo tentativo (qualunque origine) su un
/// fork già chiuso fallisce con <see cref="ConflittoRevisioneException"/> e viene trattato come no-op, mai come
/// un nuovo fork.
///
/// DISATTIVAZIONE: <see cref="AbilitaMergeAutomaticoFork"/>, costante a livello di codice come richiesto dal
/// brief (nessuna UI/config utente per ora).
/// </summary>
public static class TestCaseAutoForkMergeService
{
    public static bool AbilitaMergeAutomaticoFork = true;

    /// <summary>Operatore/ModificatoDa registrato per un Test risolto da questo servizio: mai un nome di
    /// operatore umano reale, così è sempre distinguibile nel Changelog/audit da una modifica manuale.</summary>
    public const string OperatoreMergeAutomatico = "Merge automatico (sistema)";

    /// <summary>
    /// Esamina tutti i fork attualmente noti (<see cref="TestCaseRepository.JournalForks"/>, già aggiornati da
    /// una precedente <see cref="TestCaseRepository.RefreshJournalMetadata"/>) e tenta il merge automatico su
    /// quelli eleggibili. Va chiamato in background, mai sul thread UI: esegue I/O locale e condiviso (lettura
    /// recovery, Save con lock di commit).
    /// </summary>
    public static AutoForkMergeReport EseguiSuForkNoti(
        DataPaths paths,
        TestCaseRepository repository,
        Action<string, string, string>? registraAudit = null)
    {
        var report = new AutoForkMergeReport();
        if (!AbilitaMergeAutomaticoFork) return report;
        if (!WorkspaceWritePolicy.CanWrite(paths)) return report; // mai in sola lettura

        var deviceIdLocale = repository.LocalDeviceId;
        if (string.IsNullOrWhiteSpace(deviceIdLocale)) return report;

        foreach (var fork in repository.JournalForks.ToList())
        {
            AutoForkMergeEsito esito;
            try { esito = ProvaMerge(paths, repository, fork, deviceIdLocale, registraAudit); }
            catch (Exception ex)
            {
                CrashLogService.Registra("Merge automatico fork: tentativo", ex);
                esito = AutoForkMergeEsito.Fallito;
            }
            report.Registra(esito);
        }
        return report;
    }

    /// <summary>
    /// Un solo fork. NonEleggibile copre sia "non è questo il caso" (fork a più di due rami, base non
    /// disponibile, non tocca a questo PC, ecc.) sia "conflitto vero" (resta al Master, comportamento
    /// invariato): la distinzione non serve al chiamante, che in entrambi i casi non fa nulla.
    /// </summary>
    private static AutoForkMergeEsito ProvaMerge(
        DataPaths paths,
        TestCaseRepository repository,
        JournalForkConflict fork,
        string deviceIdLocale,
        Action<string, string, string>? registraAudit)
    {
        // Solo il caso "due postazioni, stessa base" del brief: un fork con più di due rami o senza una base
        // comune affidabile (vedi DetectForks, secondo raggruppamento per sola NewRevision) non è coperto —
        // resta al Master come oggi, mai una scelta a cieca fra più di due rami.
        if (!fork.BaseRevision.HasValue || string.IsNullOrWhiteSpace(fork.BaseHash))
            return AutoForkMergeEsito.NonEleggibile;

        var rami = fork.Branches
            .Where(b => !string.IsNullOrWhiteSpace(b.NewHash))
            .GroupBy(b => b.NewHash!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.TimestampUtc).First())
            .ToList();
        if (rami.Count != 2) return AutoForkMergeEsito.NonEleggibile;

        var ramoA = rami[0];
        var ramoB = rami[1];
        if (string.IsNullOrWhiteSpace(ramoA.DeviceId) || string.IsNullOrWhiteSpace(ramoB.DeviceId) ||
            ramoA.DeviceId.Equals(ramoB.DeviceId, StringComparison.OrdinalIgnoreCase))
            return AutoForkMergeEsito.NonEleggibile; // non due postazioni distinte: fuori scope

        // Regola deterministica "DeviceId minore": dipende SOLO dai due DeviceId autori, mai da chi è online
        // ora. Due PC che vedono lo stesso fork calcolano sempre la stessa risposta.
        var autoreMinore = string.CompareOrdinal(ramoA.DeviceId, ramoB.DeviceId) <= 0 ? ramoA.DeviceId : ramoB.DeviceId;
        if (!deviceIdLocale.Equals(autoreMinore, StringComparison.OrdinalIgnoreCase))
            return AutoForkMergeEsito.NonEleggibile;

        if (!repository.TryGetLocalBase(fork.EntityUid, fork.BaseHash, out var baseVersion) || baseVersion == null)
            return AutoForkMergeEsito.NonEleggibile; // base non (più) disponibile localmente su questo PC

        var copie = repository.GetAll(includeEliminati: true).Where(t => t.Uid == fork.EntityUid).ToList();
        var cachedCurrent = copie.Where(t => !t.Eliminato).OrderByDescending(t => t.RevisioneDati).FirstOrDefault()
                             ?? copie.OrderByDescending(t => t.RevisioneDati).FirstOrDefault();
        if (cachedCurrent == null) return AutoForkMergeEsito.NonEleggibile;

        var current = repository.LoadCurrent(cachedCurrent);
        if (current == null) return AutoForkMergeEsito.NonEleggibile;
        if (current.Eliminato) return AutoForkMergeEsito.NonEleggibile; // mai su Test eliminati/nel cestino

        var recovery = DelayedForkRecoveryService.LoadAllBranches(paths, fork, current, copie);
        if (!recovery.Complete) return AutoForkMergeEsito.NonEleggibile;

        var branchA = recovery.Available.FirstOrDefault(b => b.Event.NewHash!.Equals(ramoA.NewHash, StringComparison.OrdinalIgnoreCase))?.TestCase;
        var branchB = recovery.Available.FirstOrDefault(b => b.Event.NewHash!.Equals(ramoB.NewHash, StringComparison.OrdinalIgnoreCase))?.TestCase;
        if (branchA == null || branchB == null) return AutoForkMergeEsito.NonEleggibile;

        // Vero three-way merge con la base locale: a differenza di PreparaFork, distingue correttamente "campo
        // toccato da un solo ramo" (automatico) da "stesso campo modificato in modo diverso da entrambi"
        // (conflitto). Copre già, senza codice dedicato, il caso "revisioni aggiunte da entrambe le parti":
        // TestCase.Esecuzioni non è un campo tecnico escluso, quindi se i due rami hanno appeso REV diverse
        // rispetto alla base comune il campo risulta automaticamente in conflitto.
        var merge = TestCaseThreeWayMerge.Prepara(baseVersion, branchA, branchB);
        if (merge.Conflitti.Count > 0) return AutoForkMergeEsito.NonEleggibile; // conflitto vero -> Master, invariato

        var working = merge.CreaVersioneRisolta(new Dictionary<string, SceltaMerge>(), OperatoreMergeAutomatico);
        // Changelog è escluso dal merge a tre vie: senza questa unione resterebbero le voci di un solo ramo e
        // quelle dell'altro autore andrebbero perse (stessa regola della risoluzione manuale del Master).
        working.Changelog = TestCaseForkResolution.UnisciChangelog(new[] { current, branchA, branchB, working });
        working.Uid = current.Uid;
        working.RevisioneDati = current.RevisioneDati;
        working.ModificatoDa = OperatoreMergeAutomatico;
        working.UltimaModifica = DateTime.Now;

        var resolvedKeys = TestCaseForkResolution.GetResolvedKeys(current).Concat(TestCaseForkResolution.GetResolvedKeys(working));
        TestCaseForkResolution.SetResolvedKeys(working, resolvedKeys);
        TestCaseForkResolution.MarkResolved(working, [fork]);
        var forkKey = TestCaseForkResolution.Key(fork);
        working.Changelog.Add(new ChangelogEntry
        {
            Data = DateTime.Now,
            Operatore = OperatoreMergeAutomatico,
            Campo = "merge automatico di due postazioni",
            ValorePrecedente = $"{AutoreDi(branchA, current)} ({ramoA.DeviceId}) @ {ramoA.TimestampUtc:u}",
            ValoreNuovo = $"{AutoreDi(branchB, current)} ({ramoB.DeviceId}) @ {ramoB.TimestampUtc:u}; fork {forkKey}"
        });

        TestCaseFingerprint.SetStored(working, TestCaseFingerprint.Compute(current));
        // I due rami del fork sono incorporati in questo merge: le loro copie di conflitto visibili non devono bloccare
        // proprio il salvataggio che le risolve (guardia introdotta da 61ce430). Ogni altra copia divergente blocca come prima.
        var hashRami = new HashSet<string>(new[] { ramoA.NewHash!, ramoB.NewHash! }, StringComparer.OrdinalIgnoreCase);
        try
        {
            repository.Save(working, isNew: false, revisioneAttesa: current.RevisioneDati, ramiGiaRisolti: hashRami);
        }
        catch (ConflittoRevisioneException)
        {
            // Idempotenza: qualcun altro ha già chiuso questo fork (già sincronizzato) prima che questo tentativo
            // arrivasse a scrivere. No-op sicuro: niente retry con una base ormai superata, nessun nuovo fork.
            return AutoForkMergeEsito.GiaApplicatoAltrove;
        }

        // Il merge è già su disco: pulizia e audit sono accessori e non devono trasformare l'esito in "Fallito"
        // (altrimenti il chiamante non aggiornerebbe subito i fork e potrebbe notificare un fork già chiuso).
        try { DelayedForkRecoveryService.CleanupResolvedRecovery(paths, fork); }
        catch (Exception ex) { CrashLogService.Registra("Merge automatico fork: pulizia recovery", ex); }
        // Le copie OneDrive visibili dei due rami sono ora incluse nel canonico: si archiviano nel recovery-bin, altrimenti
        // resterebbero visibili e bloccherebbero ogni salvataggio successivo del Test.
        try { repository.ArchiviaCopieDiRamiRisolti(working, hashRami, OperatoreMergeAutomatico); }
        catch (Exception ex) { CrashLogService.Registra("Merge automatico fork: archiviazione copie", ex); }
        try
        {
            registraAudit?.Invoke("AutoMergeFork", working.Id,
                $"fork={forkKey}; branches={fork.Branches.Count}; revision={working.RevisioneDati}; uid={working.Uid:N}; deviceApplicante={deviceIdLocale}");
        }
        catch (Exception ex) { CrashLogService.Registra("Merge automatico fork: audit", ex); }
        return AutoForkMergeEsito.Applicato;
    }

    private static string AutoreDi(TestCase branch, TestCase fallback) =>
        string.IsNullOrWhiteSpace(branch.ModificatoDa) ? (fallback.ModificatoDa ?? "?") : branch.ModificatoDa;
}
