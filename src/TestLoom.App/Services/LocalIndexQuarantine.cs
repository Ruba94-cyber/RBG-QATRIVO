using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Mette da parte un database locale illeggibile trattando DB + WAL + SHM come UN SOLO insieme.
///
/// Il vecchio comportamento (spostare i tre file uno per uno e, se lo spostamento falliva, cancellarli in
/// silenzio) aveva due difetti gravi: cancellava file che potevano essere ancora aperti da un'altra
/// connessione, e dichiarava riuscita una quarantena che non lo era, facendo poi ripartire la ricostruzione
/// su un file ancora corrotto. Qui, se il file principale non si riesce a spostare, NON si cancella nulla e
/// l'operazione fallisce in modo esplicito con un motivo leggibile.
///
/// Il suffisso resta `.corrupt-<timestamp>` anche per gli altri tipi di reset perche' e' il contratto con
/// LocalSqliteMaintenanceService.CleanupCorruptQuarantines, che e' l'unico a limitarne numero ed eta'.
/// </summary>
public static class LocalIndexQuarantine
{
    private const int TentativiSpostamento = 5;
    private const int PausaFraTentativiMs = 120;

    /// <summary>
    /// Ritorna true solo se, al termine, nessuno dei tre file e' piu' al proprio posto. Il chiamante deve
    /// avere gia' smesso di usare la generazione guasta e atteso i reader: su Windows uno spostamento con
    /// handle aperti fallisce sempre, ed e' esattamente la protezione che vogliamo.
    /// </summary>
    public static bool TrySequester(string databasePath, out string motivo)
    {
        motivo = "";
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            motivo = "Percorso database locale non valido.";
            return false;
        }

        // Le connessioni in pool restano aperte sul file anche quando nessuno le sta usando: senza questo
        // rilascio lo spostamento fallirebbe per sharing violation su un database che nessuno sta leggendo.
        SqliteConnection.ClearAllPools();

        var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var wal = databasePath + "-wal";
        var shm = databasePath + "-shm";

        if (File.Exists(databasePath) && !TryMoveWithRetries(databasePath, databasePath + $".corrupt-{suffix}", out var errore))
        {
            // Il file principale e' ancora in uso: fermarsi qui lascia il workspace in uno stato coerente
            // (DB + WAL + SHM tutti al loro posto) e permette di ritentare, invece di distruggere il set.
            motivo = $"Impossibile isolare il database locale ({Path.GetFileName(databasePath)}): {errore}";
            return false;
        }

        // Il database e' stato spostato: WAL e SHM vanno preservati con esso. Se un companion non si
        // riesce a spostare, tentiamo il rollback dell'intero set; cancellare un WAL qui renderebbe la
        // quarantena irreversibile proprio nel caso in cui un altro processo lo sta ancora usando.
        var spostati = new List<(string Originale, string Quarantena)>
        {
            (databasePath, databasePath + $".corrupt-{suffix}")
        };
        foreach (var companion in new[] { wal, shm })
        {
            if (!File.Exists(companion)) continue;
            var destinazione = companion + $".corrupt-{suffix}";
            if (TryMoveWithRetries(companion, destinazione, out var erroreCompanion))
            {
                spostati.Add((companion, destinazione));
                continue;
            }
            var rollbackCompleto = true;
            foreach (var (originale, quarantena) in spostati.AsEnumerable().Reverse())
                if (File.Exists(quarantena) && !TryMoveWithRetries(quarantena, originale, out _))
                    rollbackCompleto = false;
            motivo = $"Impossibile isolare {Path.GetFileName(companion)}: {erroreCompanion}. " +
                     (rollbackCompleto ? "Set originale ripristinato." : "Rollback incompleto: copie di quarantena conservate, indice non riabilitato.");
            return false;
        }

        return true;
    }

    private static bool TryMoveWithRetries(string from, string to, out string errore)
    {
        errore = "";
        for (var tentativo = 0; tentativo < TentativiSpostamento; tentativo++)
        {
            try
            {
                File.Move(from, to, overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                errore = ex.Message;
                if (tentativo == TentativiSpostamento - 1) break;

                // Un handle chiuso da poco puo' restare visibile al filesystem per qualche decina di ms.
                SqliteConnection.ClearAllPools();
                Thread.Sleep(PausaFraTentativiMs);
            }
        }
        return File.Exists(to) && !File.Exists(from);
    }

    /// <summary>
    /// Recupero all'avvio di una cache SQLite locale dopo un guasto dell'inizializzazione. Una versione o un
    /// layout non interpretabili (<see cref="LocalIndexFaultKind.SchemaIncompatible"/>) NON sono corruzione: il
    /// file e' una cache ricostruibile dai JSON condivisi, quindi viene ELIMINATO senza lasciare copie
    /// `.corrupt-*`. Ogni altro guasto, corruzione compresa, resta in quarantena. Se l'eliminazione non riesce
    /// nulla e' stato cancellato e si ricade sulla quarantena.
    /// </summary>
    public static bool TryRecuperaAllAvvio(string databasePath, LocalIndexFaultKind guasto, out string motivo)
    {
        if (guasto == LocalIndexFaultKind.SchemaIncompatible)
        {
            if (TryEliminaIncompatibile(databasePath, out motivo)) return true;
            CrashLogService.Registra(
                $"Indice locale {Path.GetFileName(databasePath)}: eliminazione della versione non compatibile non riuscita, ricaduta sulla quarantena. {motivo}", null);
        }
        return TrySequester(databasePath, out motivo);
    }

    /// <summary>
    /// Elimina DB, WAL, SHM e journal di una cache con versione non interpretabile, senza copie `.corrupt-*`.
    /// Prima verifica che OGNI file sia apribile in esclusiva e scrivibile: se uno non lo e' non si cancella
    /// nulla e il chiamante ricade sulla quarantena, che conserva il set intero. Poi cancella nell'ordine WAL,
    /// SHM, journal, DB: un WAL o un journal rimasti senza il loro DB verrebbero riapplicati al file nuovo.
    /// </summary>
    public static bool TryEliminaIncompatibile(string databasePath, out string motivo)
    {
        motivo = "";
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            motivo = "Percorso database locale non valido.";
            return false;
        }

        // Come per la quarantena: le connessioni in pool tengono il file aperto e farebbero fallire la cancellazione.
        SqliteConnection.ClearAllPools();

        var file = new[] { databasePath + "-wal", databasePath + "-shm", databasePath + "-journal", databasePath }
            .Where(File.Exists)
            .ToList();

        foreach (var percorso in file)
        {
            if (!TryAperturaEsclusiva(percorso, out var errore))
            {
                motivo = $"Impossibile eliminare {Path.GetFileName(percorso)}: {errore}";
                return false;
            }
        }

        foreach (var percorso in file)
        {
            if (!TryDeleteWithRetries(percorso, out var errore))
            {
                motivo = $"Impossibile eliminare {Path.GetFileName(percorso)}: {errore}";
                return false;
            }
        }
        return true;
    }

    /// <summary>Verifica che nessun altro processo tenga il file aperto e che sia scrivibile: sono le due condizioni
    /// che fanno fallire una cancellazione. Un file in sola lettura non e' transitorio e non viene ritentato.</summary>
    private static bool TryAperturaEsclusiva(string path, out string errore)
    {
        errore = "";
        for (var tentativo = 0; tentativo < TentativiSpostamento; tentativo++)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (UnauthorizedAccessException ex)
            {
                errore = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                errore = ex.Message;
                if (tentativo == TentativiSpostamento - 1) break;
                SqliteConnection.ClearAllPools();
                Thread.Sleep(PausaFraTentativiMs);
            }
        }
        return false;
    }

    private static bool TryDeleteWithRetries(string path, out string errore)
    {
        errore = "";
        for (var tentativo = 0; tentativo < TentativiSpostamento; tentativo++)
        {
            try
            {
                File.Delete(path);
                if (!File.Exists(path)) return true;
            }
            catch (Exception ex)
            {
                errore = ex.Message;
                if (tentativo == TentativiSpostamento - 1) break;
                SqliteConnection.ClearAllPools();
                Thread.Sleep(PausaFraTentativiMs);
            }
        }
        return !File.Exists(path);
    }
}
