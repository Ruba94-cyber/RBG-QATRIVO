using Microsoft.Data.Sqlite;

namespace TestLoom.App.Services;

/// <summary>
/// Protezione contro un WAL SQLite anomalo dopo un'interruzione brutale del processo (crash, kill, blackout)
/// a metà di una transazione molto grande — osservato durante un test prestazionale a 100k record dopo aver
/// terminato forzatamente il processo (feedback utente 2026-09-17: "nessuno perda dati... l'app dovrà essere
/// perfetta ed essere pronta al deploy"). Una transazione mai commessa non fa MAI perdere dati al riavvio:
/// SQLite scarta da solo i frame WAL senza un commit valido. Ma un WAL rimasto molto grande può rendere le
/// operazioni successive innaturalmente lente finché non viene ripulito. Un checkpoint TRUNCATE all'apertura
/// è l'operazione ufficialmente raccomandata da SQLite per questo caso: unisce il WAL nel file principale e
/// lo azzera, senza toccare in alcun modo una transazione incompleta.
/// </summary>
public static class SqliteWalGuard
{
    /// <summary>16 MiB, non centinaia: un WAL grande rallenta OGNI lettura molto prima di diventare enorme,
    /// perché SQLite deve consultare l'indice del WAL per ogni pagina letta. Misurato sull'indice NC a 100k
    /// record (feedback utente 2026-09-17): con un WAL da 138 MB un `SELECT COUNT(*)` costava 162ms, dopo il
    /// checkpoint 4ms — 40 volte più veloce, e quel WAL era comunque sotto la soglia di 200 MB usata prima,
    /// quindi la protezione non scattava mai. In funzionamento normale il checkpoint automatico di SQLite
    /// tiene il WAL intorno ai 4 MB (1000 pagine), quindi questa soglia non scatta per caso: si attiva solo
    /// quando qualcosa ha davvero impedito il checkpoint automatico (crash, kill, lettore rimasto aperto).</summary>
    private const long OversizedThresholdBytes = 16L * 1024 * 1024;

    public static void CheckpointIfOversized(SqliteConnection connection, string databasePath)
    {
        try
        {
            var walPath = databasePath + "-wal";
            if (!File.Exists(walPath) || new FileInfo(walPath).Length < OversizedThresholdBytes) return;
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex) { CrashLogService.Registra("Checkpoint WAL SQLite anomalo", ex); }
    }
}
